using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using V = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Platform.Billing;

public sealed partial class PaymentDraftService
{
    // Called only after the current buyer declaration owner has authorized the request.
    // These retained proofs are not a bank search API and never confer finance grants.
    private async Task<BsonDocument> TimelineVerificationAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long verificationId, OrderTimelineHistory proof, Dictionary<long, BsonDocument> seen, CancellationToken ct) {
        if (seen.TryGetValue(verificationId, out var captured)) return captured;
        var saved = await new PaymentVerificationRecords(database).RawAsync(tx, verificationId, ct);
        if (saved["recordedAt"].ToUniversalTime() > Now.UtcDateTime) throw OrderTimelineHistory.Broken();
        var expected = saved["source"].AsBsonDocument;
        var head = await records.HeadAsync(tx, expected["recordedBy"].AsInt64, expected["declarationId"].AsInt64, ct);
        var version = await records.VersionAsync(tx, head["versionId"].AsInt64, ct);
        var submission = await new PaymentSubmissionRecords(database).ReadAsync(tx, head["submissionId"].AsInt64, ct);
        var source = PaymentDraftBson.SubmittedSource(head, version, submission);
        OrderTimelineHistory.Equal(expected, source);
        foreach (var invoice in source["invoices"].AsBsonArray)
            OrderTimelineHistory.Equal(invoice, await invoices.PaymentSourceAsync(actor, authority, tx, invoice["invoiceId"].AsInt64, ct));
        proof.Hold(PaymentDraftBson.Versions, version);
        await proof.EffectsAsync(version, PaymentDraftBson.CommandOperation(version), "recordedBy", ct, outbox: false);
        proof.Hold(PaymentSubmissionBson.Collection, submission);
        await proof.EffectsAsync(submission, PaymentSubmissionBson.Operation, "submittedBy", ct, PaymentSubmissionBson.OcrKey(submission));
        var pin = await EvidencePinRecords.ReadAsync(database, tx, submission["pinId"].AsInt64, ct);
        if (database.Client is not MongoClient client) throw OrderTimelineHistory.Broken();
        var evidence = new EvidenceService(database, new MongoUnitOfWork(client), new(database), clock);
        await evidence.ValidateHistoricalPinAsync(tx, pin, ct);
        proof.Hold("evidencePins", pin);
        await proof.HoldAsync("evidencePinAudits", new("pinId", pin["_id"]), ct);

        var receipt = await BankReceiptRecords.ReadReceiptHistoryAsync(database, tx, saved["receiptId"].AsInt64, ct);
        await proof.CaptureBankReceiptAsync(receipt, ct);
        await new BankTransactionClaims(database).RequireCreditAsync(tx, receipt, ct);
        await proof.HoldAsync(BankTransactionClaims.Collection, new("_id", receipt["canonicalIdentity"]), ct);
        var import = await new BankStatementRecords(database).ImportAsync(tx, receipt["importId"].AsInt64, ct);
        var account = import["account"].AsBsonDocument;
        var bankLine = import["preview"]["rows"].AsBsonArray.Single(row => row["number"] == receipt["rowNumber"]).AsBsonDocument;
        var bank = new BankReceiptPaymentSource(receipt, account, bankLine);
        OrderTimelineHistory.Equal(saved["receiptHash"], receipt["snapshotHash"]);
        var review = await TimelineAcceptedReviewAsync(tx, source, saved["review"].AsBsonDocument, evidence, proof, ct);
        OrderTimelineHistory.Equal(saved["review"], review);
        PaymentVerificationRecords.Facts(source, bank, review);
        PaymentVerificationPolicy.RequireResolutions(PaymentVerificationRecords.Discrepancies(source, bank, review), V.Input(saved));
        OrderTimelineHistory.Equal(saved["binding"], V.Binding(source, bank, review, saved["policy"].AsBsonDocument));
        OrderTimelineHistory.Equal(saved["policy"]["scope"], receipt["scope"]);
        if (receipt["recordedAt"].CompareTo(saved["recordedAt"]) > 0) throw OrderTimelineHistory.Broken();
        proof.Hold(BankReceiptBson.Receipts, receipt); proof.Hold(BankStatementBson.Imports, import);
        await proof.EffectsAsync(import, BankStatementBson.Operation, "actorId", ct, BankStatementBson.SourceKey(import));
        proof.Hold(ReceivingAccountBson.Versions, account);
        var confirmation = await proof.HoldAsync(BankReceiptBson.Confirmations, new("_id", receipt["confirmationId"]), ct);
        await proof.EffectsAsync(confirmation, BankReceiptBson.Operation, "actorId", ct);
        await proof.HoldAsync("journalEntries", new("_id", receipt["journalId"]), ct);
        await proof.HoldAsync("accountingAuditEvents", new("eventKey", "accounting:journal:" + receipt["journalId"].AsInt64), ct);
        await proof.HoldAsync("outboxEvents", new("sourceKey", "accounting:journal:" + receipt["journalId"].AsInt64), ct);
        var party = await new PaymentReceiptPurposeRecords(database).CustomerAsync(tx, receipt["_id"].AsInt64, receipt["snapshotHash"], source["party"].AsBsonDocument, true, ct) ?? throw OrderTimelineHistory.Broken();
        proof.Hold(V.Parties, party); proof.Hold(V.Decisions, saved);
        await proof.EffectsAsync(saved, V.Operation, "actorId", ct);
        seen.Add(verificationId, saved);
        if (party["firstVerificationId"] != saved["_id"])
            _ = await TimelineVerificationAsync(actor, authority, tx, party["firstVerificationId"].AsInt64, proof, seen, ct);
        return saved;
    }

    private async Task<BsonDocument> TimelineAcceptedReviewAsync(IClientSessionHandle tx, BsonDocument source,
        BsonDocument saved, EvidenceService evidence, OrderTimelineHistory proof, CancellationToken ct) {
        var id = source["declarationId"].AsInt64;
        if (saved.GetValue("kind", "ocr") == "manual") {
            var store = new PaymentManualReviewRecords(database);
            var latest = await store.LatestAsync(tx, source, ct) ?? throw OrderTimelineHistory.Broken();
            foreach (var candidate in await proof.CaptureAsync(PaymentManualReviewRecords.Versions, new("declarationId", id), ct)) {
                var row = await store.VersionAsync(tx, candidate["_id"].AsInt64, ct);
                await evidence.ValidateAcceptedManualFailureAsync(tx, id, source["revision"].AsInt64, source["submissionId"].AsInt64,
                    PaymentSubmissionBson.Reference(source["evidence"].AsBsonDocument), row["failure"].AsBsonDocument, ct);
                proof.Hold(PaymentManualReviewRecords.Versions, row);
                await proof.EffectsAsync(row, PaymentManualReviewRecords.Operation, "actorId", ct);
                var failure = await proof.HoldAsync("documentProcessingFailures", new("_id", row["failure"]["failureId"]), ct);
                await proof.HoldAsync("documentProcessingReadAudits", new("_id", failure["readAuditId"]), ct);
                await proof.HoldAsync("outboxEvents", new("_id", failure["outboxId"]), ct);
            }
            return PaymentManualReviewRecords.Review(latest);
        }
        var binding = PaymentCorrectionBson.Binding(saved["binding"].AsBsonDocument);
        if (binding.DeclarationRevision != source["revision"].AsInt64 || binding.SubmissionId != source["submissionId"].AsInt64 ||
            binding.Evidence != PaymentSubmissionBson.Reference(source["evidence"].AsBsonDocument)) throw OrderTimelineHistory.Broken();
        var job = await proof.HoldAsync("outboxEvents", new() { { "kind", "payment.ocr.requested" }, { "payload.submissionId", binding.SubmissionId } }, ct);
        var extracted = await new ExtractionRecords(database).ResultAsync(tx, job, ct) ?? throw OrderTimelineHistory.Broken();
        if (extracted["_id"] != binding.ExtractionId || extracted["binding"]["declarationId"] != id || extracted["binding"]["declarationRevision"] != binding.DeclarationRevision ||
            extracted["binding"]["submissionId"] != binding.SubmissionId || !InvoiceRecords.Equal(extracted["binding"]["reference"], source["evidence"])) throw OrderTimelineHistory.Broken();
        var view = new PaymentExtractionView(id, binding.SubmissionId, binding.Evidence, "succeeded", binding.ExtractionId,
            new(extracted["createdAt"].ToUniversalTime()), ExtractionRecords.Extract(extracted));
        var review = await VerificationReviewAsync(tx, view, binding.DeclarationRevision, ct);
        proof.Hold("outboxEvents", job); proof.Hold("documentExtractionResults", extracted);
        await proof.HoldAsync("documentExtractionAudits", new("_id", extracted["auditId"]), ct);
        await proof.HoldAsync("documentProcessingReadAudits", new("_id", extracted["readAuditId"]), ct);
        foreach (var row in await proof.CaptureAsync(PaymentCorrectionBson.Versions, new("declarationId", id), ct)) {
            proof.Hold(PaymentCorrectionBson.Versions, row);
            await proof.EffectsAsync(row, PaymentCorrectionBson.Operation, "recordedBy", ct);
        }
        return review;
    }
}
