using System.IO;

namespace Pulse.Transport.WebTransport
{
    /// <summary>
    ///     Well-known location of the self-signed dev-certificate SHA-256 hash (base64). The server
    ///     writes it on startup when it generates a self-signed certificate (Development); a local
    ///     WebTransport client reads it to pin that certificate via <c>serverCertificateHashes</c>.
    ///     Dev-only convenience — a CA-signed cert needs no pinning, and this file is not produced
    ///     outside the self-signed path.
    /// </summary>
    public static class WebTransportDevCert
    {
        public static string HashFilePath =>
            Path.Combine(Path.GetTempPath(), "dcl-pulse-wt-cert-hash");
    }
}
