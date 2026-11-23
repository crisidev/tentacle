using System;
using System.Collections.Generic;
using System.Diagnostics;
using Tentacle.Protocol;
using Xunit;

namespace Tentacle.Protocol.Tests;

/// <summary>
/// A tentacle's GPU facts are remote input: the server puts its device into rebuilt
/// command lines, so only what an agent's probe produces is accepted.
/// </summary>
public class UntrustedGpuTests
{
    private static GpuInfo Gpu(string vendor, string driver, string api, string device, params string[] capabilities)
        => new() { Vendor = vendor, Driver = driver, Api = api, Device = device, Capabilities = capabilities };

    [Fact]
    public void WhatProbesReportIsAccepted()
    {
        Assert.Null(Gpu("intel", "i915", "qsv", "/dev/dri/renderD128", "encode:h264", "decode:hevc10").Problem());
        Assert.Null(Gpu("intel", "xe", "qsv", "/dev/dri/renderD129").Problem());
        Assert.Null(Gpu("amd", "amdgpu", "vaapi", "/dev/dri/renderD128", "encode:hevc", "decode:vp9").Problem());
        Assert.Null(Gpu("nvidia", "nvidia", "nvenc", "/dev/nvidia0", "encode:av1").Problem());
        Assert.Null(Gpu("broadcom", "v3d", Hardware.NoApi, "/dev/dri/renderD128").Problem());
        Assert.Null(Gpu("unknown", string.Empty, Hardware.NoApi, string.Empty).Problem());
    }

    [Theory]
    [InlineData("/dev/dri/renderD128 -i /etc/passwd")]
    [InlineData("/dev/dri/renderD128,driver=iHD")]
    [InlineData("../dev/dri/renderD128")]
    [InlineData("/dev/dri/../../etc/passwd")]
    [InlineData("/dev/dri/renderD128\n")]
    [InlineData("/dev/dri/renderD128\"")]
    [InlineData("/dev/dri/renderD12 8")]
    [InlineData("/dev/dri/card0")]
    [InlineData("/dev/dri/renderD")]
    [InlineData("")]
    public void AnythingButARenderNodeIsRefused(string device)
    {
        Assert.Contains("not a render node", Gpu("amd", "amdgpu", "vaapi", device).Problem(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/dev/nvidia0 -i /etc/passwd")]
    [InlineData("/dev/dri/renderD128")]
    [InlineData("0")]
    public void NvidiaNeedsItsOwnDeviceNode(string device)
    {
        Assert.NotNull(Gpu("nvidia", "nvidia", "nvenc", device).Problem());
    }

    [Theory]
    [InlineData("amd", "amdgpu", "vaapi; rm -rf /")]
    [InlineData("amd", "amdgpu", "amf")]
    [InlineData("amd", "amdgpu", "cuda")]
    [InlineData("amd", "amdgpu", "nvenc")]
    [InlineData("evil", "amdgpu", "vaapi")]
    [InlineData("amd", "radeon", "vaapi")]
    [InlineData("intel", "i915", "vaapi")]
    [InlineData("nvidia", "nouveau", "nvenc")]
    public void TheApiMustBeTheOneItsVendorUses(string vendor, string driver, string api)
    {
        Assert.NotNull(Gpu(vendor, driver, api, "/dev/dri/renderD128").Problem());
    }

    [Fact]
    public void UnknownCapabilitiesAreRefused()
    {
        Assert.Contains("unknown capability", Gpu("amd", "amdgpu", "vaapi", "/dev/dri/renderD128", "encode:h264 -i x").Problem(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReasonsAreSafeToLog()
    {
        var why = Gpu("amd", "amdgpu", "vaapi", "/dev/dri/renderD128\n\u001b[31mfake log line" + new string('x', 200)).Problem()!;
        Assert.DoesNotContain('\n', why);
        Assert.DoesNotContain('\u001b', why);
        Assert.True(why.Length < 160, why);
    }

    [Theory]
    [InlineData("plain words")]
    [InlineData("-i file:\"/data/movies/Test (2026)/Test (2026).mkv\" -y \"/out/x.m3u8\"")]
    [InlineData("-vf \"subtitles=f='/a b/c.ass':fontsdir='/f',hwupload\" -x")]
    [InlineData("say \"\\\"hi\\\"\" a\\\\\"b c\" d\\e \"\" end")]
    [InlineData("tab\tseparated  and   spaced")]
    [InlineData("\"in \"\"quotes\"\" doubled\" x")]
    public void ArgumentsSplitAsDotNetStartsAProcess(string arguments)
    {
        // The real thing: .NET starts printf with this Arguments string, and printf
        // prints each argv entry it received.
        var start = new ProcessStartInfo("printf") { RedirectStandardOutput = true };
        start.Arguments = "\"%s\\0\" " + arguments;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var received = new List<string>(output.Split('\0'));
        received.RemoveAt(received.Count - 1);

        // printf reuses its format for every argument after the first (the format).
        Assert.Equal(received, ProcessArguments.Split(arguments));
    }
}
