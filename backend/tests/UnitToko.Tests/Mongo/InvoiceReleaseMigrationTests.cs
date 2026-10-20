using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Ordering;
using B = UnitToko.Platform.Billing.InvoiceReleaseBson;

namespace UnitToko.Tests.Mongo;

[Collection("Mongo")]
public sealed class InvoiceReleaseMigrationTests(MongoFixture fixture)
{
    private async Task<StoreTestData> BaselineAsync() {
        var data = await StoreTestData.CreateWithMigrationTargetAsync(fixture, 118);
        var steps = new CollectionMigrations(data.Database, data.Clock).RegisteredSteps();
        Assert.IsType<OrderDraftDiscardMigration>(steps[117]); // the actual predecessor of 119
        Assert.IsType<InvoiceReleaseMigration>(steps[118]);
        Assert.True(await new MigrationGate(data.Database, data.Clock, 118).IsCurrentAsync(default)); return data;
    }
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase database) {
        var result = new BsonDocument();
        foreach (var definition in (await (await database.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString)) {
            var name = definition["name"].AsString; if (name == "schemaMigrations") continue;
            var rows = await database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync();
            result.Add(name, new BsonDocument { { "definition", definition }, { "indexes", new BsonArray(indexes.OrderBy(row => row["name"].AsString)) }, { "rows", new BsonArray(rows) } });
        }
        return result;
    }
    [Fact(Skip = Retired.HistoricalUpgrade)]
    public async Task Real_118_invoice_classification_preserves_original_receipt_journal_document_and_financial_bytes() {
        var data = await BaselineAsync();
        await using var legacy = await InvoiceData.CreateAsync(fixture, await ShipmentDispatchTestData.CreateAsync(fixture,
            attachedOrder: await OrderSubmissionTestData.CreateAsync(fixture, attachedPrices: await CatalogPriceTestData.CreateAsync(fixture,
                attachedCatalog: await CatalogAuthoringTestData.AttachAsync(data, fixture.Client)))));
        var preview = await legacy.Service.PreviewAsync(legacy.Finance, legacy.Selection);
        var invoice = await legacy.Service.IssueAsync(legacy.Finance, legacy.Selection, preview.Review, CatalogPriceTestData.Meta("actual-pre119-invoice"));
        var before = await SnapshotAsync(data.Database);
        var priorHistory = await data.Database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        var after = await SnapshotAsync(data.Database);
        foreach (var retained in before) Assert.Equal(retained.Value, after[retained.Name]);
        var binding = await data.Database.GetCollection<BsonDocument>(B.Bindings).Find(new BsonDocument("_id", invoice.Id.Value)).SingleAsync();
        Assert.Equal("pre_release", binding["kind"].AsString); Assert.True(binding["releaseId"].IsBsonNull);
        Assert.Equal(1, await data.Database.GetCollection<BsonDocument>(B.Anchors).CountDocumentsAsync(new BsonDocument()));
        Assert.False(await data.Database.GetCollection<BsonDocument>(B.Events).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Database.GetCollection<BsonDocument>(B.Heads).Find(new BsonDocument()).AnyAsync());
        Assert.Equal(invoice.Id, (await legacy.Service.RecoverAsync(legacy.Finance, "actual-pre119-invoice"))!.Id);
        Assert.Equal(priorHistory, (await data.Database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).Take(115));
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync(); Assert.Equal(after, await SnapshotAsync(data.Database));
        await data.Database.GetCollection<BsonDocument>(B.Anchors).DeleteOneAsync(new BsonDocument("_id", invoice.Id.Value));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => legacy.Service.RecoverAsync(legacy.Finance, "actual-pre119-invoice"));
    }
    [Fact]
    public async Task Unknown_prospective_validator_index_counter_and_orphan_effect_each_fail_before_any_target_mutation() {
        foreach (var fault in new[] { "validator", "index", "counter", "release-effect", "invoice-effect", "invoice-outbox-key", "binding" }) {
            await using var data = await BaselineAsync(); var database = data.Database;
            if (fault is "validator" or "index") {
                await database.CreateCollectionAsync(B.Heads, new CreateCollectionOptions<BsonDocument> {
                    Validator = fault == "validator" ? new BsonDocument("$or", new BsonArray { InvoiceReleaseSchemas.Validator(B.Heads), new BsonDocument() }) : InvoiceReleaseSchemas.Validator(B.Heads),
                    ValidationLevel = DocumentValidationLevel.Strict, ValidationAction = DocumentValidationAction.Error });
                if (fault == "index") await database.GetCollection<BsonDocument>(B.Heads).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("selectionKey", -1),
                    new CreateIndexOptions { Name = "ux_invoice_release_selection", Unique = true }));
            }
            if (fault == "counter") await database.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", B.Events }, { "value", 1L } });
            if (fault is "release-effect" or "invoice-effect") await database.GetCollection<BsonDocument>("idempotencyRecords").InsertOneAsync(new BsonDocument {
                { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "operation", fault == "release-effect" ? B.Prefix + "approve" : InvoiceBson.Operation },
                { "actorId", 999L }, { "key", "orphan" }, { "bodyHash", new string('a', 64) }, { "createdAt", data.Clock.UtcNow.UtcDateTime }, { "result", new BsonDocument("invoiceId", 9191L) }
            }, new InsertOneOptions { BypassDocumentValidation = true });
            if (fault == "invoice-outbox-key") await database.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument {
                { "_id", 9191L }, { "schemaVersion", 1 }, { "sourceKey", InvoiceBson.Operation + ":9191" },
                { "kind", "unrelated" }, { "createdAt", data.Clock.UtcNow.UtcDateTime }, { "payload", new BsonDocument("invoiceId", 9191L) }
            }, new InsertOneOptions { BypassDocumentValidation = true });
            if (fault == "binding") await database.GetCollection<BsonDocument>(B.Bindings).InsertOneAsync(new BsonDocument { { "_id", 9191L }, { "schemaVersion", 1 } });
            var before = await SnapshotAsync(database);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(database, data.Clock).ApplyAsync());
            Assert.Equal(before, await SnapshotAsync(database));
            Assert.Equal(118, await database.GetCollection<BsonDocument>("schemaMigrations").CountDocumentsAsync(new BsonDocument("state", "applied")));
        }
    }
    [Fact]
    public async Task Interrupted_empty_install_preserves_created_UUID_and_resumes_the_actual_chain_without_a_schema_marker_rewrite() {
        await using var data = await BaselineAsync(); var db = data.Database; using var cancelled = new CancellationTokenSource();
        var stopped = false; var settings = fixture.Client.Settings.Clone();
        settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandSucceededEvent>(command => {
            if (!stopped && command.CommandName == "create") { stopped = true; cancelled.Cancel(); }
        });
        using var client = new MongoClient(settings);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionMigrations(client.GetDatabase(db.DatabaseNamespace.DatabaseName), data.Clock).ApplyAsync(cancelled.Token));
        Assert.True(stopped); var partial = await SnapshotAsync(db);
        await new CollectionMigrations(db, data.Clock).ApplyAsync();
        var after = await SnapshotAsync(db);
        foreach (var before in partial) {
            if (!InvoiceReleaseSchemas.Collections.Contains(before.Name)) Assert.Equal(before.Value, after[before.Name]);
            else Assert.Equal(before.Value["definition"]["info"]["uuid"], after[before.Name]["definition"]["info"]["uuid"]);
        }
        await InvoiceReleaseRecords.RequireStorageAsync(db, default);
        Assert.True(await new MigrationGate(db, data.Clock, CollectionMigrations.CurrentVersion).IsCurrentAsync(default));
        foreach (var name in InvoiceReleaseSchemas.Collections) Assert.False(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
    }
}
