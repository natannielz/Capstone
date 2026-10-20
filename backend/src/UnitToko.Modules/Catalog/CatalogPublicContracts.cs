using System.Collections.Immutable;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Catalog;

/// <summary>A product as anyone may see it before signing in: what it is and how it is packed. Deliberately no price, stock,
/// internal id, owner, assortment or buyer identifier.</summary>
public sealed record PublicCatalogAttribute(string Key, string Value);
public sealed record PublicCatalogItem(string PublicId, string Code, string Name, string Description, string CategoryCode, string CategoryName,
    string PackageLabel, string PackageKind, string BaseUnitCode, int BaseUnitsPerPackage, ImmutableArray<PublicCatalogAttribute> Attributes,
    int MinimumOrderPackages, int OrderIncrementPackages);
public sealed record PublicCatalogCategory(string Code, string Name, long Count);
public sealed record PublicCatalogPage(long Total, ImmutableArray<PublicCatalogItem> Items, ImmutableArray<PublicCatalogCategory> Categories);
public enum PublicCatalogSort { Added, Name }

/// <summary>Staff view of one assortment's public state and any proposal waiting for its independent checker.</summary>
public sealed record CatalogPublicShowcaseProposal(long ProposalId, bool MakePublic, long ProposedBy, DateTimeOffset ProposedAt, string Reason);
public sealed record CatalogPublicShowcase(long AssortmentId, GrantScope Owner, string? OwnerName, long ListedProducts, bool IsPublic, long Revision,
    CatalogPublicShowcaseProposal? Pending);
public enum CatalogPublicShowcaseDecision { Approve, Reject }
