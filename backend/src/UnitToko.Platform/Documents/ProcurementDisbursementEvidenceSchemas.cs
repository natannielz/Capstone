using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Documents;

// Forward-only v66 extension. All preceding accepted validators remain exact branches.
internal static partial class ProcurementDisbursementEvidenceSchemas
{
    internal static readonly string[] Collections = ["evidenceLineages", "evidenceUploadReservations", "auditEvents", "idempotencyRecords", "evidenceReadLeases", "evidencePins"];
    internal static BsonDocument PreviousValidator(string name) => RefundPayoutEvidenceSchemas.Validator(name);
    internal static BsonDocument Validator(string name) {
        var previous = PreviousValidator(name);
        // Keep the complete, exact v55 validator; translate only its distinct outgoing branch.
        var outgoing = Translate(previous["$or"][1]).AsBsonDocument;
        return new("$or", new BsonArray { previous, outgoing });
    }
    internal static BsonDocument Extend(string name, BsonDocument current) {
        var before = PreviousValidator(name); var after = Validator(name); var found = false;
        BsonDocument Replace(BsonDocument row) {
            if (CatalogSchemas.SemanticEquals(row, after)) { found = true; return row.DeepClone().AsBsonDocument; }
            if (CatalogSchemas.SemanticEquals(row, before)) { found = true; return after.DeepClone().AsBsonDocument; }
            var copy = row.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                copy["$and"] = new BsonArray(terms.AsBsonArray.Select(value => value.IsBsonDocument ? Replace(value.AsBsonDocument) : value.DeepClone()));
            return copy;
        }
        var result = Replace(current); return found ? result : throw new InvalidOperationException("Procurement disbursement evidence migration requires the accepted predecessor validator: " + name);
    }
    // A caller requesting the original exact branch may also use this one known successor.
    // Arbitrary $or trees, unknown weaker branches and modified successors never qualify.
    internal static bool Supports(string name, BsonDocument current, BsonDocument required) =>
        InventorySchemas.Contains(current, required) || Collections.Contains(name) &&
        KnownPredecessor(name, required) && InventorySchemas.Contains(current, Validator(name));
    private static bool KnownPredecessor(string name, BsonDocument required) =>
        CatalogSchemas.SemanticEquals(required, PreviousValidator(name)) ||
        CatalogSchemas.SemanticEquals(required, RefundPayoutEvidenceSchemas.PreviousValidator(name)) ||
        PaymentEvidenceSchemas.Collections.Contains(name) && CatalogSchemas.SemanticEquals(required, EvidenceSchemas.Validator(name));
    internal static async Task RequireCompatibleStorageAsync(IMongoDatabase database, string name, BsonDocument required, BsonDocument key, CancellationToken ct) {
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
        var current = definition?.GetValue("options", new BsonDocument()).AsBsonDocument.GetValue("validator", new BsonDocument()).AsBsonDocument;
        if (current is null || !Supports(name, current, required)) throw new UnitToko.Platform.Mongo.PersistenceConflictException("evidence_storage_unavailable", "Evidence storage is not ready");
        await InventoryRepository.RequireStorageAsync(database, name, current, key, ct);
    }
    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct) {
        foreach (var name in EvidenceSchemas.Collections) {
            var required = Collections.Contains(name) ? Validator(name) : EvidenceSchemas.Validator(name);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition is null || definition.GetValue("type", "") != "collection" || options is null ||
                options.GetValue("validationLevel", "") != "strict" || options.GetValue("validationAction", "") != "error" ||
                options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !UnitToko.Platform.Billing.InvoiceStorage.Simple(collation) ||
                !SupplierDocumentEvidenceSchemas.Supports(name, options.GetValue("validator", new BsonDocument()).AsBsonDocument, required))
                throw new UnitToko.Platform.Mongo.PersistenceConflictException("evidence_storage_unavailable", "Evidence storage is not ready");
            try { PaymentEvidenceMigration.RequireIndexes(name, await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct)); }
            catch (InvalidOperationException) { throw new UnitToko.Platform.Mongo.PersistenceConflictException("evidence_storage_unavailable", "Evidence storage is not ready"); }
        }
        foreach (var name in EvidenceAccessSchemas.Collections)
            await SupplierDocumentEvidenceSchemas.RequireCompatibleStorageAsync(database, name, name == "evidenceReadLeases" ? Validator(name) : EvidenceAccessSchemas.Validator(name), EvidenceAccessSchemas.Key(name), ct);
        foreach (var name in new[] { "evidencePins", "evidencePinAudits" })
            await SupplierDocumentEvidenceSchemas.RequireCompatibleStorageAsync(database, name, name == "evidencePins" ? Validator(name) : PaymentSubmissionSchemas.Validator(name), PaymentSubmissionSchemas.Key(name), ct);
    }
    private static BsonValue Translate(BsonValue value) {
        if (value.IsString) return value.AsString switch {
            "RefundPayout" => "ProcurementDisbursement", "RefundPayoutImage" => "ProcurementDisbursementImage",
            "refundPayoutDraftId" => "procurementDisbursementDraftId", "refundPayoutDraftRevision" => "procurementDisbursementDraftRevision",
            "evidence.refund-payout-image.reserve" => "evidence.procurement-disbursement-image.reserve",
            "evidence.refund-payout-image.complete" => "evidence.procurement-disbursement-image.complete", _ => value.DeepClone() };
        if (value.IsBsonArray) return new BsonArray(value.AsBsonArray.Select(Translate));
        if (value.IsBsonDocument) return new BsonDocument(value.AsBsonDocument.Select(pair => new BsonElement(pair.Name switch {
            "refundPayoutDraftId" => "procurementDisbursementDraftId", "refundPayoutDraftRevision" => "procurementDisbursementDraftRevision", _ => pair.Name }, Translate(pair.Value))));
        return value.DeepClone();
    }
}
