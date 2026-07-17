using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse.Transport;
using Pulse.Transport.WebTransport;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DCLPulseTests.Transport;

/// <summary>
///     Pins <see cref="WebTransportCertificate.Resolve" />: inline PEM wins, then file paths; a missing
///     certificate generates a self-signed ECDSA-P256 dev cert only when self-signing is allowed
///     (Development), and throws otherwise. The dev cert meets WebTransport's
///     <c>serverCertificateHashes</c> requirements (ECDSA-P256, validity under 14 days).
/// </summary>
[TestFixture]
public class WebTransportCertificateTests
{
    private ILogger logger;

    [SetUp]
    public void SetUp() => logger = Substitute.For<ILogger>();

    [Test]
    public void Resolve_WithInlinePem_ReturnsVerbatim()
    {
        var options = new WebTransportOptions { CertPem = "INLINE-CERT", KeyPem = "INLINE-KEY" };

        (string certPem, string keyPem) = WebTransportCertificate.Resolve(options, allowSelfSigned: false, logger);

        Assert.That(certPem, Is.EqualTo("INLINE-CERT"));
        Assert.That(keyPem, Is.EqualTo("INLINE-KEY"));
    }

    [Test]
    public void Resolve_WithFilePaths_ReadsFromDisk()
    {
        string certPath = Path.GetTempFileName();
        string keyPath = Path.GetTempFileName();

        try
        {
            File.WriteAllText(certPath, "FILE-CERT");
            File.WriteAllText(keyPath, "FILE-KEY");
            var options = new WebTransportOptions { CertPath = certPath, KeyPath = keyPath };

            (string certPem, string keyPem) = WebTransportCertificate.Resolve(options, allowSelfSigned: false, logger);

            Assert.That(certPem, Is.EqualTo("FILE-CERT"));
            Assert.That(keyPem, Is.EqualTo("FILE-KEY"));
        }
        finally
        {
            File.Delete(certPath);
            File.Delete(keyPath);
        }
    }

    [Test]
    public void Resolve_WithNoCert_AndSelfSignAllowed_GeneratesSelfSignedEcdsaP256UnderFourteenDays()
    {
        try
        {
            (string certPem, string keyPem) = WebTransportCertificate.Resolve(new WebTransportOptions(), allowSelfSigned: true, logger);

            Assert.That(certPem, Does.Contain("BEGIN CERTIFICATE"));
            Assert.That(keyPem, Does.Contain("BEGIN PRIVATE KEY"));

            using X509Certificate2 certificate = X509Certificate2.CreateFromPem(certPem);
            using ECDsa? ecdsa = certificate.GetECDsaPublicKey();

            Assert.That(ecdsa, Is.Not.Null, "the dev certificate must use ECDSA — serverCertificateHashes requires ECDSA-P256");
            Assert.That(ecdsa!.KeySize, Is.EqualTo(256));
            Assert.That(certificate.NotAfter - certificate.NotBefore, Is.LessThan(TimeSpan.FromDays(14)),
                "serverCertificateHashes rejects certificates valid for more than 14 days");

            // The dev-cert SHA-256 (base64) is written to the well-known file so a local client can pin it.
            Assert.That(File.Exists(WebTransportDevCert.HashFilePath), Is.True);
            byte[] writtenHash = Convert.FromBase64String(File.ReadAllText(WebTransportDevCert.HashFilePath));
            Assert.That(writtenHash, Is.EqualTo(SHA256.HashData(certificate.RawData)));
        }
        finally
        {
            File.Delete(WebTransportDevCert.HashFilePath);
        }
    }

    [Test]
    public void Resolve_WithNoCert_AndSelfSignDisallowed_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WebTransportCertificate.Resolve(new WebTransportOptions(), allowSelfSigned: false, logger));
    }

    [Test]
    public void Resolve_WithCertButNoKey_FallsThroughInsteadOfUsingHalfConfig()
    {
        // A half-configured inline pair (cert without key) is not a usable certificate — it must fall
        // through rather than be returned verbatim. With self-signing allowed it becomes a dev cert.
        var options = new WebTransportOptions { CertPem = "CERT-ONLY" };

        try
        {
            (string certPem, _) = WebTransportCertificate.Resolve(options, allowSelfSigned: true, logger);

            Assert.That(certPem, Is.Not.EqualTo("CERT-ONLY"));
            Assert.That(certPem, Does.Contain("BEGIN CERTIFICATE"));
        }
        finally
        {
            File.Delete(WebTransportDevCert.HashFilePath);
        }
    }
}
