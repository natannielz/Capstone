using System.Security.Cryptography.X509Certificates;
using UnitToko.Platform.Identity;

namespace UnitToko.Api.Identity;

/// <summary>Owns certificate lifetimes for the shared session, OIDC and anti-forgery key ring.</summary>
public sealed class IdentityKeyMaterial : IDisposable
{
    private readonly List<X509Certificate2> certificates = [];
    public SessionPayloadProtector Protector { get; }
    public IdentityKeyMaterial(IConfiguration configuration)
    {
        var keys = configuration.GetSection("Identity:Keys");
        try
        {
            var current = Load(keys);
            var previous = keys.GetSection("PreviousCertificates").GetChildren().Select(Load).ToArray();
            if (previous.Length > 8) throw new InvalidOperationException("Too many retained identity certificates");
            Protector = SessionPayloadProtector.CreatePersistent(keys["Directory"] ?? "", keys["ApplicationName"] ?? "", current, previous);
            // Provenance seal keys live encrypted in the same protected directory; operators provision them explicitly.
            if (File.Exists(Path.Combine(keys["Directory"]!, "seal-current.txt")))
                UnitToko.Platform.Integrity.ProvenanceSealRing.Configure(UnitToko.Platform.Integrity.SealKeyRing.Load(Protector.Provider, keys["Directory"]!));
            else if (configuration.GetValue<bool>("Integrity:SealsRequired")) throw new InvalidOperationException("Provenance seal keys are required");
        }
        catch
        {
            Dispose();
            throw new InvalidOperationException("Persistent protected identity key material is missing or invalid.");
        }
    }
    private X509Certificate2 Load(IConfiguration section)
    {
        var path = section["CertificatePath"];
        var password = section["CertificatePassword"];
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            string.IsNullOrEmpty(password) || password.Length > 4096 || new FileInfo(path).Length > 1_048_576)
            throw new InvalidOperationException("Identity certificate configuration is invalid");
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        certificates.Add(certificate);
        return certificate;
    }
    public void Dispose()
    {
        foreach (var certificate in certificates) certificate.Dispose();
        certificates.Clear();
    }
}
