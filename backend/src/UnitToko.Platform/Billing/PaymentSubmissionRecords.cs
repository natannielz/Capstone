using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.PaymentSubmissionBson;

namespace UnitToko.Platform.Billing;

internal sealed class PaymentSubmissionRecords(IMongoDatabase database)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal async Task<BsonDocument> ReadAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await Rows(B.Collection).Find(tx, new BsonDocument("$and", new BsonArray { new BsonDocument("_id", id), PaymentSubmissionSchemas.Validator(B.Collection) })).SingleOrDefaultAsync(ct)
            ?? throw PaymentDraftBson.Invalid();
        if (row["snapshotHash"] != B.Hash(row) || row["bodyHash"] != B.BodyHash(row) || row["recordedBy"] != row["submittedBy"] || row["uploaderId"] != row["submittedBy"] ||
            row["personAtSubmission"]["userId"] != row["submittedBy"]) throw PaymentDraftBson.Invalid();
        var version = await new PaymentDraftRecords(database).VersionAsync(tx, row["declarationVersionId"].AsInt64, ct);
        if (version["declarationId"] != row["declarationId"] || version["revision"] != row["declarationRevision"] || version["snapshotHash"] != row["declarationHash"] ||
            version["recordedBy"] != row["recordedBy"] || version["recordedAt"].CompareTo(row["submittedAt"]) > 0) throw PaymentDraftBson.Invalid();
        var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["submittedBy"] }, { "operation", B.Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
        if (audit is null || !InvoiceRecords.Equal(audit, B.Audit(row)) || receipt is null || receipt.GetValue("bodyHash", BsonNull.Value) != row["bodyHash"] ||
            !InvoiceRecords.Equal(receipt.GetValue("result", BsonNull.Value), B.Result(row))) throw PaymentDraftBson.Invalid();
        var pin = await EvidencePinRecords.ReadAsync(database, tx, row["pinId"].AsInt64, ct);
        if (pin["associationKind"] != "PaymentSubmission" || pin["associationId"] != row["_id"] || pin["associationHash"] != row["snapshotHash"] || pin["declarationId"] != row["declarationId"] ||
            pin["declarationVersionId"] != row["declarationVersionId"] || pin["declarationRevision"] != row["declarationRevision"] || pin["reviewRevision"] != row["reviewRevision"] ||
            !InvoiceRecords.Equal(pin["reference"], row["evidence"]) || pin["reservationId"] != row["reservationId"] || pin["actorId"] != row["submittedBy"] ||
            pin["actorGrantVersion"] != row["actorGrantVersion"] || pin["createdAt"] != row["submittedAt"] || pin["reason"] != row["reason"] || pin["correlationId"] != row["correlationId"]) throw PaymentDraftBson.Invalid();
        var job = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.OcrKey(row))).SingleOrDefaultAsync(ct);
        if (job is null || job["kind"] != "payment.ocr.requested" || !InvoiceRecords.Equal(job["payload"], B.OcrPayload(row)) || job["createdAt"] != row["submittedAt"]) throw PaymentDraftBson.Invalid();
        return row;
    }
    internal async Task RequireHeadAsync(IClientSessionHandle tx, BsonDocument head, CancellationToken ct) {
        var rows = await Rows(B.Collection).Find(tx, new BsonDocument("declarationId", head["_id"])).Limit(2).ToListAsync(ct);
        if (head["state"] == "draft") { if (rows.Count != 0) throw PaymentDraftBson.Invalid(); return; }
        if (rows.Count != 1 || rows[0]["_id"] != head["submissionId"]) throw PaymentDraftBson.Invalid();
        var row = await ReadAsync(tx, head["submissionId"].AsInt64, ct);
        if (row["declarationRevision"] != head["revision"] || row["declarationVersionId"] != head["versionId"] || row["reviewRevision"] != head["reviewRevision"] ||
            row["recordedBy"] != head["recordedBy"] || row["submittedAt"] != head["updatedAt"] || row["state"] != head["state"]) throw PaymentDraftBson.Invalid();
    }
}
