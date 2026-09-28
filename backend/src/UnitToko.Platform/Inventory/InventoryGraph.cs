using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Inventory;

/// <summary>Checks the persisted physical owner and immutable lot lineage. This does not grant
/// actor/source authority or prove receipt approval, catalog compatibility, expiry eligibility or cost.</summary>
internal sealed class InventoryGraph(IMongoDatabase database)
{
    internal const int MaximumLineageLength = 64;
    internal async Task RequireAsync(IClientSessionHandle tx, InventoryBalanceDocument balance, bool writing, CancellationToken ct)
    {
        var pools = database.GetCollection<BsonDocument>("inventoryPools");
        var pool = await pools.Find(tx, new BsonDocument("_id", balance.PhysicalPoolId)).SingleOrDefaultAsync(ct);
        RequirePool(pool);
        if (Location(pool!["location"].AsBsonDocument) != balance.Balance.Key.Location || writing && !pool["active"].AsBoolean)
            throw Invalid();
        var lots = database.GetCollection<BsonDocument>("lots");
        var lot = ReadLot(await lots.Find(tx, new BsonDocument("_id", balance.Balance.Key.LotId.Value)).SingleOrDefaultAsync(ct));
        if (lot.OrganizationId != balance.Balance.Key.Location.OrganizationId.Value || lot.SkuId != balance.Balance.Key.SkuId.Value ||
            lot.DefinitionId != balance.Balance.Key.DefinitionId.Value) throw Invalid();
        var visited = new HashSet<long> { lot.Id };
        var expectedRoot = lot.RootId;
        while (lot.ParentId is { } parentId)
        {
            if (visited.Count >= MaximumLineageLength || !visited.Add(parentId)) throw Invalid();
            var parent = ReadLot(await lots.Find(tx, new BsonDocument("_id", parentId)).SingleOrDefaultAsync(ct));
            if (parent.OrganizationId != lot.OrganizationId || parent.RootId != expectedRoot ||
                parent.ExpiresOn != lot.ExpiresOn || parent.RecordedAt > lot.RecordedAt) throw Invalid();
            lot = parent;
        }
        if (lot.Id != expectedRoot) throw Invalid();
        if (writing)
        {
            // A concurrent pool suspension or ownership revision must conflict with this stock write.
            // The pool is a deliberate contention boundary; the owning service can batch stock legs.
            var fence = pool["fence"].AsInt64;
            if (fence == long.MaxValue) throw Invalid();
            var result = await pools.UpdateOneAsync(tx, pool, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct);
            if (result.ModifiedCount != 1) throw new PersistenceConflictException("stock_pool_changed", "The physical stock pool changed");
        }
        ct.ThrowIfCancellationRequested();
    }
    internal static void RequirePool(BsonDocument? row)
    {
        if (row is null || !Exact(row, ["_id", "schemaVersion", "kind", "location", "active", "revision", "fence"]) ||
            !Header(row, "inventory.pool") || !Positive(row, "_id") || !Positive(row, "revision") ||
            !row["fence"].IsInt64 || row["fence"].AsInt64 < 0 || !row["active"].IsBoolean || !row["location"].IsBsonDocument)
            throw Invalid();
        _ = Location(row["location"].AsBsonDocument);
    }
    internal static StockLocation Location(BsonDocument row)
    {
        if (!Exact(row, ["organizationId", "shopId", "warehouseId", "channelId"]) ||
            !row.Names.All(field => Positive(row, field))) throw Invalid();
        return new(new(row["organizationId"].AsInt64), new(row["shopId"].AsInt64), new(row["warehouseId"].AsInt64), new(row["channelId"].AsInt64));
    }
    internal static LotFacts ReadLot(BsonDocument? row)
    {
        if (row is null || !Exact(row, ["_id", "schemaVersion", "kind", "organizationId", "skuId", "definitionId", "rootLotId",
                "parentLotId", "origin", "sourceId", "sourceLineId", "recordedAt", "expiresOn"]) || !Header(row, "inventory.lot") ||
            !new[] { "_id", "organizationId", "skuId", "definitionId", "rootLotId", "sourceId", "sourceLineId" }.All(field => Positive(row, field)) ||
            !row["origin"].IsString || !row["recordedAt"].IsBsonDateTime) throw Invalid();
        var milliseconds = row["recordedAt"].AsBsonDateTime.MillisecondsSinceEpoch;
        if (milliseconds < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() || milliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()) throw Invalid();
        DateOnly? expiry = null;
        if (!row["expiresOn"].IsBsonNull)
        {
            if (!row["expiresOn"].IsString || !DateOnly.TryParseExact(row["expiresOn"].AsString, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed)) throw Invalid();
            expiry = parsed;
        }
        var id = row["_id"].AsInt64; var root = row["rootLotId"].AsInt64;
        long? parent = null;
        switch (row["origin"].AsString)
        {
            case "openingReceipt": case "procurementReceipt":
                if (!row["parentLotId"].IsBsonNull || root != id) throw Invalid();
                break;
            case "packageConversion":
                if (!Positive(row, "parentLotId") || row["parentLotId"].AsInt64 == id || root == id) throw Invalid();
                parent = row["parentLotId"].AsInt64;
                break;
            default: throw Invalid();
        }
        return new(id, row["organizationId"].AsInt64, row["skuId"].AsInt64, row["definitionId"].AsInt64,
            root, parent, milliseconds, expiry);
    }
    private static bool Header(BsonDocument row, string kind) => row["schemaVersion"].IsInt32 && row["schemaVersion"].AsInt32 == 1 && row["kind"].IsString && row["kind"].AsString == kind;
    private static bool Positive(BsonDocument row, string field) => row[field].IsInt64 && row[field].AsInt64 > 0;
    private static bool Exact(BsonDocument row, string[] fields) => row.ElementCount == fields.Length && row.Names.Distinct(StringComparer.Ordinal).Count() == fields.Length && fields.All(row.Contains);
    internal static DomainValidationException Invalid() => new("inventory_graph_unavailable", "Physical stock ownership or batch provenance is unavailable");
    internal sealed record LotFacts(long Id, long OrganizationId, long SkuId, long DefinitionId, long RootId, long? ParentId, long RecordedAt, DateOnly? ExpiresOn);
}
