using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace Tentacle.Broker;

/// <summary>
/// The agent port's TLS certificate. Either self-signed, created once and kept in
/// the server's data directory so its fingerprint (what agents pin) survives
/// restarts, or loaded from files (cert-manager, a real CA) and reloaded when they
/// change, so a renewal needs no restart.
/// </summary>
public sealed class BrokerCertificate
{
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(30);
    private readonly string? _certPath;
    private readonly string? _keyPath;
    private readonly Lock _lock = new();
    private X509Certificate2 _current;
    private DateTime _loadedStamp;
    private DateTimeOffset _checkedAt;

    private BrokerCertificate(X509Certificate2 certificate, string? certPath, string? keyPath)
    {
        _current = certificate;
        _certPath = certPath;
        _keyPath = keyPath;
        _loadedStamp = certPath is null ? default : Stamp(certPath, keyPath);
        _checkedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Gets a value indicating whether the certificate is the generated self-signed one.</summary>
    public bool SelfSigned => _certPath is null;

    /// <summary>
    /// Gets the current certificate, reloading the files when they changed.
    /// </summary>
    public X509Certificate2 Current
    {
        get
        {
            lock (_lock)
            {
                if (_certPath is not null && DateTimeOffset.UtcNow - _checkedAt > RecheckInterval)
                {
                    _checkedAt = DateTimeOffset.UtcNow;
                    try
                    {
                        var stamp = Stamp(_certPath, _keyPath);
                        if (stamp != _loadedStamp)
                        {
                            _current = Load(_certPath, _keyPath);
                            _loadedStamp = stamp;
                        }
                    }
                    catch (Exception e) when (e is IOException or CryptographicException or UnauthorizedAccessException)
                    {
                        // Half-written during a renewal: keep serving the old one, retry later.
                    }
                }

                return _current;
            }
        }
    }

    /// <summary>
    /// Gets the SHA-256 fingerprint of the current certificate, as agents pin it.
    /// </summary>
    public string Fingerprint => FingerprintOf(Current);

    /// <summary>
    /// The SHA-256 of a certificate's DER encoding, lowercase hex.
    /// </summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>The fingerprint.</returns>
    public static string FingerprintOf(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
    }

    /// <summary>
    /// Loads a certificate and key from files: PEM (certificate chain + separate or
    /// bundled key) or PKCS#12.
    /// </summary>
    /// <param name="certPath">The certificate (PEM or .pfx/.p12).</param>
    /// <param name="keyPath">The PEM key, when not in the certificate file.</param>
    /// <returns>The certificate source.</returns>
    public static BrokerCertificate FromFiles(string certPath, string? keyPath)
        => new(Load(certPath, keyPath), certPath, keyPath);

    /// <summary>
    /// Loads the self-signed certificate from <paramref name="pfxPath"/>, creating it
    /// on first use (or when it is about to expire).
    /// </summary>
    /// <param name="pfxPath">Where it is kept (mode 0600).</param>
    /// <param name="hostName">The server's name, added as a SAN (agents pin the fingerprint anyway).</param>
    /// <returns>The certificate source.</returns>
    public static BrokerCertificate SelfSignedAt(string pfxPath, string hostName)
    {
        ArgumentNullException.ThrowIfNull(pfxPath);
        if (File.Exists(pfxPath))
        {
            const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(pfxPath) != Private)
            {
                // Restored from a backup or created by hand: the key is for this user only.
                File.SetUnixFileMode(pfxPath, Private);
            }

            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null, X509KeyStorageFlags.Exportable);
                if (existing.NotAfter > DateTime.UtcNow.AddDays(30))
                {
                    return new BrokerCertificate(existing, null, null);
                }
            }
            catch (CryptographicException)
            {
                // Corrupt: make a new one (agents will need the new fingerprint).
            }
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=tentacle-broker", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("tentacle-broker");
        if (!string.IsNullOrWhiteSpace(hostName))
        {
            san.AddDnsName(hostName);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));

        var directory = Path.GetDirectoryName(pfxPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var pfx = created.Export(X509ContentType.Pkcs12);
        WritePrivate(pfxPath, pfx);

        return new BrokerCertificate(X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable), null, null);
    }

    /// <summary>
    /// Writes a file holding a private key: created 0600 from the start (never
    /// briefly world-readable under the umask), under a temporary name, then
    /// renamed into place so a crash leaves no partial file.
    /// </summary>
    private static void WritePrivate(string path, byte[] content)
    {
        var temporary = path + ".tmp-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    private static DateTime Stamp(string certPath, string? keyPath)
    {
        var stamp = File.GetLastWriteTimeUtc(certPath);
        return keyPath is null ? stamp : new DateTime(Math.Max(stamp.Ticks, File.GetLastWriteTimeUtc(keyPath).Ticks), DateTimeKind.Utc);
    }

    private static X509Certificate2 Load(string certPath, string? keyPath)
    {
        var extension = Path.GetExtension(certPath);
        if (extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".p12", StringComparison.OrdinalIgnoreCase))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(certPath, null);
        }

        // Kestrel on Linux serves a PEM-loaded (ephemeral) key fine.
        return X509Certificate2.CreateFromPemFile(certPath, keyPath);
    }
}
