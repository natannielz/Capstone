using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    /// <summary>Minimal frozen action context. It is not a configuration-history or drain capability.</summary>
    public async Task<BsonDocument> PreviewSuspensionAsync(ActorContext actor, long poolId, CancellationToken ct = default)
    {
        if (poolId <= 0) throw StockSourceSuspensionBson.Invalid();
        await SuspensionStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            var context = await SuspensionContextAsync(actor, snapshot, tx, poolId, Permissions.StockSourceSuspend, token);
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceSuspend, context.Resources, tx, token);
            return new BsonDocument
            {
                { "poolId", poolId }, { "poolRevision", context.Pool["revision"] }, { "active", context.Pool["active"] },
                { "suspensionRevision", context.Latest?["suspensionRevision"] ?? new BsonInt64(0) },
                { "configurationHash", StockSourceSuspensionBson.Hash(context.Configuration) },
                { "affectedLocations", new BsonArray(context.Locations.Select(StockSourceSetupBson.Location)) }
            };
        }, ct);
    }
    public Task<BsonDocument> SuspendAsync(ActorContext actor, long poolId, StockSourceSuspensionExpectation expected,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) => SuspendCoreAsync(actor, poolId, expected, metadata, false, ct);

    private async Task<BsonDocument> SuspendCoreAsync(ActorContext actor, long poolId, StockSourceSuspensionExpectation expected,
        AuthorizationCommandMetadata metadata, bool recoveryOnly, CancellationToken ct)
    {
        if (poolId <= 0 || expected is null) throw StockSourceSuspensionBson.Invalid();
        expected.Validate(); Metadata(metadata); await SuspensionStorageAsync(ct);
        var id = (await ids.NextAsync(StockSourceSuspensionSchemas.Collection, ct)).Value;
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        SuspensionContext? captured = null;
        return await AliasCommandAsync(actor, StockSourceSuspensionBson.Operation, Permissions.StockSourceSuspend, metadata,
            new { poolId, expected }, async (snapshot, tx, token) =>
            {
                captured = await SuspensionContextAsync(actor, snapshot, tx, poolId, Permissions.StockSourceSuspend, token);
                return (captured.Resources, null);
            }, async (tx, token) =>
            {
                var context = captured ?? throw StockSourceSuspensionBson.Invalid();
                var hash = StockSourceSuspensionBson.Hash(context.Configuration);
                if (context.Pool["revision"].AsInt64 != expected.PoolRevision ||
                    (context.Latest?["suspensionRevision"].AsInt64 ?? 0) != expected.SuspensionRevision || hash != expected.ConfigurationHash)
                    throw SuspensionChanged();
                if (!context.Pool["active"].AsBoolean && context.Latest is not null)
                {
                    var stopped = context.Latest["configuration"].DeepClone().AsBsonDocument;
                    stopped["source"]["active"] = false;
                    if (StockSourceSuspensionBson.Hash(stopped) == hash)
                        throw new PersistenceConflictException("stock_source_already_suspended", "This source configuration is already suspended");
                }
                var now = Now();
                if (now < context.ApprovedAt || context.Latest is not null && now < StockSourceSetupBson.Time(context.Latest["suspendedAt"]) ||
                    context.Pool["fence"].AsInt64 == long.MaxValue) throw StockSourceSuspensionBson.Invalid();
                var input = StockSourceSetupBson.ReadInput(context.Current["input"].AsBsonDocument);
                var row = StockSourceSuspensionBson.Header(id);
                row.Add("poolId", poolId); row.Add("poolRevision", expected.PoolRevision);
                row.Add("ownerLocation", StockSourceSetupBson.Location(input.Location)); row.Add("physicalKey", input.PhysicalKey);
                row.Add("suspensionRevision", checked(expected.SuspensionRevision + 1));
                row.Add("configuration", context.Configuration.DeepClone()); row.Add("configurationHash", hash);
                row.Add("suspendedBy", actor.UserId); row.Add("suspendedAt", now.UtcDateTime);
                StockSourceSetupBson.Metadata(row, auditId, metadata); StockSourceSuspensionBson.Require(row);
                // The same whole-pool fence is consumed by InventoryGraph writers and source configuration commands.
                var update = Builders<BsonDocument>.Update.Set("active", false).Inc("fence", 1L);
                if ((await Rows("inventoryPools").UpdateOneAsync(tx, context.Pool, update, cancellationToken: token)).ModifiedCount != 1)
                    throw SuspensionChanged();
                await Rows(StockSourceSuspensionSchemas.Collection).InsertOneAsync(tx, row, cancellationToken: token);
                await Rows("auditEvents").InsertOneAsync(tx, StockSourceSuspensionBson.Audit(row, actor, now), cancellationToken: token);
                return StockSourceSuspensionBson.Result(row);
            }, ct, recoveryOnly);
    }

    /// <summary>Immutable historical stop only. Neither inactive nor a suspension is accepted drain/handoff.</summary>
    public async Task<BsonDocument?> ReadSuspensionAsync(ActorContext actor, long poolId, CancellationToken ct = default)
    {
        if (poolId <= 0) throw StockSourceSuspensionBson.Invalid(); await SuspensionStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            var context = await SuspensionContextAsync(actor, snapshot, tx, poolId, Permissions.StockSourceRead, token);
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceRead, context.Resources, tx, token);
            return context.Latest?.DeepClone().AsBsonDocument;
        }, ct);
    }

    private sealed record SuspensionContext(BsonDocument Current, BsonDocument Configuration, BsonDocument? Latest,
        StockLocation[] Locations, ResourceScope[] Resources, DateTimeOffset ApprovedAt)
    {
        internal BsonDocument Pool => Current["pool"].AsBsonDocument;
    }
    private async Task<SuspensionContext> SuspensionContextAsync(ActorContext actor, AuthorizationSnapshot snapshot,
        IClientSessionHandle tx, long poolId, string permission, CancellationToken ct)
    {
        AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, permission, Now()));
        var projected = await Rows("inventoryPools").Find(tx, F.Eq("_id", poolId)).Project(new BsonDocument("location", 1)).SingleOrDefaultAsync(ct);
        if (projected is null) throw new AuthorizationDeniedException("scope_denied");
        var owner = InventoryGraph.Location(projected["location"].AsBsonDocument);
        Require(actor, snapshot, permission, Resource(owner));
        var locations = new HashSet<StockLocation> { owner };
        foreach (var collection in new[] { "stockSourceAliases", "stockSourceAliasDecisions" })
        {
            var filter = F.Eq("poolId", poolId);
            if (collection == "stockSourceAliasDecisions") filter &= F.Eq("decision", "approve");
            using var cursor = await Rows(collection).Find(tx, filter).Project(new BsonDocument("requestedLocation", 1)).ToCursorAsync(ct);
            while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current)
                locations.Add(InventoryGraph.Location(row["requestedLocation"].AsBsonDocument));
        }
        // A removed current mapping cannot erase a previously suspended channel's scope/unfinished obligations.
        using (var cursor = await Rows(StockSourceSuspensionSchemas.Collection).Find(tx, F.Eq("poolId", poolId))
            .Project(new BsonDocument("configuration.mappings.requestedLocation", 1)).ToCursorAsync(ct))
            while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current)
                foreach (var alias in row["configuration"]["mappings"].AsBsonArray)
                    locations.Add(InventoryGraph.Location(alias["requestedLocation"].AsBsonDocument));
        var ordered = locations.OrderBy(location => location.ChannelId.Value).ToArray();
        foreach (var location in ordered)
        {
            if (location != owner) StockSourceAliasPolicy.Validate(owner, location);
            Require(actor, snapshot, permission, Resource(location));
        }
        var current = await CurrentSourceAsync(tx, poolId, ct);
        var approvedAt = StockSourceSetupBson.Time(current["approvedAt"]);
        var aliases = new List<BsonDocument>();
        foreach (var location in ordered.Where(location => location != owner))
        {
            var view = await LatestAliasAsync(tx, poolId, location, ct) ?? throw StockSourceSuspensionBson.Invalid();
            RequireAliasPool(view, current); aliases.Add(view["alias"].AsBsonDocument);
            var at = StockSourceSetupBson.Time(view["decision"]["decidedAt"]); if (at > approvedAt) approvedAt = at;
        }
        var configuration = StockSourceSuspensionBson.Configuration(current, aliases);
        var latest = await LatestSuspensionAsync(tx, poolId, current, ct);
        return new(current, configuration, latest, ordered, ordered.Select(Resource).ToArray(), approvedAt);
    }
    private async Task SuspensionStorageAsync(CancellationToken ct)
    {
        await AliasStorageAsync(ct);
        await InventoryRepository.RequireStorageAsync(database, StockSourceSuspensionSchemas.Collection,
            StockSourceSuspensionSchemas.Validator(), StockSourceSuspensionSchemas.Key(), ct);
        foreach (var expected in StockSourceSuspensionSchemas.LookupIndexes())
        {
            var indexes = await (await Rows(expected.Collection).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (!indexes.Any(index => index["key"].AsBsonDocument.Equals(expected.Key) &&
                !index.GetValue("unique", false).AsBoolean && !index.GetValue("sparse", false).AsBoolean &&
                !index.GetValue("hidden", false).AsBoolean && !index.Contains("partialFilterExpression") && !index.Contains("expireAfterSeconds") &&
                (!index.TryGetValue("collation", out var collation) || collation.IsBsonDocument && collation.AsBsonDocument.Equals(new BsonDocument("locale", "simple")))))
                throw StockSourceSuspensionBson.Invalid();
        }
    }
    private static PersistenceConflictException SuspensionChanged() =>
        new("stock_source_suspension_changed", "The reviewed source configuration or suspension changed");
}
