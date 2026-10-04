using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;

namespace UnitToko.Modules.Catalog;

/// <summary>An editor selects a completed private upload; its target, owner and clean bytes are resolved by the server.</summary>
public sealed record CatalogAssociatePrimaryPhoto(LongId SkuId, long ExpectedSkuRevision, long ExpectedPhotoRevision,
    long ReservationId, EvidenceRef Reference);
public sealed record CatalogPrimaryPhotoResult(LongId AssociationId, LongId SkuId, LongId DefinitionId,
    long DefinitionVersion, long SkuRevision, long PhotoRevision, DateTimeOffset RecordedAt);

/// <summary>Current authoring selection. A previous-definition image stays in history but is not a current package photo.</summary>
public sealed record CatalogPrimaryPhotoSelection(LongId AssociationId, long ReservationId, EvidenceRef Reference,
    string Mime, DateTimeOffset RecordedAt, bool MatchesCurrentDefinition);
public sealed record CatalogPrimaryPhotoCurrent(LongId SkuId, long SkuRevision, LongId DefinitionId,
    long DefinitionVersion, long PhotoRevision, CatalogPrimaryPhotoSelection? Photo);
