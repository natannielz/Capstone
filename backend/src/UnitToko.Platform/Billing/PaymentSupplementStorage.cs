using UnitToko.Platform.Documents;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Billing;

internal static class PaymentSupplementStorage
{
    internal static bool SafeIndexes(IReadOnlyList<BsonDocument> indexes, (string Name, BsonDocument Key, bool Unique)[] keys) =>
        PdfDocumentExtractionRecords.SafeIndexes(indexes, keys);
    internal static async Task RequireAsync(IMongoDatabase database, CancellationToken ct) {
        await PaymentSupplementEvidenceSchemas.RequireAsync(database, ct);
        foreach (var name in PaymentSupplementSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw PaymentSupplementRecords.Broken();
            if (!AccountContactStorage.SafeDefinition(definition, PaymentSupplementSchemas.Validator(name))) throw PaymentSupplementRecords.Broken();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct); var keys = PaymentSupplementSchemas.Keys(name);
            if (!PdfDocumentExtractionRecords.SafeIndexes(indexes, keys) || keys.Any(key => !indexes.Any(row => AccountContactStorage.Matches(row, key)))) throw PaymentSupplementRecords.Broken();
        }
        var outbox = await AccountContactStorage.DefinitionAsync(database, "outboxEvents", ct) ?? throw PaymentSupplementRecords.Broken();
        if (!PdfEvidenceAdmissionSchemas.SafeDefinition(outbox) || !InventorySchemas.Contains(outbox["options"]["validator"].AsBsonDocument, PaymentSupplementSchemas.ExtendedOutboxValidator())) throw PaymentSupplementRecords.Broken();
        PaymentEvidenceMigration.RequireIndexes("outboxEvents", await (await database.GetCollection<BsonDocument>("outboxEvents").Indexes.ListAsync(ct)).ToListAsync(ct));
        foreach (var name in PdfDocumentExtractionSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw PaymentSupplementRecords.Broken();
            if (!PdfEvidenceAdmissionSchemas.SafeDefinition(definition) ||
                !InventorySchemas.Contains(definition["options"]["validator"].AsBsonDocument, PaymentSupplementSchemas.DocumentOwnerValidator(name))) throw PaymentSupplementRecords.Broken();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct); var keys = PdfDocumentExtractionSchemas.Keys(name);
            if (!SafeIndexes(indexes, keys) || keys.Any(key => !indexes.Any(row => AccountContactStorage.Matches(row, key)))) throw PaymentSupplementRecords.Broken();
        }
    }
}
