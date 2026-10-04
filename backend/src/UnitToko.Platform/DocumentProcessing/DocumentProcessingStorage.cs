using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Documents;

namespace UnitToko.Platform.DocumentProcessing;

internal static class DocumentProcessingStorage
{
    internal static async Task RequireAsync(IMongoDatabase database, CancellationToken ct) {
        foreach (var name in DocumentProcessingSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, DocumentProcessingSchemas.Validator(name), DocumentProcessingSchemas.Key(name), ct);
        foreach (var name in EvidenceAccessSchemas.Collections)
            await SupplierDocumentEvidenceSchemas.RequireCompatibleStorageAsync(database, name, EvidenceAccessSchemas.Validator(name), EvidenceAccessSchemas.Key(name), ct);
        await RequireOutboxAsync(database, DocumentProcessingSchemas.OutboxValidator(), ct);
    }
    internal static async Task RequireOutboxAsync(IMongoDatabase database, BsonDocument required, CancellationToken ct) {
        // The shared outbox retains its foundation's approved exists-only partial unique index.
        // Use the owning index contract instead of a single-owner collection's stricter index shape.
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "outboxEvents") }, ct)).SingleOrDefaultAsync(ct);
        var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (definition is null || definition.GetValue("type", "") != "collection" || options is null || options.GetValue("validationLevel", "") != "strict" || options.GetValue("validationAction", "") != "error" ||
            options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
            options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation) ||
            !SupplierDocumentEvidenceSchemas.Supports("outboxEvents", options.GetValue("validator", new BsonDocument()).AsBsonDocument, required)) throw Unavailable();
        try { PaymentEvidenceMigration.RequireIndexes("outboxEvents", await (await database.GetCollection<BsonDocument>("outboxEvents").Indexes.ListAsync(ct)).ToListAsync(ct)); }
        catch (InvalidOperationException) { throw Unavailable(); }
    }
    private static PersistenceConflictException Unavailable() => new("document_processing_storage_unavailable", "Document processing storage is not ready");
}
