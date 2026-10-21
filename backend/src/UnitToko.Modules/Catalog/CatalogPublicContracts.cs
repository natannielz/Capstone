using System.Collections.Immutable;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Catalog;

/// <summary>A product as anyone may see it before signing in: what it is and how it is packed. Deliberately no price, stock,
/// internal id, owner, assortment or buyer identifier.</summary>
public sealed record PublicCatalogAttribute(string Key, string Value);
/// <summary>One package of a guest product, as published now.</summary>
public sealed record PublicCatalogPackage(string PublicId, string Code, string PackageLabel, string PackageKind, string BaseUnitCode, int BaseUnitsPerPackage,
    ImmutableArray<PublicCatalogAttribute> Attributes, int MinimumOrderPackages, int OrderIncrementPackages)
{
    public bool Equals(PublicCatalogPackage? other) => other is not null && PublicId == other.PublicId && Code == other.Code && PackageLabel == other.PackageLabel &&
        PackageKind == other.PackageKind && BaseUnitCode == other.BaseUnitCode && BaseUnitsPerPackage == other.BaseUnitsPerPackage &&
        Attributes.SequenceEqual(other.Attributes) && MinimumOrderPackages == other.MinimumOrderPackages && OrderIncrementPackages == other.OrderIncrementPackages;
    public override int GetHashCode() => HashCode.Combine(PublicId, Code);
}
/// <summary>One sample product (catalog family) with every package it publishes now. PublicId is the product's own public id.</summary>
public sealed record PublicCatalogItem(string PublicId, string Name, string Description, string CategoryCode, string CategoryName,
    ImmutableArray<PublicCatalogPackage> Packages)
{
    public bool Equals(PublicCatalogItem? other) => other is not null && PublicId == other.PublicId && Name == other.Name && Description == other.Description &&
        CategoryCode == other.CategoryCode && CategoryName == other.CategoryName && Packages.SequenceEqual(other.Packages);
    public override int GetHashCode() => HashCode.Combine(PublicId, Name, CategoryCode);
}
public sealed record PublicCatalogCategory(string Code, string Name, long Count);
public sealed record PublicCatalogPage(long Total, ImmutableArray<PublicCatalogItem> Items, ImmutableArray<PublicCatalogCategory> Categories);
public enum PublicCatalogSort { Added, Name }

/// <summary>Staff view of one product (catalog family) for the guest sample: whether guests see it, how many packages are published
/// now, and any proposal waiting for its independent checker. OwnProposal says whether the viewer proposed it; who did stays in history.</summary>
public sealed record CatalogPublicSamplePending(long ProposalId, bool Adding, bool OwnProposal);
public sealed record CatalogPublicSampleItem(long FamilyId, string FamilyName, GrantScope Owner, string OwnerLabel, long Packages, bool IsSample,
    CatalogPublicSamplePending? Pending);
public sealed record CatalogPublicSampleChange(long FamilyId, string FamilyName);
public sealed record CatalogPublicSampleProposal(long ProposalId, bool OwnProposal, string OwnerLabel, string Reason, DateTimeOffset ProposedAt,
    ImmutableArray<CatalogPublicSampleChange> Adds, ImmutableArray<CatalogPublicSampleChange> Removes);
public sealed record CatalogPublicSamplePage(long Total, ImmutableArray<CatalogPublicSampleItem> Items, ImmutableArray<CatalogPublicSampleProposal> Proposals,
    long SampleCount, long SampleLimit);
public enum CatalogPublicSampleDecision { Approve, Reject }
