using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Documents;

// Forward-only M70 image target; all prior image/video validators remain exact branches.
internal static partial class SupplierDocumentEvidenceSchemas
{
    internal static readonly string[] Collections = [.. ProcurementInspectionEvidenceSchemas.Collections];
    internal static BsonDocument PreviousValidator(string name) => ProcurementInspectionEvidenceSchemas.Validator(name);
    internal static BsonDocument Validator(string name) {
        var previous = PreviousValidator(name);
        if (!Collections.Contains(name)) return previous;
        if (name == "outboxEvents") return new("$and", new BsonArray { previous, SupplierDocumentProcessingSchemas.OutboxValidator() });
        var branch = Translate(ProcurementDisbursementEvidenceSchemas.Validator(name)["$or"][1]).AsBsonDocument;
        return new("$or", new BsonArray { previous, branch });
    }
    internal static BsonDocument Extend(string name, BsonDocument current) {
        var before = PreviousValidator(name); var after = Validator(name); var found = false;
        BsonDocument Replace(BsonDocument value) {
            if (CatalogSchemas.SemanticEquals(value, after)) { found = true; return value.DeepClone().AsBsonDocument; }
            if (CatalogSchemas.SemanticEquals(value, before)) { found = true; return after.DeepClone().AsBsonDocument; }
            var copy = value.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                copy["$and"] = new BsonArray(terms.AsBsonArray.Select(term => term.IsBsonDocument ? Replace(term.AsBsonDocument) : term.DeepClone()));
            return copy;
        }
        var result = Replace(current); return found ? result : throw Bad(name);
    }
    internal static bool Supports(string name, BsonDocument current, BsonDocument required) =>
        ProcurementInspectionEvidenceSchemas.Supports(name, current, required) ||
        Collections.Contains(name) && (CatalogSchemas.SemanticEquals(required, PreviousValidator(name)) ||
            ProcurementInspectionEvidenceSchemas.Supports(name, PreviousValidator(name), required)) && InventorySchemas.Contains(current, Validator(name));
    internal static async Task RequireCompatibleStorageAsync(IMongoDatabase database, string name, BsonDocument required, BsonDocument key, CancellationToken ct) {
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
        var current = definition?.GetValue("options", new BsonDocument()).AsBsonDocument.GetValue("validator", new BsonDocument()).AsBsonDocument;
        if (current is null || !Supports(name, current, required)) throw Unavailable();
        await InventoryRepository.RequireStorageAsync(database, name, current, key, ct);
    }
    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct) {
        foreach (var name in EvidenceSchemas.Collections) {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition is null || definition.GetValue("type", "") != "collection" || options is null ||
                options.GetValue("validationLevel", "") != "strict" || options.GetValue("validationAction", "") != "error" ||
                options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation) ||
                !Supports(name, options.GetValue("validator", new BsonDocument()).AsBsonDocument, PreviousValidator(name))) throw Unavailable();
            try { PaymentEvidenceMigration.RequireIndexes(name, await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct)); }
            catch (InvalidOperationException) { throw Unavailable(); }
        }
        foreach (var name in EvidenceAccessSchemas.Collections)
            await RequireCompatibleStorageAsync(database, name, PreviousValidator(name), EvidenceAccessSchemas.Key(name), ct);
        foreach (var name in new[] { "evidencePins", "evidencePinAudits" })
            await RequireCompatibleStorageAsync(database, name, PreviousValidator(name), PaymentSubmissionSchemas.Key(name), ct);
        await SupplierDocumentExtractionRecords.RequireStorageAsync(database, ct);
    }
    private static BsonValue Translate(BsonValue value) {
        if (value.IsString) return value.AsString switch {
            "ProcurementDisbursement" => "SupplierDocument", "ProcurementDisbursementImage" => "SupplierDocumentImage",
            "procurementDisbursementDraftId" => "supplierDocumentDraftId", "procurementDisbursementDraftRevision" => "supplierDocumentDraftRevision",
            "evidence.procurement-disbursement-image.reserve" => "evidence.supplier-document-image.reserve",
            "evidence.procurement-disbursement-image.complete" => "evidence.supplier-document-image.complete",
            _ => value.DeepClone() };
        if (value.IsBsonArray) return new BsonArray(value.AsBsonArray.Select(Translate));
        if (value.IsBsonDocument) return new BsonDocument(value.AsBsonDocument.Select(pair => new BsonElement(pair.Name switch {
            "procurementDisbursementDraftId" => "supplierDocumentDraftId", "procurementDisbursementDraftRevision" => "supplierDocumentDraftRevision", _ => pair.Name }, Translate(pair.Value))));
        return value.DeepClone();
    }
    private static PersistenceConflictException Unavailable() => new("evidence_storage_unavailable", "Evidence storage is not ready");
    private static InvalidOperationException Bad(string name) => new("Supplier document evidence migration preflight failed: " + name);
}
