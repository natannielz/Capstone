namespace UnitToko.Platform.Catalog;

/// <summary>Names of the retired assortment-level showcase records (migration 121). Nothing writes them any more: migration 122
/// converted every approved-public assortment into sample products, and the rows stay as decision history.</summary>
internal static class CatalogPublicShowcaseBson
{
    internal const string Heads = "catalogPublicShowcaseHeads", Proposals = "catalogPublicShowcaseProposals",
        Decisions = "catalogPublicShowcaseDecisions", Prefix = "catalog.public.showcase.";
}
