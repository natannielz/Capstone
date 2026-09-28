using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Platform.Inventory;

internal static class InventoryBson
{
    internal const string BalanceKind = "inventory.balance";
    private static readonly string[] Fields =
        ["_id", "schemaVersion", "kind", "physicalPoolId", "skuId", "definitionId", "lotId", "location", "onHand", "reserved", "held", "expired", "version"];
    internal static BsonDocument Balance(InventoryBalanceDocument row)
    {
        if (row is null || row.Id <= 0 || row.PhysicalPoolId <= 0) throw Invalid();
        _ = StockBalancePolicy.AvailablePackages(row.Balance);
        var state = row.Balance; var key = state.Key; var location = key.Location;
        return new()
        {
            { "_id", row.Id }, { "schemaVersion", 1 }, { "kind", BalanceKind }, { "physicalPoolId", row.PhysicalPoolId },
            { "skuId", key.SkuId.Value }, { "definitionId", key.DefinitionId.Value }, { "lotId", key.LotId.Value },
            { "location", new BsonDocument { { "organizationId", location.OrganizationId.Value }, { "shopId", location.ShopId.Value },
                { "warehouseId", location.WarehouseId.Value }, { "channelId", location.ChannelId.Value } } },
            { "onHand", state.OnHand }, { "reserved", state.Reserved }, { "held", state.Held }, { "expired", state.Expired }, { "version", state.Revision }
        };
    }
    internal static InventoryBalanceDocument ReadBalance(BsonDocument? row)
    {
        if (row is null || !Exact(row, Fields) || !row["schemaVersion"].IsInt32 || row["schemaVersion"].AsInt32 != 1 ||
            !row["kind"].IsString || row["kind"].AsString != BalanceKind || !row["location"].IsBsonDocument) throw Invalid();
        var location = row["location"].AsBsonDocument;
        if (!Exact(location, ["organizationId", "shopId", "warehouseId", "channelId"])) throw Invalid();
        var key = new StockBalanceKey(new(new(Positive(location, "organizationId")), new(Positive(location, "shopId")),
            new(Positive(location, "warehouseId")), new(Positive(location, "channelId"))),
            new(Positive(row, "skuId")), new(Positive(row, "definitionId")), new(Positive(row, "lotId")));
        var state = new StockBalance(key, Nonnegative(row, "onHand"), Nonnegative(row, "reserved"),
            Nonnegative(row, "held"), Nonnegative(row, "expired"), Positive(row, "version"));
        try { _ = StockBalancePolicy.AvailablePackages(state); } catch (DomainValidationException) { throw Invalid(); }
        return new(Positive(row, "_id"), Positive(row, "physicalPoolId"), state);
    }
    private static bool Exact(BsonDocument row, string[] fields) =>
        row.ElementCount == fields.Length && row.Names.Distinct(StringComparer.Ordinal).Count() == fields.Length && fields.All(row.Contains);
    private static long Positive(BsonDocument row, string name) =>
        row.TryGetValue(name, out var value) && value.IsInt64 && value.AsInt64 > 0 ? value.AsInt64 : throw Invalid();
    private static long Nonnegative(BsonDocument row, string name) =>
        row.TryGetValue(name, out var value) && value.IsInt64 && value.AsInt64 >= 0 ? value.AsInt64 : throw Invalid();
    internal static DomainValidationException Invalid() => new("invalid_inventory_balance", "Inventory balance has invalid identities, quantities or format");
}
