using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using D = UnitToko.Platform.Fulfillment.AfterSalesDecisionRecords;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class AfterSalesDecisionMigrationTests(MongoFixture fixture)
{
    private static async Task<BsonDocument[]> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToArray();
    [Fact]
    public async Task Populated_request_upgrade_preserves_original_documents_previous_receipts_and_stronger_empty_target_rules_and_resumes() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var order = data.Invoice.Order; var db = order.Database; var clock = order.Prices.Catalog.Clock;
        await order.Prices.Catalog.Base.AddGrantAsync(order.Buyer, 99810, CatalogAuthoringTestData.Audience, [UnitToko.Modules.Identity.Permissions.CaseRead, UnitToko.Modules.Identity.Permissions.CaseRequest]);
        var service = new AfterSalesCaseService(db, new(order.Prices.Catalog.Client), new(db), clock, data.Invoice.Service); var source = await service.SourceAsync(order.Buyer, data.Issued.Id.Value); var line = source.Lines[0];
        await service.RequestAsync(order.Buyer, new(source.InvoiceId.Value, source.Binding, "damaged", [new(line.ReceiptId.Value, line.SkuId.Value, 1)]), CatalogPriceTestData.Meta("populated-47-case"));
        // Isolated version-47 image: format-1 requests are unchanged; no decision writer has run.
        Assert.Equal(0, await data.Invoice.Count(UnitToko.Platform.Inventory.ReturnQuarantineLedger.Collection));
        await data.Invoice.Rows("schemaMigrations").DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$gt", 47)));
        await db.DropCollectionAsync(UnitToko.Platform.Inventory.ReturnQuarantineLedger.Collection); await db.DropCollectionAsync(D.Collection); await db.DropCollectionAsync(InvoiceService.CaseReturnFences);
        var extra = new BsonDocument("actorId", new BsonDocument("$ne", 999L)); var fenceExtra = new BsonDocument("invoiceHash", new BsonDocument("$ne", new string('a', 64)));
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", D.Collection }, { "validator", extra } });
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", InvoiceService.CaseReturnFences }, { "validator", fenceExtra } });
        var before = await Definitions(db); var receipts = await data.Invoice.Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        var original = await data.Invoice.Rows("afterSalesCases").Find(new BsonDocument()).SingleAsync(); var invoice = await data.Invoice.Rows(InvoiceBson.Invoices).Find(new BsonDocument()).SingleAsync();
        await LegacyOrderSubmissionTestSchema.UpgradeV48Async(db, clock); var after = await Definitions(db);
        Assert.Equal(before.Where(row => row["name"] != D.Collection && row["name"] != InvoiceService.CaseReturnFences), after.Where(row => row["name"] != D.Collection && row["name"] != InvoiceService.CaseReturnFences));
        Assert.Equal(original, await data.Invoice.Rows("afterSalesCases").Find(new BsonDocument()).SingleAsync()); Assert.Equal(invoice, await data.Invoice.Rows(InvoiceBson.Invoices).Find(new BsonDocument()).SingleAsync());
        Assert.Equal(receipts, (await data.Invoice.Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).Take(47));
        Assert.True(InventorySchemas.Contains(after.Single(row => row["name"] == D.Collection)["options"]["validator"].AsBsonDocument, extra));
        Assert.True(InventorySchemas.Contains(after.Single(row => row["name"] == InvoiceService.CaseReturnFences)["options"]["validator"].AsBsonDocument, fenceExtra));
        await data.Invoice.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 48)); await LegacyOrderSubmissionTestSchema.UpgradeV48Async(db, clock); Assert.Equal(after, await Definitions(db));
    }
    [Theory]
    [InlineData("ttl")] [InlineData("partial-fence")] [InlineData("orphan")]
    public async Task Unsafe_decision_or_invoice_fence_and_orphan_effect_block_before_any_target_DDL(string fault) {
        var name = "ut_return_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name); var clock = new SystemClock();
        try {
            await LegacyOrderSubmissionTestSchema.ApplyV45Async(db, clock); await LegacyOrderSubmissionTestSchema.UpgradeV46Async(db, clock); await LegacyOrderSubmissionTestSchema.UpgradeV47Async(db, clock);
            if (fault == "ttl") { await db.CreateCollectionAsync(D.Collection); await db.GetCollection<BsonDocument>(D.Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("decidedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) })); }
            else if (fault == "partial-fence") { await db.CreateCollectionAsync(InvoiceService.CaseReturnFences); await db.GetCollection<BsonDocument>(InvoiceService.CaseReturnFences).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("invoiceHash", 1), new CreateIndexOptions<BsonDocument> { PartialFilterExpression = new BsonDocument("fence", new BsonDocument("$gt", 0L)) })); }
            else await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", D.Operation }, { "payload", new BsonDocument("caseId", 99L) } }, new InsertOneOptions { BypassDocumentValidation = true });
            var before = await Definitions(db); await Assert.ThrowsAsync<InvalidOperationException>(() => LegacyOrderSubmissionTestSchema.UpgradeV48Async(db, clock)); Assert.Equal(before, await Definitions(db));
            Assert.False(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument { { "_id", 48 }, { "state", "applied" } }).AnyAsync());
        } finally { await fixture.Client.DropDatabaseAsync(name); }
    }
}
