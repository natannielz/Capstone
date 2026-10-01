using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using C = UnitToko.Platform.Billing.PaymentAllocationCorrectionBson;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentAllocationCorrectionMigrationTests(MongoFixture fixture)
{
    private static async Task<PaymentAllocationTests.Data> Predecessor(MongoFixture fixture) {
        var data = await PaymentAllocationTests.Data.CreateAsync(fixture);
        try {
            var input = data.Input(0, 1000m); var preview = await data.Service.PreviewAsync(data.Actor, data.VerificationId, input); await data.Service.AllocateAsync(data.Actor, data.VerificationId, preview.Binding, input, PaymentAllocationTests.Data.Meta("correction-upgrade-source"));
            foreach (var table in PaymentAllocationCorrectionSchemas.Collections) { Assert.False(await data.Rows(table).Find(new BsonDocument()).AnyAsync()); await data.Source.Read.Database.DropCollectionAsync(table); }
            await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 52)); return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    private static async Task<List<BsonDocument>> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    [WindowsStorageFact]
    public async Task Populated_actual_allocation_and_canonical_heads_upgrade_with_stronger_rules_and_resume_without_changing_old_money() {
        await using var data = await Predecessor(fixture); var db = data.Source.Read.Database; var clock = data.Source.Read.Catalog.Clock; var original = await data.Rows(B.Events).Find(new BsonDocument()).SingleAsync(); var head = await data.Rows(B.Receipts).Find(new BsonDocument()).SingleAsync();
        var guard = new BsonDocument("actorId", new BsonDocument("$ne", 999L)); await db.CreateCollectionAsync(C.Events, new CreateCollectionOptions<BsonDocument> { Validator = guard }); await new CollectionMigrations(db, clock).ApplyAsync();
        Assert.Equal(original, await data.Rows(B.Events).Find(new BsonDocument()).SingleAsync()); Assert.Equal(head, await data.Rows(B.Receipts).Find(new BsonDocument()).SingleAsync()); var definitions = await Definitions(db);
        var target = Assert.Single(definitions, row => row["name"] == C.Events); Assert.True(InventorySchemas.Contains(target["options"]["validator"].AsBsonDocument, guard)); Assert.Equal("strict", target["options"]["validationLevel"]); Assert.Equal("error", target["options"]["validationAction"]);
        var indexes = await (await data.Rows(C.Events).Indexes.ListAsync()).ToListAsync(); Assert.Equal(4, indexes.Count); Assert.All(indexes.Where(row => row["name"] != "_id_"), row => Assert.True(row["unique"].AsBoolean));
        await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 52)); await new CollectionMigrations(db, clock).ApplyAsync(); Assert.Equal(definitions, await Definitions(db)); Assert.Equal(head, await data.Rows(B.Receipts).Find(new BsonDocument()).SingleAsync());
    }
    [WindowsStorageTheory]
    [InlineData("ttl")] [InlineData("partial")] [InlineData("missing-allocation")] [InlineData("missing-head")] [InlineData("missing-bank-journal")] [InlineData("missing-invoice-journal")] [InlineData("orphan-correction")]
    public async Task Unsafe_target_or_damaged_actual_source_or_surviving_effects_fail_before_target_DDL(string fault) {
        await using var data = await Predecessor(fixture); var db = data.Source.Read.Database;
        if (fault is "ttl" or "partial") { await db.CreateCollectionAsync(C.Events); await data.Rows(C.Events).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), fault == "ttl" ? new CreateIndexOptions { ExpireAfter = TimeSpan.FromMinutes(1) } : new CreateIndexOptions<BsonDocument> { PartialFilterExpression = new BsonDocument("operation", C.Operation) })); }
        if (fault == "missing-allocation") await data.Rows(B.Events).DeleteManyAsync(new BsonDocument()); if (fault == "missing-head") await data.Rows(B.Receipts).DeleteManyAsync(new BsonDocument());
        if (fault == "missing-bank-journal") { var receipt = await data.Rows(UnitToko.Platform.Billing.BankReceiptBson.Receipts).Find(new BsonDocument()).SingleAsync(); await data.Rows("journalEntries").DeleteOneAsync(new BsonDocument("_id", receipt["journalId"])); }
        if (fault == "missing-invoice-journal") { var invoice = await data.Rows(UnitToko.Platform.Billing.InvoiceBson.Invoices).Find(new BsonDocument()).SingleAsync(); await data.Rows("journalEntries").DeleteOneAsync(new BsonDocument("_id", invoice["journalId"])); }
        if (fault == "orphan-correction") { var effect = await data.Rows("outboxEvents").Find(new BsonDocument("kind", B.Operation)).SingleAsync(); effect["_id"] = ObjectId.GenerateNewId(); effect["kind"] = C.Operation + ".orphan"; effect["sourceKey"] = C.Operation + ":orphan"; await data.Rows("outboxEvents").InsertOneAsync(effect); }
        var before = await Definitions(db); Assert.NotNull(await Record.ExceptionAsync(() => new CollectionMigrations(db, data.Source.Read.Catalog.Clock).ApplyAsync())); Assert.Equal(before, await Definitions(db)); Assert.False(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 52 }, { "state", "applied" } }).AnyAsync());
    }
}
