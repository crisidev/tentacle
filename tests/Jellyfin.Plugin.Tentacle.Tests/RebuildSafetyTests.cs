using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Tentacle.CrossVendor;
using MediaBrowser.Model.Entities;
using Tentacle.Protocol;
using Xunit;
using EncodingOptions = MediaBrowser.Model.Configuration.EncodingOptions;

namespace Jellyfin.Plugin.Tentacle.Tests;

/// <summary>
/// What a tentacle reports never gets into a command line unchecked, and a rebuilt
/// command line differs from Jellyfin's only in the GPU's parts.
/// </summary>
public class RebuildSafetyTests
{
    private static GpuInfo Amd(string device) => new()
    {
        Device = device,
        Vendor = Hardware.Amd,
        Driver = "amdgpu",
        Api = "vaapi",
        Capabilities = ["encode:h264", "decode:h264"],
    };

    private static string[] Fixture(string name)
        => ProcessArguments.Split(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".args")).TrimEnd()).ToArray();

    // The recorded QSV job, rebuilt for an AMD GPU by hand: what a clean rebuild looks like.
    private static string[] Rebuilt()
    {
        var args = Fixture("hls-h264_qsv").ToList();
        int At(string option) => args.IndexOf(option);
        args.RemoveRange(At("-init_hw_device"), 6);
        args.InsertRange(At("-hwaccel"), ["-init_hw_device", "vaapi=va:/dev/dri/renderD129", "-filter_hw_device", "va"]);
        args[At("-codec:v:0") + 1] = "h264_vaapi";
        args.RemoveRange(At("-low_power"), 6);
        args.InsertRange(At("-b:v"), ["-rc_mode", "VBR"]);
        args[At("-vf") + 1] = "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,scale_vaapi=format=nv12:extra_hw_frames=24";
        return args.ToArray();
    }

    [Theory]
    [InlineData("/dev/dri/renderD128 -i /etc/passwd")]
    [InlineData("/dev/dri/renderD128,driver=iHD:/x")]
    [InlineData("../dev/dri/renderD128")]
    [InlineData("/dev/dri/renderD128\" -y \"/config/x")]
    [InlineData("/dev/dri/renderD128\n-i /etc/shadow")]
    public void AMaliciousDeviceNeverReachesACommandLine(string device)
    {
        Assert.Null(WorkerEncodingOptions.TypeOf(Amd(device)));
        Assert.Null(WorkerEncodingOptions.For(new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.qsv }, Amd(device)));
        Assert.Null(TentacleRuntime.BuildForeignHardwareTest(Amd(device)));
    }

    [Fact]
    public void UnknownVendorsAndApisAreNotDriven()
    {
        var evil = Amd("/dev/dri/renderD128");
        evil.Vendor = "evil";
        Assert.Null(WorkerEncodingOptions.TypeOf(evil));
        var amf = Amd("/dev/dri/renderD128");
        amf.Api = "amf";
        Assert.Null(WorkerEncodingOptions.TypeOf(amf));
        var cuda = new GpuInfo { Vendor = Hardware.Nvidia, Driver = "nvidia", Api = "cuda", Device = "/dev/nvidia0" };
        Assert.Null(WorkerEncodingOptions.TypeOf(cuda));

        // What a probe really reports is.
        Assert.Equal(HardwareAccelerationType.vaapi, WorkerEncodingOptions.TypeOf(Amd("/dev/dri/renderD129")));
        Assert.NotNull(TentacleRuntime.BuildForeignHardwareTest(Amd("/dev/dri/renderD129")));
        Assert.Equal(HardwareAccelerationType.nvenc, WorkerEncodingOptions.TypeOf(new GpuInfo { Vendor = Hardware.Nvidia, Driver = "nvidia", Api = "nvenc", Device = "/dev/nvidia0" }));
    }

    [Fact]
    public void ACleanRebuildChangesOnlyTheGpusParts()
    {
        Assert.Null(CommandRebuilder.WhatChanged(Fixture("hls-h264_qsv"), Rebuilt()));
    }

    [Fact]
    public void ARebuildThatChangesAnInputIsNotUsed()
    {
        var args = Rebuilt();
        args[Array.IndexOf(args, "-i") + 1] = "file:/etc/passwd";
        Assert.Equal("inputs", CommandRebuilder.WhatChanged(Fixture("hls-h264_qsv"), args));

        // Nor one that adds an input, even hidden behind a GPU option's value.
        var added = Rebuilt().ToList();
        added.InsertRange(added.IndexOf("-vf"), ["-i", "/etc/shadow"]);
        Assert.Equal("inputs", CommandRebuilder.WhatChanged(Fixture("hls-h264_qsv"), added.ToArray()));
    }

    [Fact]
    public void ARebuildThatChangesTheOutputIsNotUsed()
    {
        var args = Rebuilt();
        args[^1] = "/config/data/data/jellyfin.db";
        Assert.Equal("output", CommandRebuilder.WhatChanged(Fixture("hls-h264_qsv"), args));
    }

    [Fact]
    public void ARebuildThatTouchesAnotherFileIsNotUsed()
    {
        var args = Rebuilt();
        var vf = Array.IndexOf(args, "-vf") + 1;
        args[vf] += ",subtitles=f=/etc/shadow";
        Assert.Equal("paths", CommandRebuilder.WhatChanged(Fixture("hls-h264_qsv"), args));
    }

    [Fact]
    public void ARebuildThatChangesAnythingButTheGpusPartsIsNotUsed()
    {
        // Streams, audio, the muxer: Jellyfin's, untouched.
        var maps = Rebuilt();
        maps[Array.IndexOf(maps, "-map") + 1] = "0:2";
        Assert.Equal("options", CommandRebuilder.WhatChanged(Fixture("hls-h264_qsv"), maps));

        var audio = Rebuilt();
        audio[Array.IndexOf(audio, "-codec:a:0") + 1] = "copy";
        Assert.Equal("options", CommandRebuilder.WhatChanged(Fixture("hls-h264_qsv"), audio));

        var extra = Rebuilt().Append("-report").ToArray();
        Assert.NotNull(CommandRebuilder.WhatChanged(Fixture("hls-h264_qsv"), extra));
    }

    [Fact]
    public void ControlCharactersAreNotUsed()
    {
        var args = Rebuilt();
        args[Array.IndexOf(args, "-rc_mode") + 1] = "VBR\n-i";
        Assert.Equal("control", CommandRebuilder.WhatChanged(Fixture("hls-h264_qsv"), args));
    }
}
