using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Tentacle.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;
using Tentacle.Broker;
using Tentacle.Protocol;

namespace Jellyfin.Plugin.Tentacle;

/// <summary>
/// Wires the broker into Jellyfin: its options come from the plugin configuration
/// and from Jellyfin's own paths; its lifetime follows Jellyfin's host.
/// </summary>
public sealed partial class TentacleRuntime : IAsyncDisposable
{
    private static readonly TimeSpan RootsTtl = TimeSpan.FromSeconds(30);
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IApplicationPaths _paths;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<TentacleRuntime> _logger;
    private readonly Lock _rootsLock = new();
    private readonly CancellationTokenSource _stopping = new();
    private BrokerCertificate? _certificate;
    private IReadOnlyList<SharedRoot> _roots = [];
    private DateTimeOffset _rootsAt = DateTimeOffset.MinValue;
    private string _serverFfmpeg = string.Empty;
    private volatile IReadOnlyList<GpuInfo>? _serverGpus;

    /// <summary>
    /// Initializes a new instance of the <see cref="TentacleRuntime"/> class.
    /// </summary>
    /// <param name="configurationManager">The server configuration.</param>
    /// <param name="paths">The application paths.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaEncoder">The media encoder.</param>
    /// <param name="attributor">Finds who and what each job is for.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public TentacleRuntime(
        IServerConfigurationManager configurationManager,
        IApplicationPaths paths,
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        JobAttributor attributor,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(attributor);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _configurationManager = configurationManager;
        _paths = paths;
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _logger = loggerFactory.CreateLogger<TentacleRuntime>();

        var config = Config;
        Options = new BrokerOptions
        {
            SocketPath = Environment.GetEnvironmentVariable("TENTACLE_SOCKET") ?? config.SocketPath,
            AgentPort = config.AgentPort,
            Token = EnsureToken(config),
            Certificate = () => _certificate?.Current,
            Placement = config.Placement,
            StrictFfmpegVersion = config.StrictFfmpegVersion,
            BackgroundNice = Math.Clamp(config.BackgroundNice, 0, 19),
            LocalSlots = Math.Clamp(config.LocalSlots, 0, 64),
            LocalWeight = config.LocalWeight > 0 ? config.LocalWeight : 1,
            LocalFallback = config.LocalFallback,
            MetricsNameViewers = config.MetricsNameViewers,
            VerifyInterval = TimeSpan.FromMinutes(Math.Max(1, config.VerifyIntervalMinutes)),
            ServerUid = PeerCredentials.CurrentUid,
            SharedRoots = SharedRoots,
            HardwareTest = () => BuildHardwareTest(_configurationManager.GetEncodingOptions()),
            Attribute = attributor.Attribute,
        };

        ApplyPreviousToken(config);

        // Jellyfin's own registry: the metrics show up in its /metrics (EnableMetrics).
        Broker = new Broker(Options, loggerFactory.CreateLogger<Broker>(), new TentacleMetrics(Prometheus.Metrics.DefaultRegistry));
        Host = new BrokerHost(Broker, loggerFactory);
    }

    /// <summary>Gets the broker options.</summary>
    public BrokerOptions Options { get; }

    /// <summary>Gets the broker.</summary>
    public Broker Broker { get; }

    /// <summary>Gets the broker's listener.</summary>
    public BrokerHost Host { get; }

    /// <summary>Gets a value indicating whether this server's GPUs have been measured.</summary>
    public bool ServerGpusProbed => _serverGpus is not null;

    /// <summary>Gets why the TLS certificate could not be loaded, if it could not (the broker then does not start).</summary>
    public string? CertificateError { get; private set; }

    /// <summary>Gets the agent port's certificate, when it speaks TLS.</summary>
    public BrokerCertificate? Certificate => Host.Tls ? _certificate : null;

    /// <summary>Gets a value indicating whether the token comes from TENTACLE_TOKEN (and so cannot be rotated here).</summary>
    public static bool TokenFromEnvironment => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TENTACLE_TOKEN"));

    /// <summary>Gets a value indicating whether Jellyfin serves /metrics, where the Tentacle metrics are.</summary>
    public bool MetricsEnabled => _configurationManager.Configuration.EnableMetrics;

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Starts the broker.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        _configurationManager.NamedConfigurationUpdated += OnNamedConfigurationUpdated;
        Options.ServerFfmpegVersion = ServerFfmpegVersion;
        try
        {
            _certificate = LoadCertificate(Config);
            if (_certificate is not null)
            {
                LogTls(_certificate.SelfSigned ? "self-signed" : "from files", _certificate.Current.Subject, _certificate.Current.NotAfter, _certificate.Fingerprint);
            }
        }
        catch (Exception e) when (e is IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException or ArgumentException)
        {
            // Never fall back to cleartext: no broker, every job runs locally.
            CertificateError = $"TLS certificate: {e.Message}";
            LogCertificateFailed(e);
            return;
        }

        await Host.StartAsync(cancellationToken).ConfigureAwait(false);
        LogRoots(string.Join(", ", SharedRoots().Select(r => r.Writable ? r.Path + " (rw)" : r.Path)));
        _ = Task.Run(() => ProbeServerGpusAsync(_stopping.Token), CancellationToken.None);
    }

    /// <summary>
    /// The GPU Jellyfin's hardware acceleration uses on this server, measured like a
    /// tentacle's: the device its encoding settings name, else the first one found.
    /// </summary>
    /// <returns>The GPU; null when there is none or it is not measured yet.</returns>
    public GpuInfo? ServerGpu()
    {
        var gpus = _serverGpus;
        if (gpus is null || gpus.Count == 0)
        {
            return null;
        }

        var options = _configurationManager.GetEncodingOptions();
        var wanted = options.HardwareAccelerationType == HardwareAccelerationType.qsv ? options.QsvDevice : options.VaapiDevice;
        return gpus.FirstOrDefault(g => string.Equals(g.Device, wanted, StringComparison.Ordinal)) ?? gpus[0];
    }

    /// <summary>
    /// Measures this server's GPUs once, after Jellyfin has settled, with the real
    /// ffmpeg (not the shim): a few seconds of short encodes and decodes per GPU.
    /// </summary>
    private async Task ProbeServerGpusAsync(CancellationToken stop)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stop).ConfigureAwait(false);
            var realDir = Environment.GetEnvironmentVariable("TENTACLE_REAL_DIR") ?? "/usr/lib/jellyfin-ffmpeg";
            var ffmpeg = Path.Combine(realDir, "ffmpeg");
            if (!File.Exists(ffmpeg))
            {
                ffmpeg = _mediaEncoder.EncoderPath;
            }

            var gpus = await GpuProbe.DetectAsync(ffmpeg, Environment.GetEnvironmentVariable("TENTACLE_GPUS"), m => LogGpuProbe(m), stop).ConfigureAwait(false);
            foreach (var gpu in gpus)
            {
                LogGpuProbe(GpuProbe.Describe(gpu));
            }

            _serverGpus = gpus;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Stops the broker.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        _configurationManager.NamedConfigurationUpdated -= OnNamedConfigurationUpdated;
        await _stopping.CancelAsync().ConfigureAwait(false);
        await Host.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _stopping.Dispose();
        return Host.DisposeAsync();
    }

    /// <summary>
    /// The directories every tentacle should share with the server at the same path:
    /// where ffmpeg writes (transcodes, temp, extracted subtitles and attachments,
    /// concat lists) and what it only reads (libraries, the fallback font, extra roots).
    /// Each tentacle proves which of them it really shares.
    /// </summary>
    /// <returns>The roots.</returns>
    public IReadOnlyList<SharedRoot> SharedRoots()
    {
        lock (_rootsLock)
        {
            if (DateTimeOffset.UtcNow - _rootsAt < RootsTtl)
            {
                return _roots;
            }

            var roots = new List<SharedRoot>
            {
                new(_configurationManager.GetTranscodePath(), true, true),
                new(_paths.TempDirectory, true, true),
                new(Path.Combine(_paths.DataPath, "subtitles"), true, true),
                new(Path.Combine(_paths.DataPath, "attachments"), true, true),

                // Only DVD/Blu-ray folders use concat lists.
                new(Path.Combine(_paths.CachePath, "concat"), true),
            };
            var fallbackFont = _configurationManager.GetEncodingOptions().FallbackFontPath;
            if (!string.IsNullOrEmpty(fallbackFont))
            {
                roots.Add(new SharedRoot(fallbackFont, false));
            }

            // Media libraries only: Collections (box sets) and playlists are Jellyfin's own
            // folders under data/, never an ffmpeg input.
            roots.AddRange(_libraryManager.GetVirtualFolders()
                .Where(f => !IsMetadataOnly(f.CollectionType?.ToString()))
                .SelectMany(f => f.Locations)
                .Select(l => new SharedRoot(l, false)));
            roots.AddRange(Config.ExtraRoots.Select(r => new SharedRoot(r, false)));
            _roots = roots
                .Where(r => !string.IsNullOrWhiteSpace(r.Path))

                // An optional directory this server does not have is not in use here
                // (no DVD rips → no concat lists): nothing to share, nothing to check.
                .Where(r => r.Required || Directory.Exists(r.Path) || File.Exists(r.Path))
                .Select(r => r with { Path = r.Path.TrimEnd('/') })
                .DistinctBy(r => r.Path, StringComparer.Ordinal)
                .ToArray();
            _rootsAt = DateTimeOffset.UtcNow;
            return _roots;
        }
    }

    /// <summary>
    /// The VAAPI device QSV is derived from, opened the way Jellyfin does on Linux
    /// (EncodingHelper.GetQsvDeviceArgs): the iHD driver on i915, by render node or by vendor.
    /// </summary>
    /// <param name="renderNode">The configured QSV device, if any.</param>
    /// <returns>The -init_hw_device value.</returns>
    public static string QsvVaapiDevice(string? renderNode) => string.IsNullOrWhiteSpace(renderNode)
        ? "vaapi=va:,vendor_id=0x8086,driver=iHD,kernel_driver=i915"
        : $"vaapi=va:{renderNode},driver=iHD,kernel_driver=i915";

    /// <summary>
    /// The hardware self-test for the server's encoding settings: initialise the same
    /// device type Jellyfin will use and push a few frames through it (and through
    /// the hardware H.264 encoder when hardware encoding is on).
    /// </summary>
    /// <param name="options">The encoding options.</param>
    /// <returns>The test, or null when the server encodes in software.</returns>
    public static HardwareTest? BuildHardwareTest(EncodingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var input = new[] { "-hide_banner", "-v", "error" };
        var source = new[] { "-f", "lavfi", "-i", "testsrc2=s=320x180:r=10:d=1" };
        var encode = options.EnableHardwareEncoding;
        var device = static (string? d) => string.IsNullOrWhiteSpace(d) ? "/dev/dri/renderD128" : d;
        string[] Encoder(string codec) => encode ? ["-c:v", codec] : [];
        string[]? args = options.HardwareAccelerationType switch
        {
            HardwareAccelerationType.qsv =>
            [
                .. input, "-init_hw_device", QsvVaapiDevice(options.QsvDevice), "-init_hw_device", "qsv=qs@va", "-filter_hw_device", "qs",
                .. source, "-vf", "hwupload=extra_hw_frames=16,format=qsv", .. Encoder("h264_qsv"), "-f", "null", "-",
            ],
            HardwareAccelerationType.vaapi =>
            [
                .. input, "-init_hw_device", $"vaapi=va:{device(options.VaapiDevice)}", "-filter_hw_device", "va",
                .. source, "-vf", "format=nv12,hwupload", .. Encoder("h264_vaapi"), "-f", "null", "-",
            ],
            HardwareAccelerationType.nvenc =>
            [
                .. input, "-init_hw_device", "cuda=cu:0", "-filter_hw_device", "cu",
                .. source, "-vf", "format=nv12,hwupload_cuda", .. Encoder("h264_nvenc"), "-f", "null", "-",
            ],
            _ => null,
        };
        if (args is null)
        {
            return null;
        }

        // Which GPUs can run the command lines Jellyfin builds: QSV is Intel, NVENC is
        // NVIDIA; VAAPI is whatever the server's own device is.
        var type = options.HardwareAccelerationType.ToString();
        var vendor = Hardware.VendorOfApi(type) ?? Hardware.VendorOfRenderNode(device(options.VaapiDevice));
        return new HardwareTest(type, args, vendor);
    }

    /// <summary>
    /// Replaces the token, keeping the old one valid for <paramref name="overlap"/> so
    /// agents can be moved over one by one. Agents still on the old token when the
    /// overlap ends are disconnected.
    /// </summary>
    /// <param name="overlap">How long the old token keeps working.</param>
    /// <returns>The new token, or null when TENTACLE_TOKEN pins it.</returns>
    public string? RotateToken(TimeSpan overlap)
    {
        if (TokenFromEnvironment || Plugin.Instance is not { } plugin)
        {
            return null;
        }

        var config = plugin.Configuration;
        config.PreviousToken = config.Token;
        config.PreviousTokenExpires = DateTime.UtcNow + overlap;
        config.Token = NewToken();
        plugin.UpdateConfiguration(config);
        LogRotated(config.PreviousTokenExpires.Value);
        return config.Token;
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private BrokerCertificate? LoadCertificate(PluginConfiguration config)
    {
        var envCert = Environment.GetEnvironmentVariable("TENTACLE_TLS_CERT");
        if (!string.IsNullOrWhiteSpace(envCert))
        {
            var envKey = Environment.GetEnvironmentVariable("TENTACLE_TLS_KEY");
            return BrokerCertificate.FromFiles(envCert, string.IsNullOrWhiteSpace(envKey) ? null : envKey);
        }

        return config.Tls switch
        {
            TlsMode.Off => null,
            TlsMode.Files => BrokerCertificate.FromFiles(config.TlsCertificatePath, string.IsNullOrWhiteSpace(config.TlsKeyPath) ? null : config.TlsKeyPath),
            _ => BrokerCertificate.SelfSignedAt(Path.Combine(_paths.DataPath, "tentacle", "broker.pfx"), Environment.MachineName),
        };
    }

    private void ApplyPreviousToken(PluginConfiguration config)
    {
        var env = Environment.GetEnvironmentVariable("TENTACLE_PREVIOUS_TOKEN");
        if (!string.IsNullOrWhiteSpace(env))
        {
            // Rotating through the environment (a k8s Secret): accepted while set.
            Options.PreviousToken = env.Trim();
            Options.PreviousTokenExpires = null;
            return;
        }

        Options.PreviousToken = TokenFromEnvironment ? string.Empty : config.PreviousToken;
        Options.PreviousTokenExpires = config.PreviousTokenExpires is { } until ? new DateTimeOffset(DateTime.SpecifyKind(until, DateTimeKind.Utc)) : null;
    }

    private static bool IsMetadataOnly(string? collectionType)
        => string.Equals(collectionType, "boxsets", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collectionType, "playlists", StringComparison.OrdinalIgnoreCase);

    private static string EnsureToken(PluginConfiguration config)
    {
        var env = Environment.GetEnvironmentVariable("TENTACLE_TOKEN");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        if (string.IsNullOrEmpty(config.Token) && Plugin.Instance is { } plugin)
        {
            config.Token = NewToken();
            plugin.SaveConfiguration();
        }

        return config.Token;
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        if (e is PluginConfiguration config)
        {
            if (config.Placement != Options.Placement)
            {
                LogPlacementChanged(Options.Placement, config.Placement);
                Options.Placement = config.Placement;
            }

            // Live: a changed token drops agents still on the old one at their next
            // heartbeat, unless it is kept as the previous token (rotation).
            if (!TokenFromEnvironment && config.Token.Length > 0)
            {
                Options.Token = config.Token;
            }

            ApplyPreviousToken(config);
            Options.StrictFfmpegVersion = config.StrictFfmpegVersion;
            Options.BackgroundNice = Math.Clamp(config.BackgroundNice, 0, 19);
            Options.LocalSlots = Math.Clamp(config.LocalSlots, 0, 64);
            Options.LocalWeight = config.LocalWeight > 0 ? config.LocalWeight : 1;
            Options.LocalFallback = config.LocalFallback;
            Options.MetricsNameViewers = config.MetricsNameViewers;
            Options.VerifyInterval = TimeSpan.FromMinutes(Math.Max(1, config.VerifyIntervalMinutes));
            Broker.ReverifyAll();
        }

        lock (_rootsLock)
        {
            _rootsAt = DateTimeOffset.MinValue;
        }
    }

    private void OnNamedConfigurationUpdated(object? sender, ConfigurationUpdateEventArgs e)
    {
        if (string.Equals(e.Key, "encoding", StringComparison.OrdinalIgnoreCase))
        {
            LogEncodingChanged();
            Broker.ReverifyAll();
        }
    }

    /// <summary>
    /// The server's `ffmpeg -version` first line, cached once known. Jellyfin sets
    /// its ffmpeg path after the plugin's hosted service starts, so ask lazily.
    /// </summary>
    private string ServerFfmpegVersion()
    {
        if (_serverFfmpeg.Length > 0 || string.IsNullOrEmpty(_mediaEncoder.EncoderPath))
        {
            return _serverFfmpeg;
        }

        try
        {
            var start = new ProcessStartInfo(_mediaEncoder.EncoderPath, "-version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(start);
            if (process is not null)
            {
                _serverFfmpeg = (process.StandardOutput.ReadLine() ?? string.Empty).Trim();
                process.WaitForExit(5000);
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            _serverFfmpeg = string.Empty;
        }

        return _serverFfmpeg;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tentacle agent port TLS certificate ({Kind}): {Subject}, valid until {NotAfter}, SHA-256 fingerprint {Fingerprint}")]
    private partial void LogTls(string kind, string subject, DateTime notAfter, string fingerprint);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tentacle could not load its TLS certificate: the broker does not start, every job runs locally")]
    private partial void LogCertificateFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tentacle on this server: {Detail}")]
    private partial void LogGpuProbe(string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tentacle token rotated; the previous one works until {Until:u}")]
    private partial void LogRotated(DateTime until);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tentacle shared roots: {Roots}")]
    private partial void LogRoots(string roots);

    [LoggerMessage(Level = LogLevel.Information, Message = "Encoding settings changed: tentacles verify again")]
    private partial void LogEncodingChanged();

    [LoggerMessage(Level = LogLevel.Information, Message = "Tentacle placement changed from {Old} to {New}")]
    private partial void LogPlacementChanged(PlacementMode old, PlacementMode @new);
}
