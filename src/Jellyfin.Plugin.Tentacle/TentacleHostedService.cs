using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tentacle.Protocol;

namespace Jellyfin.Plugin.Tentacle;

/// <summary>
/// Starts and stops the broker with Jellyfin.
/// </summary>
public partial class TentacleHostedService : IHostedService
{
    private readonly TentacleRuntime _runtime;
    private readonly ShimInstaller _shim;
    private readonly ILogger<TentacleHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TentacleHostedService"/> class.
    /// </summary>
    /// <param name="runtime">The runtime.</param>
    /// <param name="shim">Installs the shim.</param>
    /// <param name="logger">The logger.</param>
    public TentacleHostedService(TentacleRuntime runtime, ShimInstaller shim, ILogger<TentacleHostedService> logger)
    {
        _runtime = runtime;
        _shim = shim;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        LogStarting(ProtocolInfo.ProductVersion, ProtocolInfo.Version);

        // Hosted services start before Jellyfin validates ffmpeg: the shim must exist
        // by then when JELLYFIN_FFMPEG / FFMPEG_PATH points at it.
        _shim.Install();
        await _runtime.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => _runtime.StopAsync(cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tentacle {Version} started (protocol {Protocol})")]
    private partial void LogStarting(string version, int protocol);
}
