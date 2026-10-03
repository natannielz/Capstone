using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Documents;

// The video target has dedicated policies. No accepted image branch or cap changes.
internal static partial class ProcurementInspectionEvidenceSchemas
{
    internal const string StorePolicies = "evidenceVideoStorePolicies";
    internal const string ReadPolicies = "evidenceVideoReadPolicies";
    internal static readonly string[] Collections = [.. ProcurementDisbursementEvidenceSchemas.Collections, "outboxEvents"];
    internal static readonly string[] NewCollections = [StorePolicies, ReadPolicies, .. VideoValidationSchemas.Collections];
    internal static BsonDocument PreviousValidator(string name) => ProcurementDisbursementEvidenceSchemas.Collections.Contains(name)
        ? ProcurementDisbursementEvidenceSchemas.Validator(name) : EvidenceSchemas.Collections.Contains(name) ? EvidenceSchemas.Validator(name)
        : EvidenceAccessSchemas.Collections.Contains(name) ? EvidenceAccessSchemas.Validator(name)
        : EvidenceValidationSchemas.Collections.Contains(name) ? EvidenceValidationSchemas.Validator(name) : PaymentSubmissionSchemas.Validator(name);
    internal static BsonDocument Validator(string name) {
        if (name == StorePolicies) return EvidenceSchemas.Validator("evidenceStorePolicies");
        if (name == ReadPolicies) {
            var value = EvidenceAccessSchemas.Validator("evidenceReadPolicies");
            value["$and"][0]["$jsonSchema"]["properties"]["maximumBytes"]["maximum"] = long.MaxValue / 3;
            return value;
        }
        if (VideoValidationSchemas.Collections.Contains(name)) return VideoValidationSchemas.Validator(name);
        var previous = PreviousValidator(name);
        if (!Collections.Contains(name)) return previous;
        var branch = Translate(name == "outboxEvents" ? previous : previous["$or"][1]).AsBsonDocument;
        var combined = new BsonDocument("$or", new BsonArray { previous, branch });
        return name == "outboxEvents" ? new("$and", new BsonArray { combined, VideoValidationSchemas.OutboxValidator() }) : combined;
    }
    internal static BsonDocument Key(string name) => name switch {
        StorePolicies => new("storeId", 1), ReadPolicies => EvidenceAccessSchemas.Key("evidenceReadPolicies"),
        _ when VideoValidationSchemas.Collections.Contains(name) => VideoValidationSchemas.Key(name),
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
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
        InventorySchemas.Contains(current, required) ||
        ProcurementDisbursementEvidenceSchemas.Collections.Contains(name) && ProcurementDisbursementEvidenceSchemas.Supports(name, current, required) ||
        Collections.Contains(name) && KnownPredecessor(name, required) && InventorySchemas.Contains(current, Validator(name));
    private static bool KnownPredecessor(string name, BsonDocument required) => CatalogSchemas.SemanticEquals(required, PreviousValidator(name)) ||
        ProcurementDisbursementEvidenceSchemas.Collections.Contains(name) && (
            CatalogSchemas.SemanticEquals(required, ProcurementDisbursementEvidenceSchemas.PreviousValidator(name)) ||
            CatalogSchemas.SemanticEquals(required, RefundPayoutEvidenceSchemas.PreviousValidator(name))) ||
        EvidenceSchemas.Collections.Contains(name) && CatalogSchemas.SemanticEquals(required, EvidenceSchemas.Validator(name));
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
                !InventorySchemas.Contains(options.GetValue("validator", new BsonDocument()).AsBsonDocument, Validator(name))) throw Unavailable();
            try { PaymentEvidenceMigration.RequireIndexes(name, await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct)); }
            catch (InvalidOperationException) { throw Unavailable(); }
        }
        foreach (var name in EvidenceAccessSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, Validator(name), EvidenceAccessSchemas.Key(name), ct);
        foreach (var name in new[] { "evidencePins", "evidencePinAudits" })
            await InventoryRepository.RequireStorageAsync(database, name, Validator(name), PaymentSubmissionSchemas.Key(name), ct);
        foreach (var name in NewCollections)
            await InventoryRepository.RequireStorageAsync(database, name, Validator(name), Key(name), ct);
    }
    private static BsonValue Translate(BsonValue value) {
        if (value.IsString) return value.AsString switch {
            "ProcurementDisbursement" => "ProcurementInspection", "ProcurementDisbursementImage" => "ProcurementUnboxingVideo",
            "procurementDisbursementDraftId" => "procurementInspectionDraftId", "procurementDisbursementDraftRevision" => "procurementInspectionDraftRevision",
            "evidence.procurement-disbursement-image.reserve" => "evidence.procurement-unboxing-video.reserve",
            "evidence.procurement-disbursement-image.complete" => "evidence.procurement-unboxing-video.complete",
            "evidence.validation.requested" => VideoValidationRecords.Kind, _ => value.DeepClone() };
        if (value.IsBsonArray) return new BsonArray(value.AsBsonArray.Select(Translate));
        if (value.IsBsonDocument) return new BsonDocument(value.AsBsonDocument.Select(pair => new BsonElement(pair.Name switch {
            "procurementDisbursementDraftId" => "procurementInspectionDraftId", "procurementDisbursementDraftRevision" => "procurementInspectionDraftRevision", _ => pair.Name }, Translate(pair.Value))));
        return value.DeepClone();
    }
    private static PersistenceConflictException Unavailable() => new("evidence_storage_unavailable", "Evidence storage is not ready");
    private static InvalidOperationException Bad(string name) => new("Procurement inspection evidence migration preflight failed: " + name);
}
