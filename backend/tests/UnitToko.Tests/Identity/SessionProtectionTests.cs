using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using UnitToko.Platform.Identity;

namespace UnitToko.Tests.Identity;

public sealed class SessionProtectionTests
{
    [Fact]
    public void Persistent_certificate_protected_keys_are_shared_and_purposes_are_isolated()
    {
        InDirectory(directory =>
        {
            using var rsa = RSA.Create(2048);
            using var certificate = new CertificateRequest("CN=UnitToko synthetic session keys", rsa, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            var first = SessionPayloadProtector.CreatePersistent(directory, "UnitToko.Tests", certificate);
            var encrypted = first.Protect("invitation-delivery", "synthetic-secret");
            var second = SessionPayloadProtector.CreatePersistent(directory, "UnitToko.Tests", certificate);
            Assert.Equal("synthetic-secret", second.Unprotect("invitation-delivery", encrypted));
            Assert.Throws<CryptographicException>(() => second.Unprotect("different-purpose", encrypted));
            var file = Assert.Single(Directory.GetFiles(directory, "key-*.xml"));
            var text = File.ReadAllText(file);
            Assert.Contains("encryptedSecret", text); Assert.DoesNotContain("<masterKey", text);
            var firstFramework = first.Provider.CreateProtector("framework-test");
            Assert.Equal("shared", second.Provider.CreateProtector("framework-test").Unprotect(firstFramework.Protect("shared")));
        });
    }

    [Fact]
    public void Missing_persistence_private_key_or_existing_plaintext_keys_fail_closed()
    {
        InDirectory(directory =>
        {
            using var rsa = RSA.Create(2048);
            using var certificate = new CertificateRequest("CN=UnitToko synthetic session keys", rsa, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
            Assert.Throws<InvalidOperationException>(() => SessionPayloadProtector.CreatePersistent("relative-directory", "app", certificate));
            Assert.Throws<InvalidOperationException>(() => SessionPayloadProtector.CreatePersistent(directory, "app", publicOnly));
            File.WriteAllText(Path.Combine(directory, "key-unsafe.xml"), "<key><descriptor><masterKey>not-a-real-key</masterKey></descriptor></key>");
            Assert.Throws<InvalidOperationException>(() => SessionPayloadProtector.CreatePersistent(directory, "app", certificate));
        });
    }

    private static void InDirectory(Action<string> action)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnitToko-SessionProtectionTests"));
        var directory = Path.GetFullPath(Path.Combine(root, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally
        {
            if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing cleanup outside test-owned directory");
            Directory.Delete(directory, recursive: true);
        }
    }
}
