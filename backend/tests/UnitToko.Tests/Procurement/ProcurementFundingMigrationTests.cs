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
public sealed class ProcurementFundingMigrationTests(MongoFixture fixture)
{
    [Fact(Skip = Retired.HistoricalUpgrade)]
    public async Task Empty_upgrade_preserves_genuine_request_supplier_history_and_stronger_existing_guards_without_inventing_funding_or_operational_policy()
    {
        await using var data = await D.CreateAsync(fixture);
        var retainedOrder = await data.ProposeAsync("funding-migration-retained-po"); await data.DecideAsync(retainedOrder, "funding-migration-retained-po-approved");
        await RestoreV63Async(data.Procurement.Organizations);
        var guard = new BsonDocument("requestId", new BsonDocument("$ne", 999999L));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", ProcurementFundingTestData.Requests }, { "validator", new BsonDocument("$and", new BsonArray { ProcurementFundingSchemas.Validator(ProcurementFundingTestData.Requests), guard }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        await data.Rows(ProcurementFundingTestData.Requests).Indexes.DropOneAsync("ux_" + ProcurementFundingTestData.Requests + "_0");
        var before = await DefinitionsAsync(data.Database); var preserved = await PreservedAsync(data.Database); var history = await HistoryAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(63))); Assert.Equal(preserved, await PreservedAsync(data.Database));
        var after = await DefinitionsAsync(data.Database);
        Assert.Equal(before.Where(row => !ProcurementFundingSchemas.Collections.Contains(row["name"].AsString)), after.Where(row => !ProcurementFundingSchemas.Collections.Contains(row["name"].AsString)));
        foreach (var name in ProcurementFundingSchemas.Collections) {
            var current = Assert.Single(after, row => row["name"] == name); Assert.Equal(Assert.Single(before, row => row["name"] == name)["info"]["uuid"], current["info"]["uuid"]);
            Assert.True(InventorySchemas.Contains(current["options"]["validator"].AsBsonDocument, ProcurementFundingSchemas.Validator(name)));
            Assert.Equal("strict", current["options"]["validationLevel"]); Assert.Equal("error", current["options"]["validationAction"]);
            var indexes = await (await data.Rows(name).Indexes.ListAsync()).ToListAsync();
            foreach (var key in ProcurementFundingSchemas.Keys(name)) { var index = Assert.Single(indexes, row => row["key"] == key); Assert.True(index["name"] == "_id_" || index.GetValue("unique", false).AsBoolean); }
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
        }
        Assert.True(InventorySchemas.Contains(Assert.Single(after, row => row["name"] == ProcurementFundingTestData.Requests)["options"]["validator"].AsBsonDocument, guard));
        var applied = await HistoryAsync(data.Database); await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(applied, await HistoryAsync(data.Database)); Assert.Equal(after, await DefinitionsAsync(data.Database)); Assert.Equal(preserved, await PreservedAsync(data.Database));
    }

    [Theory(Skip = Retired.HistoricalUpgrade)]
    [InlineData("row")] [InlineData("ttl")] [InlineData("counter")] [InlineData("orphan")]
    [InlineData("retained_request")] [InlineData("retained_supplier")] [InlineData("retained_order")]
    public async Task Unsafe_partial_install_or_retained_source_storage_refuses_before_any_DDL(string fault)
    {
        await using var data = await D.CreateAsync(fixture);
        var retainedOrder = await data.ProposeAsync("funding-migration-retained-po"); await data.DecideAsync(retainedOrder, "funding-migration-retained-po-approved");
        await RestoreV63Async(data.Procurement.Organizations);
        if (fault == "row") await data.Rows(ProcurementFundingTestData.Requests).InsertOneAsync(new BsonDocument("_id", 1L), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "ttl") await data.Rows(ProcurementFundingTestData.Requests).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "counter") await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", ProcurementFundingTestData.Requests }, { "value", long.MaxValue } });
        if (fault == "orphan") await data.Rows("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", ProcurementFundingTestData.Prefix + "approve" } }, new InsertOneOptions { BypassDocumentValidation = true });
        if (fault.StartsWith("retained_", StringComparison.Ordinal)) {
            var table = fault == "retained_request" ? ProcurementRequestTestData.Requests : fault == "retained_supplier" ? SupplierTestData.Proposals : PurchaseOrderTestData.Orders;
            await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validationLevel", "off" } });
        }
        var before = await DefinitionsAsync(data.Database); var preserved = await PreservedAsync(data.Database);
        if (fault.StartsWith("retained_", StringComparison.Ordinal))
            Assert.Equal("inventory_storage_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync())).Code);
        else Assert.Equal("Procurement funding migration preflight failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync())).Message);
        Assert.Equal(before, await DefinitionsAsync(data.Database)); Assert.Equal(preserved, await PreservedAsync(data.Database));
        Assert.False(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 64 }, { "state", "applied" } }).AnyAsync());
    }

    [Fact]
    public async Task Stored_policy_and_authorization_schemas_reject_ambiguous_money_dates_and_capacity()
    {
        await using var data = await ProcurementFundingTestData.CreateAsync(fixture); var request = await data.RequestAsync("funding-schema"); await data.DecideAsync(request, "funding-schema-approved");
        foreach (var (table, field, value) in new[] {
            (ProcurementFundingTestData.Policies, "reserveUndisbursed", (BsonValue)false),
            (ProcurementFundingTestData.Policies, "eligibleRoutes", (BsonValue)new BsonArray { "ddo", "ddo" }),
            (ProcurementFundingTestData.Requests, "actorId", (BsonValue)new BsonDouble(640)),
            (ProcurementFundingTestData.Allocations, "amount", (BsonValue)"0.00"),
            (ProcurementFundingTestData.Heads, "authorized", (BsonValue)"20000.01") }) {
            var original = await data.Rows(table).Find(new BsonDocument()).SingleAsync(); var invalid = original.DeepClone().AsBsonDocument; invalid[field] = value;
            Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows(table).ReplaceOneAsync(new BsonDocument("_id", original["_id"]), invalid))).WriteError.Code);
            Assert.Equal(original, await data.Rows(table).Find(new BsonDocument()).SingleAsync());
        }
        var proposal = await data.Rows(ProcurementFundingTestData.Requests).Find(new BsonDocument()).SingleAsync();
        foreach (var (field, value) in new[] { ("amount", (BsonValue)new BsonDecimal128(10000m)), ("neededBy", (BsonValue)"2026-02-30") }) {
            var invalid = proposal.DeepClone().AsBsonDocument; invalid["input"][field] = value;
            Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows(ProcurementFundingTestData.Requests).ReplaceOneAsync(new BsonDocument("_id", proposal["_id"]), invalid))).WriteError.Code);
        }
        Assert.Equal(proposal, await data.Rows(ProcurementFundingTestData.Requests).Find(new BsonDocument()).SingleAsync());
    }

    internal static async Task RestoreV63Async(OrganizationAddressTestData data)
    {
        await SupplierBankMigrationTests.RestoreV64Async(data);
        var history = await HistoryAsync(data.Database); Assert.Equal(64, history.Count);
        Assert.All(history, row => Assert.Equal("applied", row["state"])); Assert.Equal(new[] { 64 }, history.Skip(63).Select(row => row["_id"].AsInt32));
        foreach (var name in ProcurementFundingSchemas.Collections) {
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync()); Assert.False(await data.Rows("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
        var prefix = new BsonRegularExpression(@"^procurement\.funding(?:[.:]|$)");
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation") })
            Assert.False(await data.Rows(table).Find(new BsonDocument(field, prefix)).AnyAsync());
        Assert.Equal(1, (await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 64 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(63)), await HistoryAsync(data.Database));
    }
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) => new(await database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<List<BsonDocument>> DefinitionsAsync(IMongoDatabase database) => (await (await database.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonDocument> PreservedAsync(IMongoDatabase database) {
        var state = new BsonDocument();
        foreach (var table in new[] { "organizationProfiles", "organizationProposals", "authorizationHistory", "authorizationScopes", "catalogCategories", "productFamilies", "skus", "variantDefinitions", "idempotencyRecords", "auditEvents", "outboxEvents" }.Concat(ProcurementRequestSchemas.Collections).Concat(SupplierSchemas.Collections).Concat(PurchaseOrderSchemas.Collections))
            state[table] = new BsonArray(await database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
}
