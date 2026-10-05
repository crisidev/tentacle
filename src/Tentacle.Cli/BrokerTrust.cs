using System;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Tentacle.Cli;

/// <summary>
/// How the agent decides that a wss:// broker is the real one:
///  - pinned: TENTACLE_BROKER_FINGERPRINT, the SHA-256 of the broker's certificate
///    (the dashboard shows it). Fits the self-signed default; names are not checked.
///  - ca: TENTACLE_CA_FILE, PEM roots for a certificate from cert-manager or a
///    private CA; the chain and the host name are checked.
///  - system: neither set, the system trust store (a public CA).
/// </summary>
internal sealed class BrokerTrust
{
    private readonly string? _fingerprint;
    private readonly X509Certificate2Collection? _roots;

    private BrokerTrust(string? fingerprint, X509Certificate2Collection? roots)
    {
        _fingerprint = fingerprint;
        _roots = roots;
    }

    /// <summary>Gets the mode: pinned, ca or system.</summary>
    public string Mode => _fingerprint is not null ? "pinned" : _roots is not null ? "ca" : "system";

    /// <summary>Gets why the last certificate was refused.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>
    /// Reads TENTACLE_BROKER_FINGERPRINT and TENTACLE_CA_FILE.
    /// </summary>
    /// <returns>The trust settings.</returns>
    public static BrokerTrust FromEnvironment()
        => Create(Environment.GetEnvironmentVariable("TENTACLE_BROKER_FINGERPRINT"), Environment.GetEnvironmentVariable("TENTACLE_CA_FILE"));

    /// <summary>
    /// Creates trust settings.
    /// </summary>
    /// <param name="fingerprint">A SHA-256 fingerprint, hex, colons and case ignored.</param>
    /// <param name="caFile">A PEM file of trusted roots.</param>
    /// <returns>The trust settings.</returns>
    public static BrokerTrust Create(string? fingerprint, string? caFile)
    {
        var pin = Normalize(fingerprint);
        if (pin is not null)
        {
            if (pin.Length != 64 || !pin.All(char.IsAsciiHexDigitLower))
            {
                throw new ArgumentException("TENTACLE_BROKER_FINGERPRINT must be a SHA-256 fingerprint (64 hex digits)");
            }

            return new BrokerTrust(pin, null);
        }

        if (!string.IsNullOrWhiteSpace(caFile))
        {
            var roots = new X509Certificate2Collection();
            roots.ImportFromPemFile(caFile);
            if (roots.Count == 0)
            {
                throw new InvalidDataException($"no certificate in {caFile}");
            }

            return new BrokerTrust(null, roots);
        }

        return new BrokerTrust(null, null);
    }

    /// <summary>
    /// The SHA-256 fingerprint of a certificate, lowercase hex.
    /// </summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>The fingerprint.</returns>
    public static string FingerprintOf(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();
    }

    /// <summary>
    /// The TLS validation callback (also used for system trust, to record failures).
    /// </summary>
    /// <param name="sender">The connection.</param>
    /// <param name="certificate">The broker's certificate.</param>
    /// <param name="chain">The chain the platform built.</param>
    /// <param name="errors">The platform's verdict.</param>
    /// <returns>Whether to trust it.</returns>
    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            return Fail("the broker sent no certificate");
        }

        if (_fingerprint is not null)
        {
            var actual = FingerprintOf(certificate);
            return string.Equals(actual, _fingerprint, StringComparison.Ordinal)
                || Fail($"broker certificate fingerprint {actual} is not TENTACLE_BROKER_FINGERPRINT");
        }

        if (_roots is not null)
        {
            if ((errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
            {
                return Fail($"broker certificate does not match the host name ({errors})");
            }

            using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            using var custom = new X509Chain();
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            custom.ChainPolicy.CustomTrustStore.AddRange(_roots);
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (chain is not null)
            {
                foreach (var element in chain.ChainElements)
                {
                    custom.ChainPolicy.ExtraStore.Add(element.Certificate);
                }
            }

            return custom.Build(leaf) || Fail($"broker certificate is not signed by TENTACLE_CA_FILE ({custom.ChainStatus.FirstOrDefaultStatus()})");
        }

        return errors == SslPolicyErrors.None
            || Fail($"broker certificate not trusted ({errors}); for the self-signed default set TENTACLE_BROKER_FINGERPRINT (shown on the dashboard)");
    }

    /// <summary>
    /// Forgets the last failure (it was reported).
    /// </summary>
    public void ClearFailure() => LastFailure = null;

    private static string? Normalize(string? fingerprint)
        => string.IsNullOrWhiteSpace(fingerprint) ? null : fingerprint.Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();

    private bool Fail(string why)
    {
        LastFailure = why;
        return false;
    }
}

/// <summary>
/// Chain status formatting.
/// </summary>
internal static class ChainStatusExtensions
{
    /// <summary>
    /// The first chain problem, readable.
    /// </summary>
    /// <param name="statuses">The chain statuses.</param>
    /// <returns>The first one, or "unknown".</returns>
    public static string FirstOrDefaultStatus(this X509ChainStatus[] statuses)
        => statuses.Length > 0 ? $"{statuses[0].Status}: {statuses[0].StatusInformation.Trim()}" : "unknown";
}
