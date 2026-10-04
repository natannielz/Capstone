using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;
using B = UnitToko.Platform.Procurement.ProcurementAdvanceReturnBson;
using P = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;
using V = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementAdvanceReturnMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Empty_upgrade_is_resumable_preserves_stronger_guards_and_does_not_create_financial_owners()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); await RestoreV75Async(data.Database);
        var required = (await DefinitionAsync(data.Database, P.AdvanceHeads))["options"]["validator"];
        var guard = new BsonDocument("_id", new BsonDocument("$ne", 999999L));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", P.AdvanceHeads },
            { "validator", new BsonDocument("$and", new BsonArray { required, guard }) } });
        var rows = data.Database.GetCollection<BsonDocument>(B.Proposals);
        await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("policyId", 1),
            new CreateIndexOptions<BsonDocument> { Name = "unexpected_return_partial", Unique = true,
                PartialFilterExpression = new BsonDocument("version", new BsonDocument("$gt", 1L)) }));
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await rows.Indexes.DropOneAsync("unexpected_return_partial"); }
        await data.Database.DropCollectionAsync(B.Lines); var before = await SnapshotAsync(data.Database, definitions: false);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        var after = await SnapshotAsync(data.Database, definitions: false);
        foreach (var table in before.Names) Assert.Equal(before[table], after[table]);
        Assert.True(InventorySchemas.Contains((await DefinitionAsync(data.Database, P.AdvanceHeads))["options"]["validator"].AsBsonDocument, guard));
        foreach (var table in B.Collections) {
            Assert.False(await data.Database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
            await InventoryRepository.RequireStorageAsync(data.Database, table, ProcurementAdvanceReturnSchemas.Validator(table), ProcurementAdvanceReturnSchemas.Keys(table), default);
        }
        foreach (var table in new[] { P.Payables, P.Applications, P.AdvanceHeads, V.Parties, "journalEntries", "cashMovements" })
            Assert.False(await data.Database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
        var installed = await SnapshotAsync(data.Database); await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(installed, await SnapshotAsync(data.Database));
    }

    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Genuine_v1_payable_application_survives_additive_upgrade_and_missing_journal_fails_before_DDL()
    {
        await using var data = await ProcurementSupplierPayableTestData.CreateAsync(fixture);
        var recognized = await data.DecideAsync((await data.ProposeAsync("return-migration-recognize"))["proposalId"].AsInt64, "return-migration-recognized");
        var applied = await data.DecideAsync((await data.ProposeAsync("return-migration-apply", data.Apply(recognized["payableId"].AsInt64, 1000)))["proposalId"].AsInt64, "return-migration-applied");
        Assert.Equal("1000.00", (await data.Rows(P.AdvanceHeads).Find(new BsonDocument("_id", data.SettlementId)).SingleAsync())["appliedAmount"]);
        Assert.Equal(applied, (await data.Service.LookupAsync(data.Checker, P.DecideOperation, "return-migration-applied"))["result"]);
        await RestoreV75Async(data.Database); var before = await SnapshotAsync(data.Database, definitions: false);
        foreach (var (table, filter) in new[] {
            ("journalEntries", new BsonDocument("_id", applied["journalId"])),
            (P.AdvanceHeads, new BsonDocument("_id", data.SettlementId)),
            (P.Applications, new BsonDocument("_id", applied["applicationId"])) }) {
            var row = await data.Rows(table).Find(filter).SingleAsync(); await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", row["_id"]));
            try { await RefusesAsync(data.Database, data.Clock); }
            finally { await data.Rows(table).InsertOneAsync(row); }
        }
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(before, await SnapshotAsync(data.Database, definitions: false));
        var head = await data.Rows(P.AdvanceHeads).Find(new BsonDocument("_id", data.SettlementId)).SingleAsync();
        Assert.False(head.Contains("stateVersion")); Assert.Equal("5000.00", head["remainingAmount"]);
        Assert.Equal(applied, (await data.Service.LookupAsync(data.Checker, P.DecideOperation, "return-migration-applied"))["result"]);
    }

    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Genuine_customer_v1_purpose_and_private_submission_survive_and_missing_pin_refuses_before_DDL()
    {
        await using var data = await PaymentAllocationTests.Data.CreateAsync(fixture);
        var source = data.Source; var db = source.Read.Database;
        var party = await data.Rows(V.Parties).Find(new BsonDocument("_id", source.ReceiptId)).SingleAsync();
        Assert.Equal(1, party["schemaVersion"].AsInt32);
        var submitted = await data.Rows("paymentSubmissions").Find(new BsonDocument("declarationId", source.Read.DeclarationId)).SingleAsync();
        var pin = await data.Rows("evidencePins").Find(new BsonDocument("_id", submitted["pinId"])).SingleAsync();
        await RestoreV75Async(db); var before = await SnapshotAsync(db, definitions: false);
        await data.Rows("evidencePins").DeleteOneAsync(new BsonDocument("_id", pin["_id"]));
        try { await RefusesAsync(db, source.Read.Catalog.Clock); }
        finally { await data.Rows("evidencePins").InsertOneAsync(pin); }
        await new CollectionMigrations(db, source.Read.Catalog.Clock).ApplyAsync();
        Assert.Equal(before, await SnapshotAsync(db, definitions: false));
        Assert.Equal(party, await data.Rows(V.Parties).Find(new BsonDocument("_id", source.ReceiptId)).SingleAsync());
        Assert.NotNull(await source.Service.ReadAsync(source.Reader, source.Read.DeclarationId));
    }

    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task A_real_supplier_return_cannot_be_disguised_as_an_empty_predecessor()
    {
        await using var data = await ProcurementAdvanceReturnTestData.CreateAsync(fixture);
        var proposal = await data.ProposeAsync("return-no-downgrade"); await data.DecideAsync(proposal["proposalId"].AsInt64, "return-no-downgrade-approved");
        var before = await SnapshotAsync(data.Database); var history = await HistoryAsync(data.Database);
        Assert.NotNull(await Record.ExceptionAsync(() => RestoreV75Async(data.Database)));
        Assert.Equal(before, await SnapshotAsync(data.Database)); Assert.Equal(history, await HistoryAsync(data.Database));
    }

    // Test-only empty-suffix removal. It never edits legacy rows, indexes, counters or source owners.
    internal static async Task RestoreV75Async(IMongoDatabase db)
    {
        await ProcurementQualityAcceptanceMigrationTests.RestoreV76Async(db);
        var history = await HistoryAsync(db);
        if (history.Count <= 75) { Assert.All(history, row => Assert.Equal("applied", row["state"])); return; }
        Assert.Equal(76, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"]));
        foreach (var table in B.Collections) {
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
            Assert.False(await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", table)).AnyAsync());
        }
        var targets = ProcurementAdvanceReturnSchemas.ExtendedPayableCollections.Append(V.Parties).ToArray();
        var replacements = new Dictionary<string, BsonDocument>();
        foreach (var table in targets) {
            var legacy = table == V.Parties ? PaymentVerificationSchemas.Validator(table) : ProcurementSupplierPayableSchemas.Validator(table);
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument("$nor", new BsonArray { legacy })).AnyAsync());
            var current = table == V.Parties ? PaymentReceiptPurposeSchemas.Validator() : ProcurementAdvanceReturnSchemas.CurrentPayableValidator(table);
            var installed = (await DefinitionAsync(db, table))["options"]["validator"].AsBsonDocument;
            replacements[table] = PaymentManualReviewMigration.ExtendValidator(installed, current, legacy, table);
        }
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"),
            ("outboxEvents", "sourceKey"), ("idempotencyRecords", "operation"), ("authorizationHistory", "operation"),
            ("movements", "source.namespace"), ("journalEntries", "source.namespace"), ("journalEntries", "sourceEvent") })
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^procurement\.advancereturn(?:[.:]|$)"))).AnyAsync());
        var before = await SnapshotAsync(db); var expected = before.DeepClone().AsBsonDocument;
        foreach (var (table, validator) in replacements) expected[table]["definition"]["options"]["validator"] = validator;
        foreach (var (table, validator) in replacements)
            await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", validator } });
        Assert.Equal(1L, (await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 76 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(75)), await HistoryAsync(db)); Assert.Equal(expected, await SnapshotAsync(db));
    }
    private static async Task RefusesAsync(IMongoDatabase db, IClock clock)
    {
        var before = await SnapshotAsync(db);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync());
        Assert.Equal("Procurement advance return migration preflight failed", error.Message); Assert.Equal(before, await SnapshotAsync(db));
        Assert.False(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument { { "_id", 76 }, { "state", "applied" } }).AnyAsync());
    }
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase db, bool definitions = true)
    {
        var result = new BsonDocument();
        foreach (var definition in (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString; if (name is "schemaMigrations" or "migrationLocks") continue;
            var table = new BsonDocument { { "indexes", new BsonArray((await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) },
                { "rows", new BsonArray(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
            if (definitions) table.Add("definition", definition); result[name] = table;
        }
        return result;
    }
    private static async Task<BsonDocument> DefinitionAsync(IMongoDatabase db, string table) => await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync();
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
}
