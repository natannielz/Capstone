using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;

namespace UnitToko.Platform.Identity;

/// <summary>Purpose-separated authenticated encryption. Production construction requires a
/// persistent key directory and certificate protection; never falls back to ephemeral keys.</summary>
public sealed class SessionPayloadProtector
{
    private readonly IDataProtector root;
    public IDataProtectionProvider Provider { get; }
    private SessionPayloadProtector(IDataProtectionProvider provider)
    {
        Provider = provider;
        root = provider.CreateProtector("UnitToko.Identity.Payloads.v1");
    }

    public static SessionPayloadProtector CreatePersistent(string keyDirectory, string applicationName, X509Certificate2 certificate,
        params X509Certificate2[] previousCertificates)
    {
        if (string.IsNullOrWhiteSpace(keyDirectory) || !Path.IsPathFullyQualified(keyDirectory) || !Directory.Exists(keyDirectory) ||
            string.IsNullOrWhiteSpace(applicationName) || applicationName.Length > 128 || applicationName.Any(char.IsControl) ||
            certificate is null || !certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow ||
            certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            throw new InvalidOperationException("Persistent protected identity keys are not configured correctly");
        using var privateKey = certificate.GetRSAPrivateKey();
        if (privateKey is not { KeySize: >= 2048 } || previousCertificates.Any(item => !item.HasPrivateKey))
            throw new InvalidOperationException("Protected identity keys require an approved private certificate");
        EnsureEncryptedKeys(keyDirectory);
        var provider = DataProtectionProvider.Create(new DirectoryInfo(keyDirectory), builder =>
            builder.SetApplicationName(applicationName).ProtectKeysWithCertificate(certificate)
                .UnprotectKeysWithAnyCertificate([certificate, .. previousCertificates]));
        var result = new SessionPayloadProtector(provider);
        // Force key availability now, so a missing private key or unwritable repository fails startup.
        var probe = result.Protect("startup-check", "key-readiness");
        if (result.Unprotect("startup-check", probe) != "key-readiness")
            throw new InvalidOperationException("Protected identity keys are unavailable");
        EnsureEncryptedKeys(keyDirectory);
        return result;
    }

    internal static SessionPayloadProtector FromTestProvider(IDataProtectionProvider provider) => new(provider);

    public string Protect(string purpose, string plaintext)
    {
        ValidatePurpose(purpose);
        if (plaintext is null || Encoding.UTF8.GetByteCount(plaintext) > 131_072)
            throw new ArgumentException("Identity payload is too large");
        return root.CreateProtector(purpose).Protect(plaintext);
    }

    public string Unprotect(string purpose, string protectedPayload)
    {
        ValidatePurpose(purpose);
        if (string.IsNullOrEmpty(protectedPayload) || protectedPayload.Length > 262_144)
            throw new CryptographicException("Identity payload is invalid");
        return root.CreateProtector(purpose).Unprotect(protectedPayload);
    }

    private static void ValidatePurpose(string purpose)
    {
        if (purpose is null || !Regex.IsMatch(purpose, "^[A-Za-z0-9][A-Za-z0-9._-]{0,159}\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("A bounded encryption purpose is required");
    }

    private static void EnsureEncryptedKeys(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.xml", SearchOption.TopDirectoryOnly))
        {
            if (new FileInfo(path).Length > 1_048_576) throw new InvalidOperationException("Identity key file is invalid");
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            if (document.Root?.Name.LocalName == "key" &&
                !document.Descendants().Any(element => element.Name.LocalName == "encryptedSecret"))
                throw new InvalidOperationException("Unprotected identity key files are not allowed");
        }
    }
}
