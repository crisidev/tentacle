using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Tentacle.Broker;
using Tentacle.Protocol;
using Xunit;

namespace Tentacle.Broker.Tests;

public sealed class SecurityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tentacle-sec-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void TokensMatchCurrentThenPreviousUntilItExpires()
    {
        var now = DateTimeOffset.UtcNow;
        var options = new BrokerOptions { Token = "new", PreviousToken = "old", PreviousTokenExpires = now.AddMinutes(5) };
        Assert.Equal(TokenMatch.Current, options.Match("new", now));
        Assert.Equal(TokenMatch.Previous, options.Match("old", now));
        Assert.Equal(TokenMatch.None, options.Match("old", now.AddMinutes(6)));
        Assert.Equal(TokenMatch.None, options.Match("other", now));
        Assert.Equal(TokenMatch.None, options.Match(string.Empty, now));
        Assert.Equal(TokenMatch.None, options.Match(null, now));

        options.PreviousTokenExpires = null;
        Assert.Equal(TokenMatch.Previous, options.Match("old", now.AddYears(1)));
        Assert.Equal(TokenMatch.None, new BrokerOptions().Match(string.Empty, now));
    }

    [Fact]
    public void SelfSignedCertificateIsKeptAcrossRestarts()
    {
        var path = Path.Combine(_dir, "tentacle", "broker.pfx");
        var first = BrokerCertificate.SelfSignedAt(path, "corellia");
        var again = BrokerCertificate.SelfSignedAt(path, "corellia");
        Assert.True(first.SelfSigned);
        Assert.True(first.Current.HasPrivateKey);
        Assert.Equal(first.Fingerprint, again.Fingerprint);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            Assert.Equal(first.Fingerprint, BrokerCertificate.SelfSignedAt(path, "corellia").Fingerprint);
        }

        Assert.Equal(64, first.Fingerprint.Length);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public void CertificateFilesAreLoaded()
    {
        var (certPath, keyPath, _) = WriteCaSignedCertificate("broker.example");
        var source = BrokerCertificate.FromFiles(certPath, keyPath);
        Assert.False(source.SelfSigned);
        Assert.True(source.Current.HasPrivateKey);
        Assert.Contains("broker.example", source.Current.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentPortSpeaksTlsAndDropsSessionsWhoseTokenIsRetired()
    {
        var certificate = BrokerCertificate.SelfSignedAt(Path.Combine(_dir, "broker.pfx"), "localhost");
        var port = FreePort();
        var options = new BrokerOptions
        {
            SocketPath = Path.Combine(_dir, "broker.sock"),
            AgentPort = port,
            Token = "new",
            PreviousToken = "old",
            Heartbeat = TimeSpan.FromMilliseconds(200),
            Certificate = () => certificate.Current,
        };
        var broker = new Broker(options, NullLogger<Broker>.Instance);
        await using var host = new BrokerHost(broker, NullLoggerFactory.Instance);
        var ct = TestContext.Current.CancellationToken;
        await host.StartAsync(ct);
        Assert.Null(host.StartError);
        Assert.True(host.Tls);

        // The wrong certificate is refused by a pinning client; plain ws:// gets nowhere.
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(port, "new", new string('0', 64), ct));
        using (var plain = new ClientWebSocket())
        {
            plain.Options.SetRequestHeader("Authorization", "Bearer new");
            await Assert.ThrowsAnyAsync<Exception>(() => plain.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/tentacle/v1/control"), ct));
        }

        // Pinned, with the previous token: registered, flagged as using it.
        using var socket = await ConnectAsync(port, "old", certificate.Fingerprint, ct);
        await SocketFrames.SendAsync(socket, Frame.Json(FrameType.Hello, new Hello { Node = "tatooine", ProtocolMin = 1, ProtocolMax = ProtocolInfo.Version }, ProtocolJson.Default.Hello), ct);
        var welcome = await SocketFrames.ReceiveAsync(socket, ct);
        Assert.Equal(FrameType.Welcome, welcome?.Type);
        Assert.True(await Eventually(() => broker.Nodes.Find("tatooine")?.Snapshot().UsesPreviousToken == true, ct));

        // The overlap ends: the session is dropped at the next heartbeat.
        options.PreviousTokenExpires = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.True(await Eventually(() => broker.Nodes.Find("tatooine") is null, ct));

        // And the old token no longer opens a session.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var rejected = await ConnectAsync(port, "old", certificate.Fingerprint, ct);
            await SocketFrames.SendAsync(rejected, Frame.Json(FrameType.Hello, new Hello { Node = "x", ProtocolMin = 1, ProtocolMax = ProtocolInfo.Version }, ProtocolJson.Default.Hello), ct);
            _ = await SocketFrames.ReceiveAsync(rejected, ct) ?? throw new WebSocketException("closed");
        });
    }

    internal static (string CertPath, string KeyPath, string CaPath) WriteCaSignedCertificate(string name, string? dir = null)
    {
        dir ??= Directory.CreateTempSubdirectory("tentacle-ca-").FullName;
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=Tentacle Test CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var leaf = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90), [1, 2, 3, 4]);

        var certPath = Path.Combine(dir, "tls.crt");
        var keyPath = Path.Combine(dir, "tls.key");
        var caPath = Path.Combine(dir, "ca.crt");
        File.WriteAllText(certPath, leaf.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(caPath, ca.ExportCertificatePem());
        return (certPath, keyPath, caPath);
    }

    private static async Task<ClientWebSocket> ConnectAsync(int port, string token, string fingerprint, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        socket.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert is not null && string.Equals(Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())), fingerprint, StringComparison.OrdinalIgnoreCase);
        try
        {
            await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{port}/tentacle/v1/control"), ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<bool> Eventually(Func<bool> condition, CancellationToken ct)
    {
        for (var i = 0; i < 50; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100, ct);
        }

        return false;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
