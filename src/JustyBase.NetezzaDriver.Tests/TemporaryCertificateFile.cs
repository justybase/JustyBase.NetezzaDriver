using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace JustyBase.NetezzaDriver.Tests;

internal sealed class TemporaryCertificateFile : IDisposable
{
    private TemporaryCertificateFile(string path) => Path = path;

    internal string Path { get; }

    internal static TemporaryCertificateFile Create()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=JustyBase Netezza driver test certificate",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"justybase-test-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, certificate.ExportCertificatePem());
        return new TemporaryCertificateFile(path);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (FileNotFoundException)
        {
        }
    }
}
