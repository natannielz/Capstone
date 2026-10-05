using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Billing;

internal sealed record PaymentSupplementHistory(BsonDocument Head, IReadOnlyList<BsonDocument> Versions, BsonDocument? Attachment);
internal sealed class PaymentSupplementRecords(IMongoDatabase database, IPaymentSupplementEvidenceHistory evidence)
{
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal async Task<PaymentSupplementHistory> HistoryAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        Transaction(tx); PaymentSupplementRules.Id(id);
        var head = await RowAsync(tx, PaymentSupplementBson.Heads, new("_id", id), ct);
        var versions = await Rows(PaymentSupplementBson.Versions).Find(tx, new BsonDocument("supplementId", id)).Sort(new BsonDocument("revision", 1)).Limit(3).ToListAsync(ct);
        if (versions.Count != head["revision"].AsInt64 || versions.Count is < 1 or > 2) throw Broken();
        BsonValue previous = BsonNull.Value, previousHash = BsonNull.Value;
        var primary = PaymentSupplementBson.Primary(head["primary"].AsBsonDocument);
        var primarySubmission = await new PaymentSubmissionRecords(database).ReadAsync(tx, primary.SubmissionId, ct);
        SamePrimary(primary, primarySubmission, head["primarySubmissionHash"]);
        var scope = await PrimaryScopeAsync(tx, primarySubmission, ct);
        if (!head["scope"].Equals(scope) || head["uploaderId"] != primarySubmission["uploaderId"] ||
            primarySubmission["submittedAt"].CompareTo(head["createdAt"]) > 0 || head["createdAt"].CompareTo(head["updatedAt"]) > 0) throw Broken();
        BsonDocument? attachment = null;
        foreach (var raw in versions) {
            var row = await RowAsync(tx, PaymentSupplementBson.Versions, new("_id", raw["_id"]), ct);
            if (row["supplementId"] != id || row["revision"] != (previous.IsBsonNull ? 1L : 2L) || row["previousVersionId"] != previous || row["previousHash"] != previousHash ||
                row["primarySubmissionHash"] != head["primarySubmissionHash"] || row["uploaderId"] != head["uploaderId"] ||
                !row["primary"].Equals(head["primary"]) || !row["scope"].Equals(head["scope"]) || row["snapshotHash"] != PaymentSupplementBson.Hash(row)) throw Broken();
            PaymentSupplementPolicy.ValidateUse(row["policy"].AsBsonDocument, scope, row["recordedAt"]);
            Metadata(row);
            var operation = row["revision"] == 1L ? PaymentSupplementBson.CreateOperation : PaymentSupplementBson.AttachOperation;
            if (row["operation"] != operation || row["actorId"] != head["uploaderId"] ||
                row["bodyHash"] != AuthorizationStore.CommandHash(Body(row), row["reason"].AsString)) throw Broken();
            if (row["revision"] == 2L) {
                attachment = await AttachmentAsync(tx, id, ct);
                if (attachment["historyVersionId"] != row["_id"] || attachment["snapshotHash"] != row["attachmentHash"] || attachment["attachedAt"] != row["recordedAt"] ||
                    !attachment["policy"].Equals(row["policy"]) || attachment["requestKey"] != row["requestKey"] || attachment["bodyHash"] != row["bodyHash"] ||
                    !attachment["primary"].Equals(row["primary"]) || attachment["uploaderId"] != row["uploaderId"] || attachment["actorGrantVersion"] != row["actorGrantVersion"] ||
                    attachment["reason"] != row["reason"] || attachment["correlationId"] != row["correlationId"] || !attachment["evidence"].Equals(row["evidence"]) ||
                    attachment["reservationId"] != row["reservationId"] || row["state"] != "attached") throw Broken();
            } else if (!row["attachmentHash"].IsBsonNull || row["state"] != "draft" || row["recordedAt"] != head["createdAt"]) throw Broken();
            var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
            var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
            if (receipt is null || receipt["bodyHash"] != row["bodyHash"] || receipt["createdAt"].CompareTo(row["recordedAt"]) > 0 ||
                !receipt["result"].Equals(PaymentSupplementBson.Result(row, row["revision"] == 1L ? null : attachment)) ||
                audit is null || !audit.Equals(PaymentSupplementBson.Audit(row, row["revision"] == 1L ? null : attachment))) throw Broken();
            if (previous.IsBsonNull && row["previousVersionId"] != BsonNull.Value || !previous.IsBsonNull && versions[0]["recordedAt"].CompareTo(row["recordedAt"]) > 0) throw Broken();
            previous = row["_id"]; previousHash = row["snapshotHash"];
        }
        if (head["versionId"] != previous || head["versionHash"] != previousHash || head["state"] != versions[^1]["state"] || head["updatedAt"] != versions[^1]["recordedAt"] ||
            (head["state"] == "attached") != (attachment is not null)) throw Broken();
        if (attachment is null && await Rows(PaymentSupplementBson.Attachments).Find(tx, new BsonDocument("_id", id)).AnyAsync(ct)) throw Broken();
        return new(head, versions, attachment);
    }
    internal async Task<BsonDocument> AttachmentAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        Transaction(tx); PaymentSupplementRules.Id(id);
        var row = await RowAsync(tx, PaymentSupplementBson.Attachments, new("_id", id), ct);
        if (row["snapshotHash"] != PaymentSupplementBson.Hash(row) || row["supplementRevision"] != 2L) throw Broken();
        var primary = PaymentSupplementBson.Primary(row["primary"].AsBsonDocument);
        var primarySubmission = await new PaymentSubmissionRecords(database).ReadAsync(tx, primary.SubmissionId, ct);
        SamePrimary(primary, primarySubmission, row["primarySubmissionHash"]);
        var scope = await PrimaryScopeAsync(tx, primarySubmission, ct);
        if (!row["scope"].Equals(scope) || primarySubmission["submittedAt"].CompareTo(row["attachedAt"]) > 0) throw Broken();
        var reference = PaymentSubmissionBson.Reference(row["evidence"].AsBsonDocument);
        var original = await evidence.RequireOriginalAsync(tx, id, row["reservationId"].AsInt64, reference, row["uploaderId"].AsInt64, ct);
        if (!row["original"].Equals(UnitToko.Platform.DocumentProcessing.PdfDocumentExtractionRecords.Original(original)) || reference.Sha256 != original.Content.Sha256 ||
            reference == primary.PrimaryEvidence || row["uploaderId"] != primarySubmission["uploaderId"]) throw Broken();
        PaymentSupplementPolicy.ValidateUse(row["policy"].AsBsonDocument, scope, row["attachedAt"]);
        Metadata(row);
        await evidence.RequireHistoricalPinAsync(tx, row, ct);
        var job = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", PaymentSupplementBson.JobKey(id))).SingleOrDefaultAsync(ct) ?? throw Broken();
        if (job["kind"] != PaymentSupplementBson.JobKind || !job["payload"].Equals(PaymentSupplementBson.JobPayload(row)) || job["createdAt"] != row["attachedAt"]) throw Broken();
        // Also inspect pending/running jobs: a hidden orphan result, foreign receipt
        // or partial-page output must not become valid merely because status lags.
        await evidence.RequireCompletedDocumentResultAsync(tx, job, row, ct);
        return row;
    }
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string name, BsonDocument identity, CancellationToken ct) {
        Transaction(tx);
        return await Rows(name).Find(tx, new BsonDocument("$and", new BsonArray { identity, PaymentSupplementSchemas.Validator(name) })).SingleOrDefaultAsync(ct) ?? throw Broken();
    }
    private async Task<BsonDocument> PrimaryScopeAsync(IClientSessionHandle tx, BsonDocument submission, CancellationToken ct) {
        var version = await new PaymentDraftRecords(database).VersionAsync(tx, submission["declarationVersionId"].AsInt64, ct);
        var party = version["party"];
        return new() { { "organizationId", party["fulfillmentOrganizationId"] }, { "shopId", party["shopId"] }, { "channelId", party["channelId"] } };
    }
    private static void Metadata(BsonDocument row) {
        PaymentSupplementRules.Text(row["requestKey"].AsString, 128); PaymentSupplementRules.Text(row["reason"].AsString, 1000);
        PaymentSupplementRules.Text(row["correlationId"].AsString, 128);
        if (row["requestKey"].AsString.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or ':'))) throw Broken();
    }
    internal static void SamePrimary(PaymentSupplementPrimary primary, BsonDocument submission, BsonValue hash) {
        if (submission["_id"] != primary.SubmissionId || submission["declarationId"] != primary.DeclarationId || submission["declarationRevision"] != primary.DeclarationRevision ||
            submission["snapshotHash"] != hash || !submission["evidence"].Equals(PaymentSubmissionBson.Reference(primary.PrimaryEvidence))) throw Broken();
    }
    internal static object Body(BsonDocument version) => version["revision"] == 1L
        ? PaymentSupplementBson.CreateBody(PaymentSupplementBson.Primary(version["primary"].AsBsonDocument))
        : PaymentSupplementBson.AttachBody(new(version["supplementId"].AsInt64, 1, version["reservationId"].AsInt64,
            PaymentSubmissionBson.Reference(version["evidence"].AsBsonDocument)));
    private void Transaction(IClientSessionHandle tx) { if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw Broken(); }
    internal static PersistenceConflictException Broken() => new("payment_supplement_history_invalid", "Riwayat lampiran perlu diperiksa petugas; gambar utama dan PDF asli tetap dipertahankan.");
}
