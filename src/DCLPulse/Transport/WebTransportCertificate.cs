using Microsoft.Extensions.Logging;
using Pulse.Transport.WebTransport;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pulse.Transport;

/// <summary>
///     Resolves the WebTransport TLS certificate and private key (PEM) from
///     <see cref="WebTransportOptions" />, in priority order: inline PEM, then PEM file paths, then a
///     generated self-signed ECDSA-P256 dev certificate — the last only when <c>allowSelfSigned</c> is
///     set (the Development environment). Outside Development a missing certificate is a fatal
///     misconfiguration: the resolver throws rather than come up with a browser-rejected self-signed
///     cert. The self-signed path logs the certificate's SHA-256 hash so a browser can trust it via
///     WebTransport's <c>serverCertificateHashes</c>, which mandates ECDSA-P256 and a validity window
///     no longer than 14 days.
/// </summary>
internal static class WebTransportCertificate
{
    // serverCertificateHashes rejects certificates valid for more than 14 days; stay safely under it.
    private static readonly TimeSpan DEV_CERT_LIFETIME = TimeSpan.FromDays(13);

    public static (string CertPem, string KeyPem) Resolve(WebTransportOptions options, bool allowSelfSigned, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(options.CertPem) && !string.IsNullOrWhiteSpace(options.KeyPem))
        {
            logger.LogInformation("WebTransport: using the inline certificate from configuration.");
            return (options.CertPem, options.KeyPem);
        }

        if (!string.IsNullOrWhiteSpace(options.CertPath) && !string.IsNullOrWhiteSpace(options.KeyPath))
        {
            logger.LogInformation("WebTransport: loading the certificate from {CertPath} and key from {KeyPath}.",
                options.CertPath, options.KeyPath);
            return (File.ReadAllText(options.CertPath), File.ReadAllText(options.KeyPath));
        }

        if (!allowSelfSigned)
            throw new InvalidOperationException(
                "WebTransport is enabled but no certificate is configured. Set WebTransport:CertPem/KeyPem or "
                + "WebTransport:CertPath/KeyPath — a self-signed certificate is generated only in the Development "
                + "environment (browsers reject a self-signed cert without a pinned serverCertificateHashes).");

        return GenerateSelfSigned(logger);
    }

    private static (string CertPem, string KeyPem) GenerateSelfSigned(ILogger logger)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);

        var subjectAltNames = new SubjectAlternativeNameBuilder();
        subjectAltNames.AddDnsName("localhost");
        subjectAltNames.AddIpAddress(IPAddress.Loopback);
        subjectAltNames.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(subjectAltNames.Build());

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        DateTimeOffset notAfter = notBefore.Add(DEV_CERT_LIFETIME);

        using X509Certificate2 certificate = request.CreateSelfSigned(notBefore, notAfter);

        string certHash = Convert.ToBase64String(SHA256.HashData(certificate.RawData));
        WriteDevCertHashFile(certHash, logger);

        logger.LogWarning(
            "WebTransport: no certificate configured — generated a self-signed ECDSA-P256 dev certificate valid until {Expiry:u}. "
            + "Trust it via serverCertificateHashes; its SHA-256 (base64) is {Hash} (also written to {HashFile}).",
            notAfter, certHash, WebTransportDevCert.HashFilePath);

        return (certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    private static void WriteDevCertHashFile(string certHashBase64, ILogger logger)
    {
        try
        {
            File.WriteAllText(WebTransportDevCert.HashFilePath, certHashBase64);
        }
        catch (Exception exception)
        {
            // The hash file only greases the local self-signed dev flow — never fail startup over it.
            logger.LogWarning(exception, "WebTransport: failed to write the dev cert hash to {HashFile}.", WebTransportDevCert.HashFilePath);
        }
    }
}
