using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Organizations;
using D = UnitToko.Tests.Procurement.SupplierBankTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class SupplierBankMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Empty_upgrade_preserves_genuine_supplier_history_and_stronger_guards_without_inventing_bank_policy_or_beneficiary()
    {
        await using var data = await SupplierTestData.CreateAsync(fixture);
        var supplier = await data.ProposeAsync("bank-migration-supplier"); var observation = await data.ObserveAsync(supplier, "bank-migration-supplier-observed");
        await data.DecideAsync(supplier, "bank-migration-supplier-approved", observation["recordId"].AsInt64);
        await RestoreV64Async(data.Procurement.Organizations);
        var guard = new BsonDocument("beneficiaryId", new BsonDocument("$ne", 999999L));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", D.Proposals }, { "validator", new BsonDocument("$and", new BsonArray { SupplierBankSchemas.Validator(D.Proposals), guard }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        await data.Rows(D.Proposals).Indexes.DropOneAsync("ux_" + D.Proposals + "_0");
        var before = await DefinitionsAsync(data.Database); var preserved = await PreservedAsync(data.Database); var history = await HistoryAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(64))); Assert.Equal(preserved, await PreservedAsync(data.Database));
        var after = await DefinitionsAsync(data.Database);
        Assert.Equal(before.Where(row => !SupplierBankSchemas.Collections.Contains(row["name"].AsString)), after.Where(row => !SupplierBankSchemas.Collections.Contains(row["name"].AsString)));
        foreach (var name in SupplierBankSchemas.Collections) {
            var current = Assert.Single(after, row => row["name"] == name); Assert.Equal(Assert.Single(before, row => row["name"] == name)["info"]["uuid"], current["info"]["uuid"]);
            Assert.True(InventorySchemas.Contains(current["options"]["validator"].AsBsonDocument, SupplierBankSchemas.Validator(name)));
            Assert.Equal("strict", current["options"]["validationLevel"]); Assert.Equal("error", current["options"]["validationAction"]);
            var indexes = await (await data.Rows(name).Indexes.ListAsync()).ToListAsync();
            foreach (var key in SupplierBankSchemas.Keys(name)) { var index = Assert.Single(indexes, row => row["key"] == key); Assert.True(index["name"] == "_id_" || index.GetValue("unique", false).AsBoolean); }
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
        }
        Assert.True(InventorySchemas.Contains(Assert.Single(after, row => row["name"] == D.Proposals)["options"]["validator"].AsBsonDocument, guard));
        var applied = await HistoryAsync(data.Database); await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(applied, await HistoryAsync(data.Database)); Assert.Equal(after, await DefinitionsAsync(data.Database)); Assert.Equal(preserved, await PreservedAsync(data.Database));
    }

    [Theory]
    [InlineData("row")] [InlineData("ttl")] [InlineData("counter")] [InlineData("orphan")] [InlineData("retained_supplier")]
    public async Task Unsafe_partial_bank_install_or_retained_supplier_storage_refuses_before_DDL(string fault)
    {
        // M65 reads retained source storage even when no beneficiary policy has been supplied.
        await using var data = await SupplierTestData.CreateAsync(fixture); await RestoreV64Async(data.Procurement.Organizations);
        if (fault == "row") await data.Rows(D.Proposals).InsertOneAsync(new BsonDocument("_id", 1L), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "ttl") await data.Rows(D.Proposals).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "counter") await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", D.Proposals }, { "value", long.MaxValue } });
        if (fault == "orphan") await data.Rows("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", D.Prefix + "approve" } }, new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "retained_supplier") await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", SupplierTestData.Proposals }, { "validationLevel", "off" } });
        var before = await DefinitionsAsync(data.Database); var preserved = await PreservedAsync(data.Database);
        if (fault == "retained_supplier") Assert.Equal("inventory_storage_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync())).Code);
        else Assert.Equal("Supplier bank migration preflight failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync())).Message);
        Assert.Equal(before, await DefinitionsAsync(data.Database)); Assert.Equal(preserved, await PreservedAsync(data.Database));
        Assert.False(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 65 }, { "state", "applied" } }).AnyAsync());
    }

    [Fact]
    public async Task Bank_policy_and_proposal_schema_reject_ambiguous_identity_or_unapproved_verification_without_replacing_history()
    {
        await using var data = await D.CreateAsync(fixture); var policy = await data.Rows(D.Policies).Find(new BsonDocument()).SingleAsync();
        foreach (var field in new[] { "trustedChannelReference", "authorizationReference", "operatingOwnerReference", "approvalReference", "supplierVersionId" }) {
            var invalid = policy.DeepClone().AsBsonDocument; invalid.Remove(field);
            Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows(D.Policies).ReplaceOneAsync(new BsonDocument("_id", D.PolicyId), invalid))).WriteError.Code);
            Assert.Equal(policy, await data.Rows(D.Policies).Find(new BsonDocument()).SingleAsync());
        }
        await data.ProposeAsync("bank-schema"); var original = await data.Rows(D.Proposals).Find(new BsonDocument()).SingleAsync();
        foreach (var fault in new[] { "actor", "account", "supplier", "unknown" }) {
            var invalid = original.DeepClone().AsBsonDocument;
            if (fault == "actor") invalid["actorId"] = new BsonDouble(650);
            if (fault == "account") invalid["input"]["details"]["accountNumber"] = 12345678L;
            if (fault == "supplier") invalid["input"]["supplierVersionId"] = "1";
            if (fault == "unknown") invalid["transferAllowed"] = true;
            Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows(D.Proposals).ReplaceOneAsync(new BsonDocument("_id", original["_id"]), invalid))).WriteError.Code);
            Assert.Equal(original, await data.Rows(D.Proposals).Find(new BsonDocument()).SingleAsync());
        }
        await data.NoFinancialEffectsAsync();
    }

    internal static async Task RestoreV64Async(OrganizationAddressTestData data)
    {
        var history = await HistoryAsync(data.Database); Assert.Equal(CollectionMigrations.CurrentVersion, history.Count);
        Assert.All(history, row => Assert.Equal("applied", row["state"])); Assert.Equal(new[] { 65 }, history.Skip(64).Select(row => row["_id"].AsInt32));
        foreach (var name in SupplierBankSchemas.Collections) {
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync()); Assert.False(await data.Rows("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
        var prefix = new BsonRegularExpression(@"^procurement\.supplierbank(?:[.:]|$)");
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation") })
            Assert.False(await data.Rows(table).Find(new BsonDocument(field, prefix)).AnyAsync());
        Assert.Equal(1, (await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 65 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(64)), await HistoryAsync(data.Database));
    }
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) => new(await database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<List<BsonDocument>> DefinitionsAsync(IMongoDatabase database) => (await (await database.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonDocument> PreservedAsync(IMongoDatabase database) {
        var state = new BsonDocument();
        foreach (var table in new[] { "organizationProfiles", "organizationProposals", "authorizationHistory", "authorizationScopes", "catalogCategories", "productFamilies", "skus", "variantDefinitions", "idempotencyRecords", "auditEvents", "outboxEvents" }.Concat(ProcurementRequestSchemas.Collections).Concat(SupplierSchemas.Collections).Concat(PurchaseOrderSchemas.Collections).Concat(ProcurementFundingSchemas.Collections))
            state[table] = new BsonArray(await database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
}
