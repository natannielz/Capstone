using System.Security.Cryptography.X509Certificates;
using UnitToko.Platform.Identity;

namespace UnitToko.Api.Identity;

/// <summary>Owns certificate lifetimes for the shared session, OIDC and anti-forgery key ring.</summary>
public sealed class IdentityKeyMaterial : IDisposable
{
    private readonly List<X509Certificate2> certificates = [];
    public SessionPayloadProtector Protector { get; }
    private const string SealCurrentFile = "seal-current.txt";
    public IdentityKeyMaterial(IConfiguration configuration) : this(configuration, provisionSeals: false) { }
    private IdentityKeyMaterial(IConfiguration configuration, bool provisionSeals)
    {
        var keys = configuration.GetSection("Identity:Keys");
        try
        {
            var current = Load(keys);
            var previous = keys.GetSection("PreviousCertificates").GetChildren().Select(Load).ToArray();
            if (previous.Length > 8) throw new InvalidOperationException("Too many retained identity certificates");
            Protector = SessionPayloadProtector.CreatePersistent(keys["Directory"] ?? "", keys["ApplicationName"] ?? "", current, previous);
            // Provenance seal keys live encrypted in the same protected directory. They are created only by the explicit
            // provisioning below (operator command, local pilot), never by an ordinary start.
            var directory = keys["Directory"]!;
            if (provisionSeals && !File.Exists(Path.Combine(directory, SealCurrentFile)))
                UnitToko.Platform.Integrity.SealKeyRing.Provision(Protector.Provider, directory);
            if (File.Exists(Path.Combine(directory, SealCurrentFile)))
                UnitToko.Platform.Integrity.ProvenanceSealRing.Configure(UnitToko.Platform.Integrity.SealKeyRing.Load(Protector.Provider, directory));
            else if (configuration.GetValue<bool>("Integrity:SealsRequired")) throw new InvalidOperationException("Provenance seal keys are required");
        }
        catch
        {
            Dispose();
            throw new InvalidOperationException("Persistent protected identity key material is missing or invalid.");
        }
    }
    /// <summary>Explicit provisioning of the provenance seal key ring in this environment's protected key directory (the
    /// operator command <c>provision-seal-keys</c> and the local pilot). Creates the first key only when none exists, never
    /// rotates an existing ring, and returns the current key id, never key material.</summary>
    public static string ProvisionSealKeys(IConfiguration configuration)
    {
        using var material = new IdentityKeyMaterial(configuration, provisionSeals: true);
        return File.ReadAllText(Path.Combine(configuration["Identity:Keys:Directory"]!, SealCurrentFile)).Trim();
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
