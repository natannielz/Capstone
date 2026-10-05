using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Storage;
using B = UnitToko.Platform.Procurement.SupplierDocumentBson;

namespace UnitToko.Platform.Procurement;

// Historical reconstruction never renews approval or reads original bytes.
internal sealed class SupplierDocumentPdfMatchRecords(IMongoDatabase database)
{
    internal async Task RequireAsync(IClientSessionHandle tx, BsonDocument match, BsonDocument document,
        ExpectedPublication original, CancellationToken ct) {
        var observations = B.PdfObservations(match["input"].AsBsonDocument) ?? throw B.Broken();
        var reference = PaymentSubmissionBson.Reference(document["evidence"].AsBsonDocument);
        if (observations.Any(value => value.Candidate.Evidence != reference)) throw B.Broken();
        var queue = database.GetCollection<BsonDocument>("outboxEvents");
        var job = await queue.Find(tx, new BsonDocument("sourceKey", B.DocumentOcrKey(document))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        SupplierDocumentRecords.Equal(job["kind"], UnitToko.Modules.Documents.PdfDocumentExtractionContract.SupplierJobKind);
        SupplierDocumentRecords.Equal(job["payload"], B.DocumentOcrPayload(document, original));
        SupplierDocumentRecords.Equal(job["createdAt"], document["recordedAt"]);
        var records = new PdfDocumentExtractionRecords(database);
        var result = await records.ResultAsync(tx, job, ct) ?? throw PdfDocumentExtractionRecords.Broken();
        if (result["sourceKind"] != "SupplierInvoice" || result["createdAt"].CompareTo(match["recordedAt"]) > 0) throw B.Broken();
        var binding = result["binding"].AsBsonDocument;
        foreach (var (field, expected) in new (string, BsonValue)[] {
            ("submissionId", document["_id"]), ("draftId", document["draftId"]), ("draftRevision", 1L),
            ("submissionHash", document["snapshotHash"]), ("draftHash", document["draftHash"]),
            ("reservationId", document["reservationId"]), ("pinId", document["pinId"]), ("uploaderId", document["actorId"]),
            ("reference", document["evidence"]) }) SupplierDocumentRecords.Equal(binding[field], expected);
        foreach (var observation in observations)
            _ = await records.RequireCandidateAsync(tx, job, "SupplierInvoice", binding, original, observation.Candidate, ct);
        // The existing atomic read audit identifies actor/current grant version,
        // source, exact result/hash and command correlation. No client audit ID.
        var filter = new BsonDocument {
            { "actorId", match["actorId"] }, { "actorGrantVersion", match["actorGrantVersion"] }, { "outboxId", job["_id"] },
            { "sourceKind", "SupplierInvoice" }, { "sourceId", document["draftId"] }, { "sourceRevision", 1L }, { "submissionId", document["_id"] },
            { "reference", document["evidence"] }, { "resultId", result["_id"] }, { "resultHash", result["resultHash"] },
            { "state", "succeeded" }, { "purpose", "supplier-document-pdf-ocr-view" }, { "correlationId", match["correlationId"] },
            { "createdAt", new BsonDocument { { "$gte", result["createdAt"] }, { "$lte", match["recordedAt"] } } },
            { "$and", new BsonArray { PdfDocumentExtractionSchemas.Validator(PdfDocumentExtractionRecords.Reads) } }
        };
        if (!await database.GetCollection<BsonDocument>(PdfDocumentExtractionRecords.Reads).Find(tx, filter).Limit(1).AnyAsync(ct)) throw B.Broken();
    }
}
