using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tentacle.Broker;

/// <summary>
/// Runs the broker's own Kestrel, separate from Jellyfin's: Jellyfin hijacks every
/// WebSocket upgrade on its port. Listens on a unix socket for shims (raw frames,
/// no HTTP) and on a TCP port for agents (WebSockets, and /healthz).
/// </summary>
public sealed partial class BrokerHost : IAsyncDisposable
{
    private readonly Broker _broker;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<BrokerHost> _logger;
    private WebApplication? _app;

    /// <summary>
    /// Initializes a new instance of the <see cref="BrokerHost"/> class.
    /// </summary>
    /// <param name="broker">The broker.</param>
    /// <param name="loggerFactory">The host application's logger factory.</param>
    public BrokerHost(Broker broker, ILoggerFactory loggerFactory)
    {
        _broker = broker;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<BrokerHost>();
    }

    /// <summary>
    /// Gets the error that kept the broker from starting, if any.
    /// </summary>
    public string? StartError { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the agent port speaks TLS (wss://).
    /// </summary>
    public bool Tls { get; private set; }

    /// <summary>
    /// Starts listening. Never throws: a broker that cannot start leaves every job local.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _broker.Options;
        try
        {
            PrepareSocketPath(options.SocketPath);
            Tls = options.Certificate() is not null;

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = typeof(BrokerHost).Assembly.GetName().Name });
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(_loggerFactory);
            builder.Services.AddSingleton(_broker);
            builder.WebHost.UseKestrel(kestrel =>
            {
                kestrel.ListenUnixSocket(options.SocketPath, listen => listen.UseConnectionHandler<ShimConnectionHandler>());
                kestrel.ListenAnyIP(options.AgentPort, listen =>
                {
                    // Decided at start: the port speaks TLS or it does not. The
                    // certificate itself is read per handshake (renewals apply live).
                    if (Tls)
                    {
                        listen.UseHttps(https => https.ServerCertificateSelector = (_, _) => options.Certificate());
                    }
                });
            });

            var app = builder.Build();
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
            app.MapGet("/healthz", () => "ok");
            app.Map("/tentacle/v1/control", _broker.HandleControlAsync);
            app.Map("/tentacle/v1/job/{jobId}", (HttpContext context, string jobId) => _broker.HandleJobAsync(context, jobId));

            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(options.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            }

            _app = app;
            _broker.Listening = true;
            LogStarted(options.SocketPath, options.AgentPort, Tls ? "TLS" : "plain, token in cleartext", options.Placement);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StartError = e.Message;
            LogStartFailed(e, options.SocketPath, options.AgentPort);
        }
    }

    /// <summary>
    /// Stops listening; running jobs end with their connections.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _broker.Listening = false;
        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }

    private static void PrepareSocketPath(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // A socket left by a crashed server would make the bind fail.
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tentacle broker listening on {Socket} (shims) and :{Port} (agents, {Transport}), placement {Placement}")]
    private partial void LogStarted(string socket, int port, string transport, PlacementMode placement);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tentacle broker could not listen on {Socket} / :{Port}; every job runs locally")]
    private partial void LogStartFailed(Exception exception, string socket, int port);

    /// <summary>
    /// Kestrel connection handler for the shim socket.
    /// </summary>
    private sealed class ShimConnectionHandler : ConnectionHandler
    {
        private readonly Broker _broker;

        public ShimConnectionHandler(Broker broker)
        {
            _broker = broker;
        }

        public override async Task OnConnectedAsync(ConnectionContext connection)
        {
            var input = connection.Transport.Input.AsStream();
            var output = connection.Transport.Output.AsStream();
            await using (input.ConfigureAwait(false))
            await using (output.ConfigureAwait(false))
            {
                try
                {
                    var peer = PeerCredentials.Of(connection.Features.Get<IConnectionSocketFeature>()?.Socket);
                    if (peer is null && OperatingSystem.IsLinux())
                    {
                        // Fail closed: without the caller's uid there is no telling it is Jellyfin.
                        _broker.RejectUnidentifiedShim();
                        return;
                    }

                    await _broker.HandleShimAsync(input, output, peer, connection.ConnectionClosed).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException or ConnectionResetException)
                {
                    // The shim went away; HandleShimAsync already accounted for the job.
                }
            }
        }
    }
}
