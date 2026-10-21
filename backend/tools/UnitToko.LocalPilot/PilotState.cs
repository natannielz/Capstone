using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace UnitToko.LocalPilot;

public sealed record PilotAccount(long Id, string Name, string Username, string Password, string FactorCode)
{
    public string Subject => "local-pilot|" + Id;
    public string Email => Username + "@unit-toko-pilot.home.arpa";
}

public sealed record PilotSecrets(string InstanceId, string ClientSecret, string CertificatePassword, PilotAccount[] Accounts);

[SupportedOSPlatform("windows")]
public sealed class PilotState : IDisposable
{
    public string Directory { get; }
    public PilotSecrets Secrets { get; }
    public X509Certificate2 Certificate { get; }
    public string CertificatePath => Path.Combine(Directory, "pilot.pfx");
    public string KeyDirectory => Path.Combine(Directory, "session-keys");
    public string SpkiPin => Convert.ToBase64String(SHA256.HashData(Certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    public PilotState(string directory)
    {
        Directory = directory;
        PilotBoundary.ProtectDirectory(directory);
        var path = Path.Combine(directory, "private-settings.json");
        // Full and review profiles also carry the review personas; release1 keeps exactly its four original accounts.
        var expected = (PilotBoundary.IsFullRelease ? PilotPersonas.OriginalAccountIds.Concat(PilotPersonas.AccountIds) : PilotPersonas.OriginalAccountIds).Order().ToArray();
        if (File.Exists(path))
        {
            Secrets = JsonSerializer.Deserialize<PilotSecrets>(File.ReadAllText(path)) ?? throw new InvalidOperationException("Invalid pilot settings.");
            var held = Secrets.Accounts.Select(x => x.Id).Order().ToArray();
            if (!Guid.TryParseExact(Secrets.InstanceId, "N", out _)) throw new InvalidOperationException("Pilot identity manifest does not match this launcher.");
            if (PilotBoundary.IsFullRelease && held.SequenceEqual(PilotPersonas.OriginalAccountIds))
            {
                // An installation from before the personas: keep every existing secret and account, add the personas once.
                Secrets = Secrets with { Accounts = [.. Secrets.Accounts, .. PersonaAccounts()] };
                SaveSecrets(path);
                Console.WriteLine("Akun persona ditambahkan ke " + Path.Combine(directory, "ACCOUNTS-PRIVATE.txt"));
            }
            else if (!held.SequenceEqual(expected)) throw new InvalidOperationException("Pilot identity manifest does not match this launcher.");
        }
        else
        {
            if (File.Exists(CertificatePath)) throw new InvalidOperationException("Incomplete pilot state; preserve it and restore its settings rather than replacing keys.");
            Secrets = new(Guid.NewGuid().ToString("N"), Random(), Random(),
            [Account(1001, "Pemohon Cabang A · SINTETIS", "pemohon-a"), Account(1002, "Pemohon Cabang B · SINTETIS", "pemohon-b"),
             Account(2001, "Petugas katalog · SINTETIS", "petugas"), Account(2002, "Peninjau penyiapan · SINTETIS", "peninjau"),
             .. (PilotBoundary.IsFullRelease ? PersonaAccounts() : Array.Empty<PilotAccount>())]);
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest("CN=UnitToko SYNTHETIC local pilot", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(PilotBoundary.PortalHost); names.AddDnsName(PilotBoundary.ProviderHost);
            if (PilotBoundary.Profile == "review") names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
            File.WriteAllBytes(CertificatePath, certificate.Export(X509ContentType.Pfx, Secrets.CertificatePassword));
            SaveSecrets(path);
        }
        PilotBoundary.ProtectDirectory(KeyDirectory);
        // Windows Schannel needs the certificate's native key-container property for TLS.
        // UserKeySet supplies a current-user temporary container; omitting PersistKeySet
        // releases it on disposal. The private PFX and its public key remain unchanged.
        Certificate = X509CertificateLoader.LoadPkcs12FromFile(CertificatePath, Secrets.CertificatePassword, X509KeyStorageFlags.UserKeySet);
        if (!Certificate.HasPrivateKey || Certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            throw new InvalidOperationException("Pilot certificate expired or private key is unavailable. Preserve the key ring and renew deliberately.");
    }
    private static PilotAccount[] PersonaAccounts() => PilotPersonas.SignedIn.Select(persona => Account(persona.AccountId, persona.Label, persona.Name)).ToArray();
    /// <summary>The settings and the private account list, each replaced whole (written beside, then renamed over the old file).</summary>
    private void SaveSecrets(string path)
    {
        static void Replace(string target, string content) { File.WriteAllText(target + ".new", content); File.Move(target + ".new", target, overwrite: true); }
        Replace(path, JsonSerializer.Serialize(Secrets, new JsonSerializerOptions { WriteIndented = true }));
        Replace(Path.Combine(Directory, "ACCOUNTS-PRIVATE.txt"), string.Join(Environment.NewLine,
            new[] { "SINTETIS — khusus pilot lokal. Kode faktor adalah SIMULASI, bukan MFA penyedia nyata.", "Jangan unggah atau bagikan berkas ini." }
            .Concat(Secrets.Accounts.Select(x => $"{x.Name}\nNama masuk: {x.Username}\nKata sandi: {x.Password}\nKode faktor simulasi: {x.FactorCode}\n"))) + Environment.NewLine);
    }
    private static string Random() =>Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    private static PilotAccount Account(long id, string name, string username) => new(id, name, username, Random(), RandomNumberGenerator.GetInt32(100000, 1000000).ToString(System.Globalization.CultureInfo.InvariantCulture));
    public void Dispose() => Certificate.Dispose();
}
