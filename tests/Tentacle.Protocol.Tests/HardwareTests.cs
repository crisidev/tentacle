using Tentacle.Protocol;
using Xunit;

namespace Tentacle.Protocol.Tests;

public class HardwareTests
{
    // corellia's real QSV command line (FFmpeg.Transcode log, 2026-10-04), shortened.
    private static readonly string[] Qsv =
    [
        "-analyzeduration", "200M", "-f", "matroska", "-init_hw_device", "vaapi=va:/dev/dri/renderD128,driver=iHD",
        "-init_hw_device", "qsv=qs@va", "-filter_hw_device", "qs", "-hwaccel", "vaapi", "-hwaccel_output_format", "vaapi",
        "-i", "file:/data/tv/a.mkv", "-codec:v:0", "h264_qsv", "-vf", "scale_vaapi=w=1280:h=640,hwmap=derive_device=qsv,format=qsv",
        "-f", "hls", "/config/cache/transcodes/x.m3u8",
    ];

    [Theory]
    [InlineData("0x8086", "i915", "intel")]
    [InlineData("0x8086", "xe", "intel")]
    [InlineData("0x1002", "amdgpu", "amd")]
    [InlineData("0x10de", "nvidia", "nvidia")]
    [InlineData("", "v3d", "broadcom")]
    [InlineData("", "", "unknown")]
    public void VendorFromPciIdOrDriver(string pci, string driver, string vendor)
        => Assert.Equal(vendor, Hardware.VendorOf(pci, driver));

    [Theory]
    [InlineData("intel", "i915", "qsv")]
    [InlineData("amd", "amdgpu", "vaapi")]
    [InlineData("nvidia", "nvidia", "nvenc")]
    [InlineData("nvidia", "nouveau", "none")]
    [InlineData("broadcom", "v3d", "none")]
    public void ApiForVendor(string vendor, string driver, string api)
        => Assert.Equal(api, Hardware.ApiFor(vendor, driver));

    [Fact]
    public void ModelFromTheVaapiDriverLine()
    {
        Assert.Equal("AMD Radeon Graphics (renoir)", Hardware.ModelFromVaapi(
            "[AVHWDeviceContext @ 0x5] VAAPI driver: Mesa Gallium driver 25.0.7-2 for AMD Radeon Graphics (radeonsi, renoir, ACO, DRM 3.61, 6.12.107+deb13-amd64).\n"));
        Assert.Equal("Intel GPU (iHD 25.2.6)", Hardware.ModelFromVaapi(
            "[AVHWDeviceContext @ 0x5] VAAPI driver: Intel iHD driver for Intel(R) Gen Graphics - 25.2.6 ().\n"));
        Assert.Equal(string.Empty, Hardware.ModelFromVaapi("Device creation failed: -5."));
    }

    [Fact]
    public void QsvJobsNeedIntel()
    {
        var a = ArgvAnalysis.Analyze("ffmpeg", Qsv);
        Assert.True(a.UsesHardware);
        Assert.Equal("qsv", a.HardwareApi);
        Assert.Equal("intel", a.HardwareVendor);
    }

    [Theory]
    [InlineData("vaapi=va:/dev/dri/renderD128,driver=iHD", "h264_vaapi", "intel")]
    [InlineData("vaapi=va:/dev/dri/renderD128,driver=radeonsi", "h264_vaapi", "amd")]
    [InlineData("vaapi=va:/dev/dri/renderD128", "h264_vaapi", null)]
    [InlineData("cuda=cu:0", "h264_nvenc", "nvidia")]
    public void VaapiNamesItsVendorThroughTheDriver(string device, string codec, string? vendor)
    {
        var a = ArgvAnalysis.Analyze("ffmpeg", ["-init_hw_device", device, "-i", "/data/a.mkv", "-c:v", codec, "-f", "hls", "/config/cache/transcodes/x.m3u8"]);
        Assert.Equal(vendor, a.HardwareVendor);
        Assert.True(a.UsesHardware);
    }

    [Fact]
    public void SoftwareJobsNeedNoVendor()
    {
        var a = ArgvAnalysis.Analyze("ffmpeg", ["-i", "/data/a.mkv", "-codec:v:0", "copy", "-codec:a:0", "libfdk_aac", "-f", "hls", "/config/cache/transcodes/x.m3u8"]);
        Assert.False(a.UsesHardware);
        Assert.Null(a.HardwareApi);
        Assert.Null(a.HardwareVendor);
    }

    [Fact]
    public void PointAtMovesOnlyTheDeviceOptions()
    {
        var moved = Hardware.PointAt(Qsv, "/dev/dri/renderD129");
        Assert.Equal("vaapi=va:/dev/dri/renderD129,driver=iHD", moved[5]);
        Assert.Equal(Qsv.Length, moved.Length);
        for (var i = 0; i < Qsv.Length; i++)
        {
            if (i != 5)
            {
                Assert.Equal(Qsv[i], moved[i]);
            }
        }

        // libva picks by vendor when the device is empty: name it.
        Assert.Equal("vaapi=va:/dev/dri/renderD129,vendor_id=0x8086,driver=iHD", Hardware.PointAt(["-init_hw_device", "vaapi=va:,vendor_id=0x8086,driver=iHD"], "/dev/dri/renderD129")[1]);
        Assert.Equal("/dev/dri/renderD129", Hardware.PointAt(["-hwaccel_device", "/dev/dri/renderD128"], "/dev/dri/renderD129")[1]);

        // A path that merely looks like a device elsewhere stays.
        Assert.Equal("/data/dev/dri/renderD128.mkv", Hardware.PointAt(["-i", "/data/dev/dri/renderD128.mkv"], "/dev/dri/renderD129")[1]);

        // NVIDIA tentacles are chosen by index, not by render node.
        Assert.Equal(Qsv, Hardware.PointAt(Qsv, "/dev/nvidia1"));
    }

    [Fact]
    public void ProbesFollowTheApi()
    {
        Assert.Contains("hevc_vaapi", Hardware.EncodeProbe("vaapi", "/dev/dri/renderD128", "hevc10")!);
        Assert.Contains("main10", Hardware.EncodeProbe("vaapi", "/dev/dri/renderD128", "hevc10")!);
        Assert.Contains("av1_qsv", Hardware.EncodeProbe("qsv", "/dev/dri/renderD128", "av1")!);
        Assert.Contains("cuda=cu:1", Hardware.EncodeProbe("nvenc", "/dev/nvidia1", "h264")!);
        Assert.Null(Hardware.EncodeProbe("none", "/dev/dri/renderD128", "h264"));
        Assert.Contains("hwdownload,format=nv12|p010le", Hardware.DecodeProbe("vaapi", "/dev/dri/renderD128", "/tmp/s.mkv")!);
        Assert.Contains("vaapi=va:/dev/dri/renderD128,driver=iHD", Hardware.DecodeProbe("qsv", "/dev/dri/renderD128", "/tmp/s.mkv")!);
    }

    [Fact]
    public void HelloCarriesTheGpu()
    {
        var hello = new Hello { Node = "coruscant", Host = "coruscant", Arch = "x64", DetectOnly = true, Gpu = new GpuInfo { Device = "/dev/dri/renderD128", Vendor = "amd", Api = "vaapi", Capabilities = ["encode:h264"] } };
        var back = Frame.Json(FrameType.Hello, hello, ProtocolJson.Default.Hello).Read(ProtocolJson.Default.Hello);
        Assert.Equal("amd", back.Gpu!.Vendor);
        Assert.True(back.DetectOnly);
        Assert.Equal(["encode:h264"], back.Gpu.Capabilities);

        // An agent before 0.7 sends none of it.
        var old = Frame.Json(FrameType.Hello, new Hello { Node = "old" }, ProtocolJson.Default.Hello).Read(ProtocolJson.Default.Hello);
        Assert.Null(old.Gpu);
        Assert.Equal(string.Empty, old.Arch);
    }
}
