using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Billing.PaymentSupplementBson;

namespace UnitToko.Platform.Billing;

public sealed partial class PaymentSupplementService
{
    // Metadata-only recovery remains possible after an approval expires. Original byte
    // reads and writes use WithAdmissionAsync instead, which requires the live policy.
    internal async Task<T> WithEvidenceMetadataAsync<T>(IClientSessionHandle tx, ActorContext actor, long declarationId, long supplementId,
        long expectedRevision, Func<PaymentSupplementAdmissionAuthority, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        RequireEvidenceTransaction(tx); if (expectedRevision != 1) throw PaymentDraftRecords.Changed();
        var authority = await authorization.LoadAsync(actor, tx, ct);
        var history = await records.HistoryAsync(tx, supplementId, ct);
        if (history.Head["revision"] != expectedRevision || history.Attachment is not null) throw PaymentDraftRecords.Changed();
        var primary = B.Primary(history.Head["primary"].AsBsonDocument);
        if (primary.DeclarationId != declarationId) throw new AuthorizationDeniedException("scope_denied");
        var parent = await declarations.SupplementParentAsync(actor, authority, tx, declarationId, primary.DeclarationRevision, primary, false, ct);
        RequireOwner(history, parent);
        var result = await action(new(supplementId, 1, history.Head["uploaderId"].AsInt64, primary, parent.PolicyScope, null, null), ct);
        await FenceHeadAsync(tx, history.Head, ct); await declarations.SupplementParentFenceAsync(actor, authority, tx, parent, false, ct);
        return result;
    }
    internal async Task<PaymentSupplementPrimary> EvidencePrimaryAsync(IClientSessionHandle tx, ActorContext actor, long declarationId,
        long supplementId, bool review, CancellationToken ct)
    {
        RequireEvidenceTransaction(tx); PaymentSupplementRules.Id(declarationId); PaymentSupplementRules.Id(supplementId);
        var authority = await authorization.LoadAsync(actor, tx, ct); var history = await records.HistoryAsync(tx, supplementId, ct);
        var primary = B.Primary(history.Head["primary"].AsBsonDocument);
        if (primary.DeclarationId != declarationId) throw new AuthorizationDeniedException("scope_denied");
        var parent = await declarations.SupplementParentAsync(actor, authority, tx, declarationId, primary.DeclarationRevision, primary, review, ct);
        RequireOwner(history, parent); await FenceHeadAsync(tx, history.Head, ct);
        await declarations.SupplementParentFenceAsync(actor, authority, tx, parent, review, ct); return primary;
    }
    // During the owning attach transaction, head 1 intentionally precedes CAS to 2.
    // Full HistoryAsync is not applicable until pin, receipt result and CAS all exist.
    // This narrow pending check cannot escape the actual transaction or manufacture
    // a historical actor; final head/policy/parent fences remain the owning command's.
    internal async Task<PaymentSupplementAdmissionAuthority> PendingEvidenceAsync(IClientSessionHandle tx, ActorContext actor,
        PaymentSupplementAssociation association, CancellationToken ct)
    {
        RequireEvidenceTransaction(tx);
        if (association.Revision != 2 || association.UploaderId != actor.UserId || association.ActorGrantVersion != actor.GrantVersion) throw PaymentSupplementRecords.Broken();
        var authority = await authorization.LoadAsync(actor, tx, ct);
        var parent = await declarations.SupplementParentAsync(actor, authority, tx, association.Primary.DeclarationId,
            association.Primary.DeclarationRevision, association.Primary, false, ct);
        var head = await records.RowAsync(tx, B.Heads, new("_id", association.SupplementId), ct);
        var attachment = await records.RowAsync(tx, B.Attachments, new("_id", association.SupplementId), ct);
        var version = await records.RowAsync(tx, B.Versions, new("_id", attachment["historyVersionId"]), ct);
        var previous = await records.RowAsync(tx, B.Versions, new("_id", head["versionId"]), ct);
        var approval = await policies.RequireAsync(tx, parent.PolicyScope, ct);
        var job = await records.Rows("outboxEvents").Find(tx, new MongoDB.Bson.BsonDocument("sourceKey", B.JobKey(association.SupplementId))).SingleOrDefaultAsync(ct);
        var audit = await records.Rows("auditEvents").Find(tx, new MongoDB.Bson.BsonDocument("_id", version["auditId"])).SingleOrDefaultAsync(ct);
        var receipt = await records.Rows("idempotencyRecords").Find(tx, new MongoDB.Bson.BsonDocument {
            { "actorId", actor.UserId }, { "operation", B.AttachOperation }, { "key", attachment["requestKey"] }, { "bodyHash", attachment["bodyHash"] } }).SingleOrDefaultAsync(ct);
        if (head["revision"] != 1L || head["state"] != "draft" || head["versionHash"] != previous["snapshotHash"] ||
            previous["snapshotHash"] != B.Hash(previous) || previous["supplementId"] != association.SupplementId || previous["revision"] != 1L ||
            attachment["snapshotHash"] != association.AttachmentHash || attachment["snapshotHash"] != B.Hash(attachment) ||
            version["snapshotHash"] != B.Hash(version) || version["previousVersionId"] != previous["_id"] || version["previousHash"] != previous["snapshotHash"] ||
            version["supplementId"] != association.SupplementId || version["revision"] != 2L || version["state"] != "attached" || version["operation"] != B.AttachOperation ||
            version["actorId"] != actor.UserId || version["actorGrantVersion"] != actor.GrantVersion || version["requestKey"] != attachment["requestKey"] ||
            version["bodyHash"] != attachment["bodyHash"] || version["reason"] != attachment["reason"] || version["correlationId"] != attachment["correlationId"] ||
            version["recordedAt"] != attachment["attachedAt"] || version["reservationId"] != attachment["reservationId"] || !version["evidence"].Equals(attachment["evidence"]) ||
            version["attachmentHash"] != attachment["snapshotHash"] || !version["policy"].Equals(attachment["policy"]) ||
            !attachment["policy"].Equals(B.PolicySnapshot(approval)) || !attachment["primary"].Equals(B.Primary(association.Primary)) ||
            attachment["primarySubmissionHash"] != association.PrimarySubmissionHash || parent.Submission["snapshotHash"] != association.PrimarySubmissionHash ||
            !head["primary"].Equals(attachment["primary"]) || !head["scope"].Equals(parent.PolicyScope) || !attachment["scope"].Equals(parent.PolicyScope) ||
            attachment["uploaderId"] != actor.UserId || attachment["actorGrantVersion"] != actor.GrantVersion || head["uploaderId"] != actor.UserId ||
            attachment["pinId"] != association.PinId || attachment["reservationId"] != association.ReservationId ||
            !attachment["evidence"].Equals(PaymentSubmissionBson.Reference(association.PdfEvidence)) ||
            !attachment["original"].Equals(UnitToko.Platform.DocumentProcessing.PdfDocumentExtractionRecords.Original(association.Original)) ||
            attachment["attachedAt"].AsBsonDateTime.MillisecondsSinceEpoch != association.AttachedAt.ToUnixTimeMilliseconds() ||
            attachment["reason"] != association.Reason || attachment["correlationId"] != association.CorrelationId ||
            receipt is null || !receipt["result"].IsBsonDocument || receipt["result"].AsBsonDocument.ElementCount != 0 ||
            audit is null || !audit.Equals(B.Audit(version, attachment)) || job is null || job["kind"] != B.JobKind || job["status"] != "pending" ||
            !job["payload"].Equals(B.JobPayload(attachment)) || job["createdAt"] != attachment["attachedAt"])
            throw PaymentSupplementRecords.Broken();
        PaymentSupplementPolicy.ValidateUse(attachment["policy"].AsBsonDocument, parent.PolicyScope, attachment["attachedAt"]);
        return new(association.SupplementId, 1, actor.UserId, association.Primary, parent.PolicyScope, null, null,
            new DateTimeOffset(approval["expiresAt"].ToUniversalTime()));
    }
    private void RequireEvidenceTransaction(IClientSessionHandle tx)
    {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw PaymentSupplementRecords.Broken();
    }
}
