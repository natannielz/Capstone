using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Organizations;
using D = UnitToko.Tests.Procurement.PurchaseOrderTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class PurchaseOrderMigrationTests(MongoFixture fixture)
{
    [Fact(Skip = Retired.HistoricalUpgrade)]
    public async Task Empty_upgrade_preserves_genuine_request_supplier_history_and_stronger_existing_guards_without_inventing_orders()
    {
        await using var data = await D.CreateAsync(fixture); await RestoreV62Async(data.Procurement.Organizations);
        var guard = new BsonDocument("requestId", new BsonDocument("$ne", 999999L));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", D.Proposals }, { "validator", new BsonDocument("$and", new BsonArray { PurchaseOrderSchemas.Validator(D.Proposals), guard }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        await data.Rows(D.Proposals).Indexes.DropOneAsync("ux_" + D.Proposals + "_0");
        var before = await DefinitionsAsync(data.Database); var preserved = await PreservedAsync(data.Database); var history = await HistoryAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(62))); Assert.Equal(preserved, await PreservedAsync(data.Database));
        var after = await DefinitionsAsync(data.Database);
        Assert.Equal(before.Where(row => !PurchaseOrderSchemas.Collections.Contains(row["name"].AsString)), after.Where(row => !PurchaseOrderSchemas.Collections.Contains(row["name"].AsString)));
        foreach (var name in PurchaseOrderSchemas.Collections) {
            var current = Assert.Single(after, row => row["name"] == name); Assert.Equal(Assert.Single(before, row => row["name"] == name)["info"]["uuid"], current["info"]["uuid"]);
            Assert.True(InventorySchemas.Contains(current["options"]["validator"].AsBsonDocument, PurchaseOrderSchemas.Validator(name)));
            Assert.Equal("strict", current["options"]["validationLevel"]); Assert.Equal("error", current["options"]["validationAction"]);
            var indexes = await (await data.Rows(name).Indexes.ListAsync()).ToListAsync();
            foreach (var key in PurchaseOrderSchemas.Keys(name)) Assert.True(Assert.Single(indexes, row => row["key"] == key)["unique"].AsBoolean);
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
        }
        Assert.True(InventorySchemas.Contains(Assert.Single(after, row => row["name"] == D.Proposals)["options"]["validator"].AsBsonDocument, guard));
        var applied = await HistoryAsync(data.Database); await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(applied, await HistoryAsync(data.Database)); Assert.Equal(after, await DefinitionsAsync(data.Database)); Assert.Equal(preserved, await PreservedAsync(data.Database));
    }

    [Theory(Skip = Retired.HistoricalUpgrade)]
    [InlineData("row")] [InlineData("ttl")] [InlineData("counter")] [InlineData("orphan")]
    [InlineData("retained_request")] [InlineData("retained_supplier")]
    public async Task Unsafe_partial_install_or_retained_source_storage_refuses_before_any_DDL(string fault)
    {
        await using var data = await D.CreateAsync(fixture); await RestoreV62Async(data.Procurement.Organizations);
        if (fault == "row") await data.Rows(D.Proposals).InsertOneAsync(new BsonDocument("_id", 1L), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "ttl") await data.Rows(D.Proposals).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "counter") await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", D.Proposals }, { "value", long.MaxValue } });
        if (fault == "orphan") await data.Rows("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", D.Prefix + "approve" } }, new InsertOneOptions { BypassDocumentValidation = true });
        if (fault.StartsWith("retained_", StringComparison.Ordinal)) {
            var table = fault == "retained_request" ? ProcurementRequestTestData.Requests : SupplierTestData.Proposals;
            await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validationLevel", "off" } });
        }
        var before = await DefinitionsAsync(data.Database); var preserved = await PreservedAsync(data.Database);
        if (fault.StartsWith("retained_", StringComparison.Ordinal))
            Assert.Equal("inventory_storage_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync())).Code);
        else Assert.Equal("Purchase order migration preflight failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync())).Message);
        Assert.Equal(before, await DefinitionsAsync(data.Database)); Assert.Equal(preserved, await PreservedAsync(data.Database));
        Assert.False(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 63 }, { "state", "applied" } }).AnyAsync());
    }

    [Fact]
    public async Task Real_order_and_allocation_schemas_reject_malformed_values_without_rewriting_approved_records()
    {
        await using var data = await D.CreateAsync(fixture); var proposal = await data.ProposeAsync("po-schema"); await data.DecideAsync(proposal, "po-schema-approved");
        foreach (var (table, field, value) in new[] { (D.Proposals, "actorId", (BsonValue)new BsonDouble(630)), (D.Orders, "version", (BsonValue)"1"),
            (ProcurementRequestPurchaseOrderSchemas.Commitments, "committedPackages", (BsonValue)7), (ProcurementRequestPurchaseOrderSchemas.Allocations, "packages", (BsonValue)0) }) {
            var original = await data.Rows(table).Find(new BsonDocument()).SingleAsync(); var invalid = original.DeepClone().AsBsonDocument; invalid[field] = value;
            Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows(table).ReplaceOneAsync(new BsonDocument("_id", original["_id"]), invalid))).WriteError.Code);
            Assert.Equal(original, await data.Rows(table).Find(new BsonDocument()).SingleAsync());
        }
    }

    internal static async Task RestoreV62Async(OrganizationAddressTestData data)
    {
        await ProcurementFundingMigrationTests.RestoreV63Async(data);
        var history = await HistoryAsync(data.Database); Assert.Equal(63, history.Count);
        Assert.All(history, row => Assert.Equal("applied", row["state"])); Assert.Equal(new[] { 63 }, history.Skip(62).Select(row => row["_id"].AsInt32));
        foreach (var name in PurchaseOrderSchemas.Collections) {
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync()); Assert.False(await data.Rows("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
        var prefix = new BsonRegularExpression(@"^procurement\.purchase\.order(?:[.:]|$)");
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation") })
            Assert.False(await data.Rows(table).Find(new BsonDocument(field, prefix)).AnyAsync());
        Assert.Equal(1, (await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 63 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(62)), await HistoryAsync(data.Database));
    }
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) => new(await database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<List<BsonDocument>> DefinitionsAsync(IMongoDatabase database) => (await (await database.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonDocument> PreservedAsync(IMongoDatabase database) {
        var state = new BsonDocument();
        foreach (var table in new[] { "organizationProfiles", "organizationProposals", "authorizationHistory", "authorizationScopes", "catalogCategories", "productFamilies", "skus", "variantDefinitions", "idempotencyRecords", "auditEvents", "outboxEvents" }.Concat(ProcurementRequestSchemas.Collections).Concat(SupplierSchemas.Collections))
            state[table] = new BsonArray(await database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
}
