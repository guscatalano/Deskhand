using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Deskhand.Core;

/// <summary>
/// Optional HTTPS for the Deskhand hosts. A host asks for a server certificate via environment
/// variables; if none are set this returns null and the caller stays on plain HTTP. Two sources:
///   - <c>&lt;prefix&gt;TLS_CERT</c> = path to a PKCS#12 (.pfx) file, with optional
///     <c>&lt;prefix&gt;TLS_PASSWORD</c>.
///   - <c>&lt;prefix&gt;TLS = self-signed</c> = generate an ephemeral self-signed cert (CN = machine
///     name; SAN = localhost + hostname + this box's IPv4 addresses). Handy for a LAN with no CA;
///     clients will warn about trust — front it with a real cert / reverse proxy for anything serious.
/// The prefix is <c>DESKHAND_</c> for the local server and <c>DESKHAND_FLEET_</c> for the fleet.
/// </summary>
public static class TlsSupport
{
    public static X509Certificate2? FromEnvironment(string prefix)
    {
        var path = Environment.GetEnvironmentVariable(prefix + "TLS_CERT")?.Trim();
        if (!string.IsNullOrWhiteSpace(path))
        {
            var pwd = Environment.GetEnvironmentVariable(prefix + "TLS_PASSWORD");
            return X509CertificateLoader.LoadPkcs12FromFile(path, pwd);
        }

        var mode = Environment.GetEnvironmentVariable(prefix + "TLS")?.Trim();
        if (string.Equals(mode, "self-signed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "self", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "1", StringComparison.OrdinalIgnoreCase))
            return SelfSigned();

        return null;
    }

    static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={Environment.MachineName}", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(IPAddress.Loopback);
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                if (ip.AddressFamily == AddressFamily.InterNetwork) san.AddIpAddress(ip);
        }
        catch { /* no network name resolution — loopback SAN is enough */ }
        req.CertificateExtensions.Add(san.Build());

        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // Round-trip through a PFX so Kestrel gets a persistable private key (avoids the Windows
        // "ephemeral key set" failure when binding an in-memory CreateSelfSigned cert).
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }
}
