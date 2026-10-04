using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Documents;

/// <summary>A new scan of the exact committed CatalogSku original. No new upload or first-result rewrite.</summary>
public sealed record RequestCatalogImageRevalidation(LongId SkuId, long ExpectedSkuRevision,
    long ExpectedProofRevision, long ReservationId, EvidenceRef Reference);

public sealed record CatalogImageRevalidationRequestResult(LongId RequestId, LongId SkuId,
    LongId VersionRowId, string State, DateTimeOffset RecordedAt);

/// <summary>Authoring-only selection; a pending or rejected scan never grants content bytes.</summary>
public sealed record CatalogImageRevalidationCurrent(LongId SkuId, LongId VersionRowId,
    long SkuRevision, long ProofRevision, string State, DateTimeOffset? CheckedAt,
    string? Mime, string? PipelineId);
