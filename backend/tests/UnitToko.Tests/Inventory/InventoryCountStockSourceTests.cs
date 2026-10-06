using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Inventory;

// Genuine registered96 setup/count producers in isolated Mongo; every person,
// account and source input is synthetic. No operational activation or drain claim.
[Collection("Mongo")]
public sealed class InventoryCountStockSourceTests(MongoFixture fixture)
{
    [Fact]
    public async Task Genuine_inactive_setup_explicit_counts_and_original_keys_preserve_custom_legacy_protocol_and_never_complete_inventory_coverage() {
        await using var data = await Data.CreateAsync(fixture); Assert.Equal(96, CollectionMigrations.CurrentVersion);
        var original = await data.PhysicalAsync(); var pool = await data.PoolAsync(); var source = await data.StateAsync();
        Assert.False(pool["active"].AsBoolean); Assert.Equal(1L, (await data.HeadAsync())["revision"].AsInt64);
        var empty = await data.ReadSourceAsync(); Assert.False(empty.Complete); Assert.Equal(0L, empty.OpenCount); Assert.Equal(1L, empty.ProducerRevision);
        // The count-only/current-person actor can query genuine choices without
        // receiving stock.source.read or an automatic permission grant.
        var beforePermissions = new BsonArray((await data.Rows("authorizationGrants").Find(new BsonDocument("userId", data.Counter.UserId)).ToListAsync())
            .Select(row => { row.Remove("fence"); return row; }));
        var choice = Assert.Single((await data.Counts.SourcesAsync(data.Counter, Data.Warehouse, 0, 25)).Items);
        Assert.Equal(data.PoolId, choice.Pool.PoolId); Assert.False(choice.Pool.Active); Assert.Equal(Data.Warehouse, choice.RequestedLocation);
        Assert.Equal(beforePermissions, new BsonArray((await data.Rows("authorizationGrants").Find(new BsonDocument("userId", data.Counter.UserId)).ToListAsync())
            .Select(row => { row.Remove("fence"); return row; }))); // only the established managed read fence is normalized
        Assert.DoesNotContain(await data.Rows("authorizationGrants").Find(new BsonDocument("userId", data.Counter.UserId)).ToListAsync(),
            row => row["permissions"].AsBsonArray.Contains(Permissions.StockSourceRead));
        var unbound = data.Input(2); var legacy = await data.Counts.RecordCountAsync(data.Counter, unbound, Data.Meta("count-source-unbound"));
        var legacyRow = await data.Rows(StockCountBson.Collection).Find(new BsonDocument("_id", legacy.Id)).SingleAsync();
        Assert.Equal(1, legacyRow["schemaVersion"].AsInt32); Assert.False(legacyRow.Contains("poolBinding")); Assert.False(legacyRow.Contains("poolSourceHash"));
        var oldBody = new BsonDocument { { "location", StockCountBson.Scope(unbound.Location) }, { "skuId", unbound.SkuId }, { "expectedSkuRevision", unbound.ExpectedSkuRevision },
            { "expectedDefinitionId", unbound.ExpectedDefinitionId }, { "packages", unbound.Packages }, { "observedLotLabel", unbound.ObservedLotLabel ?? throw new InvalidOperationException("Expected explicit synthetic label") }, { "reason", Data.Meta("count-source-unbound").Reason } };
        Assert.Equal(StockCountBson.Hash(oldBody), legacyRow["bodyHash"].AsString); Assert.Null(legacy.PoolBinding);
        Assert.False(await data.Rows(InventoryCountStockSourceRecords.Associations).Find(new BsonDocument("_id", legacy.Id)).AnyAsync());
        var legacyAudit = await data.Rows("auditEvents").Find(new BsonDocument("_id", legacy.AuditId)).SingleAsync();
        var legacyReceipt = await data.Rows("idempotencyRecords").Find(new BsonDocument { { "operation", StockCountBson.Operation }, { "key", "count-source-unbound" } }).SingleAsync();
        var first = unbound with { PoolSelection = choice.Pool.Selection }; var second = first with { Packages = 0 };
        var firstSaved = await data.Counts.RecordCountAsync(data.Counter, first, Data.Meta("count-source-bound-one"));
        var secondSaved = await data.Counts.RecordCountAsync(data.Counter, second, Data.Meta("count-source-bound-zero"));
        Assert.Equal(choice.Pool, firstSaved.PoolBinding); Assert.Equal(choice.Pool, secondSaved.PoolBinding); Assert.Equal(0L, secondSaved.BaseUnits);
        var stable = await data.StateAsync();
        Assert.Equal(firstSaved, await data.Counts.RecordCountAsync(data.Counter, first, Data.Meta("count-source-bound-one")));
        Assert.Equal(firstSaved, await data.Counts.RecoverAsync(data.Counter, "count-source-bound-one"));
        Assert.Equal(firstSaved, await data.Counts.ReadAsync(data.Counter, firstSaved.Id));
        Assert.Equal(3, (await data.Counts.ListAsync(data.Counter, Data.Warehouse, 0, 25)).Items.Length);
        Assert.Equal(stable, await data.StateAsync());
        Assert.Equal("idempotency_key_reused", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Counts.RecordCountAsync(data.Counter,
            first with { PoolSelection = null }, Data.Meta("count-source-bound-one")))).Code);
        Assert.Equal(legacyRow, await data.Rows(StockCountBson.Collection).Find(new BsonDocument("_id", legacy.Id)).SingleAsync());
        Assert.Equal(legacyAudit, await data.Rows("auditEvents").Find(new BsonDocument("_id", legacy.AuditId)).SingleAsync());
        Assert.Equal(legacyReceipt, await data.Rows("idempotencyRecords").Find(new BsonDocument("_id", legacyReceipt["_id"])).SingleAsync());
        var after = await data.PoolAsync(); var expected = pool.DeepClone().AsBsonDocument; expected["fence"] = pool["fence"].AsInt64 + 2;
        Assert.Equal(expected, after); Assert.Equal(3L, (await data.HeadAsync())["revision"].AsInt64);
        Assert.Equal(2L, await data.Rows(InventoryCountStockSourceRecords.Associations).CountDocumentsAsync(new BsonDocument()));
        var partial = await data.ReadSourceAsync(); Assert.False(partial.Complete); Assert.Equal(2L, partial.OpenCount); Assert.Equal(3L, partial.ProducerRevision);
        Assert.Equal(expected, await data.PoolAsync()); Assert.Equal(original, await data.PhysicalAsync());
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", StockCountBson.Operation)).AnyAsync());
        Assert.NotEqual(source, await data.StateAsync());
    }
    [Fact]
    public async Task Concurrent_same_key_appends_one_count_and_current_foreign_scope_or_stale_source_is_denied_without_repair() {
        await using var data = await Data.CreateAsync(fixture); var physical = await data.PhysicalAsync();
        var choice = Assert.Single((await data.Counts.SourcesAsync(data.Counter, Data.Warehouse, 0, 25)).Items);
        var input = data.Input() with { PoolSelection = choice.Pool.Selection };
        async Task<StockCountObservation?> Record() {
            try { return await data.Counts.RecordCountAsync(data.Counter, input, Data.Meta("count-source-race")); }
            catch (PersistenceConflictException conflict) when (conflict.Code == "version_conflict") { return null; }
        }
        var results = await Task.WhenAll(Record(), Record()); var saved = Assert.Single(results.OfType<StockCountObservation>().Select(row => row.Id).Distinct());
        var original = await data.Counts.RecoverAsync(data.Counter, "count-source-race"); Assert.NotNull(original); Assert.Equal(saved, original.Id);
        foreach (var result in results) Assert.Equal(original, result ?? await data.Counts.RecoverAsync(data.Counter, "count-source-race"));
        Assert.Single(await data.Rows(StockCountBson.Collection).Find(new BsonDocument()).ToListAsync());
        Assert.Single(await data.Rows(InventoryCountStockSourceRecords.Associations).Find(new BsonDocument()).ToListAsync());
        Assert.Single(await data.Rows(InventoryCountStockSourceRecords.Transitions).Find(new BsonDocument("sourceKind", "count")).ToListAsync());
        var before = await data.StateAsync(); var foreign = await data.Catalog.ActorAsync(966400, Data.Foreign, Permissions.StockCount, Permissions.StockRead, Permissions.StockSourceRead);
        await PersonTestData.SeedDistinctPeopleAsync(data.Catalog.Database, data.Catalog.Clock, foreign.UserId);
        Assert.Equal("scope_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Counts.RecordCountAsync(foreign, input, Data.Meta("count-source-foreign")))).Code);
        Assert.Equal("scope_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Counts.SourcesAsync(foreign, Data.Warehouse, 0, 25))).Code);
        Assert.Null(await data.Counts.ReadAsync(foreign, saved));
        Assert.Equal("stock_source_revision_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Counts.RecordCountAsync(data.Counter,
            input with { PoolSelection = input.PoolSelection! with { ConfigurationHash = new string('f', 64) } }, Data.Meta("count-source-stale")))).Code);
        Assert.Equal(before, await data.StateAsync()); Assert.Equal(physical, await data.PhysicalAsync()); Assert.False((await data.PoolAsync())["active"].AsBoolean);
    }
    [Fact]
    public async Task Genuine_missing_head_custom_receipt_or_association_refuses_writes_reads_and_recovery_without_history_or_stock_repair() {
        await using var data = await Data.CreateAsync(fixture);
        var choice = Assert.Single((await data.Counts.SourcesAsync(data.Counter, Data.Warehouse, 0, 25)).Items);
        var input = data.Input() with { PoolSelection = choice.Pool.Selection }; var head = await data.HeadAsync();
        Assert.Equal(1L, (await data.Rows(InventoryCountStockSourceRecords.Heads).DeleteOneAsync(head)).DeletedCount);
        try {
            var before = await data.StateAsync(); var physical = await data.PhysicalAsync();
            Assert.Equal("inventory_count_source_obligation_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => data.Counts.RecordCountAsync(data.Counter,
                input, Data.Meta("count-source-missing-head")))).Code);
            Assert.Equal(before, await data.StateAsync()); Assert.Equal(physical, await data.PhysicalAsync());
        } finally { await data.Rows(InventoryCountStockSourceRecords.Heads).InsertOneAsync(head); }
        var result = await data.Counts.RecordCountAsync(data.Counter, input, Data.Meta("count-source-graph"));
        var association = await data.Rows(InventoryCountStockSourceRecords.Associations).Find(new BsonDocument("_id", result.Id)).SingleAsync();
        Assert.Equal(1L, (await data.Rows(InventoryCountStockSourceRecords.Associations).DeleteOneAsync(association)).DeletedCount);
        try {
            var before = await data.StateAsync();
            Assert.Equal("inventory_count_source_obligation_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => data.Counts.RecoverAsync(data.Counter, "count-source-graph"))).Code);
            Assert.Equal("inventory_count_source_obligation_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => data.ReadSourceAsync())).Code);
            Assert.Equal(before, await data.StateAsync());
        } finally { await data.Rows(InventoryCountStockSourceRecords.Associations).InsertOneAsync(association); }
        var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument { { "operation", StockCountBson.Operation }, { "key", "count-source-graph" } }).SingleAsync();
        Assert.Equal(1L, (await data.Rows("idempotencyRecords").DeleteOneAsync(receipt)).DeletedCount);
        try {
            var before = await data.StateAsync();
            Assert.Equal("inventory_count_source_obligation_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => data.Counts.RecoverAsync(data.Counter, "count-source-graph"))).Code);
            Assert.Equal("inventory_count_source_obligation_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => data.Counts.RecordCountAsync(data.Counter,
                input, Data.Meta("count-source-graph")))).Code); Assert.Equal(before, await data.StateAsync());
        } finally { await data.Rows("idempotencyRecords").InsertOneAsync(receipt); }
        var extra = receipt.DeepClone().AsBsonDocument; extra["_id"] = ObjectId.GenerateNewId(); extra["key"] = "count-source-extra-linked-receipt";
        await data.Rows("idempotencyRecords").InsertOneAsync(extra);
        try {
            var before = await data.StateAsync();
            Assert.Equal("inventory_count_source_obligation_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => data.ReadSourceAsync())).Code);
            Assert.Equal(before, await data.StateAsync());
        } finally { Assert.Equal(1L, (await data.Rows("idempotencyRecords").DeleteOneAsync(extra)).DeletedCount); }
        Assert.Equal(result, await data.Counts.RecoverAsync(data.Counter, "count-source-graph"));
    }
    [Fact]
    public async Task Required_audit_failure_and_final_session_expiry_after_real_canonical_CAS_rollback_observation_and_both_source_owners() {
        await using var data = await Data.CreateAsync(fixture);
        var choice = Assert.Single((await data.Counts.SourcesAsync(data.Counter, Data.Warehouse, 0, 25)).Items);
        var input = data.Input() with { PoolSelection = choice.Pool.Selection };
        var definition = await (await data.Catalog.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "auditEvents") })).SingleAsync();
        var validator = definition["options"]["validator"].DeepClone(); var before = await data.StateAsync(); var physical = await data.PhysicalAsync();
        await data.Catalog.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", new BsonDocument("$and", new BsonArray {
            validator, new BsonDocument("kind", new BsonDocument("$ne", StockCountBson.AuditKind)) }) } });
        try { await Assert.ThrowsAsync<MongoWriteException>(() => data.Counts.RecordCountAsync(data.Counter, input, Data.Meta("count-source-audit-rollback")));
            Assert.Equal(before, await data.StateAsync()); Assert.Equal(physical, await data.PhysicalAsync());
        } finally { await data.Catalog.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", validator } }); }
        var originalTime = data.Catalog.Clock.UtcNow; var deadline = originalTime.AddSeconds(1);
        await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Counter.Session!.Id),
            Builders<BsonDocument>.Update.Set("idleExpiresAt", deadline.UtcDateTime));
        var pool = await data.PoolAsync(); var request = 0; var fired = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "M96CountFinalFactor-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(value => {
                if (Volatile.Read(ref request) != 0 || value.DatabaseNamespace.DatabaseName != data.Catalog.Database.DatabaseNamespace.DatabaseName ||
                    value.CommandName != "update" || value.Command.GetValue("update", BsonNull.Value) != "inventoryPools") return;
                foreach (var update in value.Command["updates"].AsBsonArray)
                    if (update["q"]["_id"] == data.PoolId && update["u"].AsBsonDocument.GetValue("$inc", new BsonDocument()).AsBsonDocument.GetValue("fence", 0L) == 1L)
                        Interlocked.CompareExchange(ref request, value.RequestId, 0);
            });
            builder.Subscribe<CommandSucceededEvent>(value => {
                if (request != 0 && value.RequestId == request && value.Reply.GetValue("nModified", 0).ToInt64() == 1 && Interlocked.CompareExchange(ref fired, 1, 0) == 0)
                    data.Catalog.Clock.UtcNow = deadline;
            });
        };
        using var client = new MongoClient(settings); var database = client.GetDatabase(data.Catalog.Database.DatabaseNamespace.DatabaseName);
        var service = new StockCountService(database, new(client), new(database), data.Catalog.Clock);
        try {
            var failure = await Record.ExceptionAsync(() => service.RecordCountAsync(data.Counter, input, Data.Meta("count-source-final-factor")));
            Assert.Equal(1, fired); Assert.Equal(deadline, data.Catalog.Clock.UtcNow);
            Assert.Equal("stale_or_inactive_session", Assert.IsType<AuthorizationDeniedException>(failure).Code);
            Assert.Equal(before, await data.StateAsync()); Assert.Equal(pool, await data.PoolAsync());
            Assert.Equal(physical, await data.PhysicalAsync());
        } finally { data.Catalog.Clock.UtcNow = originalTime; }
        Assert.Null(await data.Counts.RecoverAsync(data.Counter, "count-source-final-factor")); Assert.Equal(before, await data.StateAsync());
    }

    internal sealed class Data(CatalogAuthoringTestData catalog) : IAsyncDisposable
    {
        internal static readonly GrantScope Warehouse = CatalogAuthoringTestData.Warehouse;
        internal static readonly GrantScope Foreign = new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 201, WarehouseId: 202, ChannelId: 203);
        internal CatalogAuthoringTestData Catalog => catalog;
        internal ActorContext Proposer = null!, Reviewer = null!, Counter = null!;
        internal long PoolId, SkuId, DefinitionId;
        internal MongoUnitOfWork Unit => new(catalog.Client);
        internal StockCountService Counts => new(catalog.Database, Unit, new(catalog.Database), catalog.Clock);
        internal StockSourceSetupService Sources => new(catalog.Database, Unit, new(catalog.Database), catalog.Clock);
        internal IMongoCollection<BsonDocument> Rows(string name) => catalog.Rows(name);
        internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic explicit count-source verification", "count-source-native");
        internal StockCountInput Input(int packages = 2) => new(Warehouse, SkuId, 1, DefinitionId, packages, "OBSERVED-UNVERIFIED-LOT");
        internal static async Task<Data> CreateAsync(MongoFixture fixture) {
            var catalog = await CatalogAuthoringTestData.CreateAsync(fixture); var data = new Data(catalog);
            try {
                Assert.Equal(96, CollectionMigrations.CurrentVersion); Assert.True(await new CollectionMigrations(catalog.Database, catalog.Clock).IsCurrentAsync());
                var sku = await catalog.SkuAsync(12, "COUNT-SOURCE-12", "CNT"); data.SkuId = sku.Id.Value; data.DefinitionId = sku.CurrentDefinitionId!.Value.Value;
                data.Proposer = await catalog.ActorAsync(966100, Warehouse, Permissions.StockSourcePropose, Permissions.StockSourceRead);
                data.Reviewer = await catalog.ActorAsync(966200, Warehouse, Permissions.StockSourceApprove, Permissions.StockSourceRead);
                data.Counter = await catalog.ActorAsync(966300, Warehouse, Permissions.StockCount, Permissions.StockRead);
                await PersonTestData.SeedDistinctPeopleAsync(catalog.Database, catalog.Clock, data.Proposer.UserId, data.Reviewer.UserId, data.Counter.UserId);
                var location = StockSourceSetupService.StockCountLocation(Warehouse);
                var proposed = await data.Sources.ProposeAsync(data.Proposer, new(location, "synthetic-explicit-count-source", "Synthetic approved inactive count destination",
                    StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger, 60000, 0, null, null, "Synthetic discrepancy owner", "Synthetic handoff"), Meta("count-source-setup"));
                var approved = await data.Sources.DecideAsync(data.Reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
                    "Independent synthetic source review", Meta("count-source-setup-approved")); data.PoolId = approved["poolId"].AsInt64;
                Assert.False((await data.PoolAsync())["active"].AsBoolean); return data;
            } catch { await data.DisposeAsync(); throw; }
        }
        internal Task<BsonDocument> PoolAsync() => Rows("inventoryPools").Find(new BsonDocument("_id", PoolId)).SingleAsync();
        internal Task<BsonDocument> HeadAsync() => Rows(InventoryCountStockSourceRecords.Heads).Find(new BsonDocument("_id", PoolId)).SingleAsync();
        internal async Task<StockSourceObligationResult> ReadSourceAsync() {
            var pool = await PoolAsync(); var expected = new StockSourceObligationExpectation(PoolId, InventoryGraph.Location(pool["location"].AsBsonDocument), pool["revision"].AsInt64, pool["fence"].AsInt64);
            return await Unit.ExecuteAsync((tx, ct) => new InventoryCountStockSourceObligationReader(catalog.Database, Unit, new(catalog.Database), catalog.Clock)
                .ReadAndFenceAsync(tx, Reviewer, expected, ct));
        }
        internal async Task<BsonDocument> StateAsync() {
            var state = new BsonDocument();
            foreach (var name in new[] { "inventoryPools", InventoryCountStockSourceRecords.Heads, InventoryCountStockSourceRecords.Transitions,
                InventoryCountStockSourceRecords.Associations, StockCountBson.Collection }) state[name] = new BsonArray(await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
            foreach (var (name, field, operation) in new[] { ("auditEvents", "kind", StockCountBson.AuditKind), ("idempotencyRecords", "operation", StockCountBson.Operation), ("outboxEvents", "kind", StockCountBson.Operation) })
                state[name] = new BsonArray(await Rows(name).Find(new BsonDocument(field, operation)).Sort(new BsonDocument("_id", 1)).ToListAsync());
            return state;
        }
        internal async Task<BsonDocument> PhysicalAsync() {
            var state = new BsonDocument();
            foreach (var name in new[] { "stockBalances", "lots", "movements", "reservations", "journalEntries", "invoices", "paymentSubmissions", "paymentAllocations", "cashMovements" })
                state[name] = new BsonArray(await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
            return state;
        }
        public ValueTask DisposeAsync() => catalog.DisposeAsync();
    }
}
