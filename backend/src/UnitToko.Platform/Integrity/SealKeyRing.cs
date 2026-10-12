using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace UnitToko.Platform.Integrity;

public sealed class SealUnavailableException(string code) : Exception("Provenance seal is unavailable")
{
    public string Code { get; } = code;
}

/// <summary>Server-held HMAC keys for provenance seals. Key material never enters MongoDB; it is stored
/// encrypted by the certificate-protected Data Protection provider in the protected key directory.</summary>
public sealed class SealKeyRing
{
    private const string Purpose = "UnitToko.Integrity.Seals.v1";
    private readonly IReadOnlyDictionary<string, byte[]> keys;
    public string CurrentKeyId { get; }

    private SealKeyRing(string current, IReadOnlyDictionary<string, byte[]> keys)
    {
        if (!keys.ContainsKey(current) || keys.Values.Any(key => key.Length != 32) ||
            keys.Keys.Any(id => id.Length is 0 or > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new SealUnavailableException("seal_key_ring_invalid");
        CurrentKeyId = current; this.keys = keys;
    }

    public static SealKeyRing ForKeys(string currentKeyId, IReadOnlyDictionary<string, byte[]> keys) =>
        new(currentKeyId, keys.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal));

    public byte[] Key(string keyId) => keys.TryGetValue(keyId, out var key) ? key : throw new SealUnavailableException("seal_key_unknown");
    internal bool Has(string keyId) => keys.ContainsKey(keyId);

    public static SealKeyRing Load(IDataProtectionProvider provider, string directory)
    {
        var protector = provider.CreateProtector(Purpose);
        var currentFile = Path.Combine(directory, "seal-current.txt");
        if (!File.Exists(currentFile)) throw new SealUnavailableException("seal_key_ring_missing");
        var keys = Directory.GetFiles(directory, "seal-key-*.protected").ToDictionary(
            file => Path.GetFileNameWithoutExtension(file)["seal-key-".Length..], file => protector.Unprotect(File.ReadAllBytes(file)), StringComparer.Ordinal);
        return new(File.ReadAllText(currentFile).Trim(), keys);
    }

    /// <summary>Explicit operator provisioning of a new current key; never called implicitly at startup.</summary>
    public static string Provision(IDataProtectionProvider provider, string directory)
    {
        var id = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(directory, "seal-key-" + id + ".protected"), provider.CreateProtector(Purpose).Protect(RandomNumberGenerator.GetBytes(32)));
        File.WriteAllText(Path.Combine(directory, "seal-current.txt"), id);
        return id;
    }
}
