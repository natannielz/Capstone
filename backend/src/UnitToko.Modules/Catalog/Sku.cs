using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Catalog;

/// <summary>Sellable identity; definition changes never reinterpret its previous immutable versions.
/// Construction alone supplies no authorization, identifier reservation or persistence guarantee.</summary>
public sealed record Sku(LongId Id, LongId OrganizationId, LongId FamilyId,
    Guid PublicId, string Code, string PhysicalItemKey, bool Active,
    LongId CurrentDefinitionId, long Revision, LongId? PredecessorSkuId = null);
