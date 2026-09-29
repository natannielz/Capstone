using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Documents;

public enum EvidenceTargetKind
{
    CatalogFamily = 1,
    CatalogSku = 2,
    PaymentDeclaration = 3
}

/// <summary>
/// Identifies immutable content within a lineage. Id is not the independent Mongo version-row key.
/// This reference does not grant access or prove that the referenced bytes remain eligible.
/// </summary>
public sealed record EvidenceRef
{
    public long Id { get; }
    public long Version { get; }
    public string Sha256 { get; }

    public EvidenceRef(long id, long version, string sha256)
    {
        if (id <= 0 || version <= 0 || sha256 is null || sha256.Length != 64 ||
            sha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new DomainValidationException("evidence_invalid_reference", "Evidence reference is invalid");
        Id = id;
        Version = version;
        Sha256 = sha256;
    }
}

/// <summary>Stable owning aggregate identity; ownership and current authority are resolved by its module.</summary>
public sealed record EvidenceTargetRef
{
    public EvidenceTargetKind Kind { get; }
    public long Id { get; }

    public EvidenceTargetRef(EvidenceTargetKind kind, long id)
    {
        if (kind is not (EvidenceTargetKind.CatalogFamily or EvidenceTargetKind.CatalogSku or EvidenceTargetKind.PaymentDeclaration) || id <= 0)
            throw new DomainValidationException("evidence_invalid_target", "Evidence target is invalid");
        Kind = kind;
        Id = id;
    }
}

/// <summary>
/// Explicit server configuration, never request-selected limits. Decoder isolation and elapsed-time,
/// memory, quota and storage enforcement belong to the runtime and are not supplied by this value.
/// </summary>
public sealed record EvidenceLimits
{
    public long MaximumBytes { get; }
    public int MaximumWidth { get; }
    public int MaximumHeight { get; }
    public long MaximumPixels { get; }

    public EvidenceLimits(long maximumBytes, int maximumWidth, int maximumHeight, long maximumPixels)
    {
        if (maximumBytes <= 0 || maximumWidth <= 0 || maximumHeight <= 0 || maximumPixels <= 0)
            throw new DomainValidationException("evidence_invalid_limits", "Evidence limits must be explicitly configured");
        MaximumBytes = maximumBytes;
        MaximumWidth = maximumWidth;
        MaximumHeight = maximumHeight;
        MaximumPixels = maximumPixels;
    }

    public bool AcceptsImage(long length, int width, int height, int frames, int pages) =>
        length > 0 && length <= MaximumBytes &&
        width > 0 && width <= MaximumWidth &&
        height > 0 && height <= MaximumHeight &&
        (long)width * height <= MaximumPixels &&
        frames == 1 && pages == 1;
}

public static class EvidencePolicy
{
    /// <summary>
    /// Pure media prerequisite only. The service must additionally verify exact reference/generation,
    /// complete decoding, limits, current security verdict, owning target and transactionally pinned eligibility.
    /// A true result never verifies received money, actor independence or permission to submit.
    /// </summary>
    public static bool CanSubmitPayment(string? state, string? scan, string? mime, bool bytesPresent) =>
        state == "validated" && scan == "clean" && bytesPresent &&
        mime is "image/jpeg" or "image/png";
}
