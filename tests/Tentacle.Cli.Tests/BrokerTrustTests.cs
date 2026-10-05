using System;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Tentacle.Cli;
using Xunit;

namespace Tentacle.Cli.Tests;

public class BrokerTrustTests
{
    private static X509Certificate2 SelfSigned(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void PinnedFingerprintIsTheOnlyThingThatCounts()
    {
        using var broker = SelfSigned("tentacle-broker");
        using var other = SelfSigned("tentacle-broker");
        var pin = BrokerTrust.FingerprintOf(broker);
        var colons = string.Join(':', pin.ToUpperInvariant().Chunk(2).Select(c => new string(c)));
        var trust = BrokerTrust.Create(colons, null);

        Assert.Equal("pinned", trust.Mode);
        Assert.True(trust.Validate(this, broker, null, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(trust.Validate(this, other, null, SslPolicyErrors.None));
        Assert.Contains("is not TENTACLE_BROKER_FINGERPRINT", trust.LastFailure, StringComparison.Ordinal);
        Assert.False(trust.Validate(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("zz00000000000000000000000000000000000000000000000000000000000000")]
    public void MalformedFingerprintsAreRefusedUpFront(string pin)
        => Assert.Throws<ArgumentException>(() => BrokerTrust.Create(pin, null));

    [Fact]
    public void CaFileTrustChecksTheChainAndTheName()
    {
        var dir = Directory.CreateTempSubdirectory("tentacle-trust-").FullName;
        try
        {
            using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var caRequest = new CertificateRequest("CN=Test CA", caKey, HashAlgorithmName.SHA256);
            caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var leaf = new CertificateRequest("CN=broker", key, HashAlgorithmName.SHA256)
                .Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), [9, 9, 9]);
            var caFile = Path.Combine(dir, "ca.crt");
            File.WriteAllText(caFile, ca.ExportCertificatePem());

            var trust = BrokerTrust.Create(null, caFile);
            Assert.Equal("ca", trust.Mode);

            // The platform's chain fails (unknown root): the CA file vouches for it.
            Assert.True(trust.Validate(this, leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.False(trust.Validate(this, leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch));
            using var stranger = SelfSigned("broker");
            Assert.False(trust.Validate(this, stranger, null, SslPolicyErrors.RemoteCertificateChainErrors));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SystemTrustFollowsThePlatform()
    {
        var trust = BrokerTrust.Create(null, null);
        using var cert = SelfSigned("broker");
        Assert.Equal("system", trust.Mode);
        Assert.True(trust.Validate(this, cert, null, SslPolicyErrors.None));
        Assert.False(trust.Validate(this, cert, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Contains("TENTACLE_BROKER_FINGERPRINT", trust.LastFailure, StringComparison.Ordinal);
    }
}
