using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.PaymentSupplementBson;

namespace UnitToko.Platform.Billing;

// Additive draft: only this owner writes supplement heads/history/association.
// Existing Billing payment/image, bank, inventory and accounting owners remain unchanged.
public sealed partial class PaymentSupplementService
{
    private readonly IMongoDatabase database;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly PaymentDraftService declarations;
    private readonly AuthorizationStore authorization;
    private readonly PaymentSupplementRecords records;
    private readonly PaymentSupplementPolicy policies;
    private readonly IPaymentSupplementEvidenceHistory evidence;
    internal PaymentSupplementService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        PaymentDraftService declarations, IPaymentSupplementEvidenceHistory evidence) {
        this.database = database; this.ids = ids; this.clock = clock; this.declarations = declarations; this.evidence = evidence;
        authorization = new(database, unit, clock); records = new(database, evidence); policies = new(database, clock);
    }
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata); PaymentSupplementRules.Text(metadata.Reason, 1000); PaymentSupplementRules.Text(metadata.CorrelationId, 128);
        PaymentSupplementRules.Text(metadata.IdempotencyKey, 128);
        if (metadata.IdempotencyKey.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or ':'))) throw PaymentSupplementRules.Invalid();
    }
    private async Task StorageAsync(CancellationToken ct) {
        await declarations.PaymentEvidenceStorageAsync(ct); await PaymentSupplementStorage.RequireAsync(database, ct);
    }
    private CommandAuthorization Command(ActorContext actor, AuthorizationSnapshot authority, PaymentSupplementParent parent) {
        var resources = PaymentDraftBson.Scopes(parent.Scopes);
        return new(ScopePolicy.Authorize(actor, authority, Permissions.PaymentEvidence, resources[0], clock.UtcNow),
            [.. resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory)).Distinct()], Permissions.PaymentEvidence, new(resources[0]));
    }
    public async Task<BsonDocument> CreateAsync(ActorContext actor, PaymentSupplementPrimary input, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = input.Validate(); Metadata(metadata); await StorageAsync(ct); var body = B.CreateBody(input);
        var recovered = await LookupAsync(actor, input.DeclarationId, input.DeclarationRevision, B.CreateOperation, metadata.IdempotencyKey, null, ct);
        if (recovered is not null) {
            if (recovered.BodyHash != AuthorizationStore.CommandHash(body, metadata.Reason)) throw new PersistenceConflictException("idempotency_key_reused", "Kunci lampiran telah dipakai untuk permintaan lain.");
            return recovered.Result;
        }
        var id = (await ids.NextAsync(B.Heads, ct)).Value; var versionId = (await ids.NextAsync(B.Versions, ct)).Value;
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        PaymentSupplementParent? parent = null; BsonDocument? policy = null;
        return await authorization.ExecuteCommandAsync(actor, B.CreateOperation, metadata, body, async (authority, tx, token) => {
            parent = await declarations.SupplementParentAsync(actor, authority, tx, input.DeclarationId, input.DeclarationRevision, input, false, token);
            var priorReceipt = await LookupVersionAsync(tx, actor.UserId, B.CreateOperation, metadata.IdempotencyKey, token);
            policy = priorReceipt is null ? await policies.RequireAsync(tx, parent.PolicyScope, token) : null;
            return Command(actor, authority, parent);
        }, async (tx, token) => {
            var source = parent ?? throw PaymentSupplementRecords.Broken(); var approval = policy ?? throw PaymentSupplementRecords.Broken(); var now = Now;
            var count = await records.Rows(B.Heads).CountDocumentsAsync(tx, new BsonDocument("primary.submissionId", input.SubmissionId), cancellationToken: token);
            if (count >= approval["maximumSupplements"].AsInt32) throw new PersistenceConflictException("payment_supplement_limit", "Jumlah lampiran mencapai batas kebijakan unit; hubungi petugas.");
            var version = Version(versionId, id, 1, source, actor.UserId, null, null, B.PolicySnapshot(approval));
            B.Metadata(version, B.CreateOperation, auditId, actor.UserId, actor.GrantVersion, metadata, body, now);
            var head = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "revision", 1L }, { "state", "draft" },
                { "primary", B.Primary(source.Binding) }, { "primarySubmissionHash", source.Submission["snapshotHash"] }, { "uploaderId", actor.UserId },
                { "scope", source.PolicyScope }, { "versionId", versionId }, { "versionHash", version["snapshotHash"] }, { "createdAt", now.UtcDateTime },
                { "updatedAt", now.UtcDateTime }, { "fence", 0L } };
            await records.Rows(B.Heads).InsertOneAsync(tx, head, cancellationToken: token);
            await records.Rows(B.Versions).InsertOneAsync(tx, version, cancellationToken: token);
            await records.Rows("auditEvents").InsertOneAsync(tx, B.Audit(version, null), cancellationToken: token);
            return B.Result(version, null);
        }, ct, async (authority, tx, token) => {
            var saved = await LookupVersionAsync(tx, actor.UserId, B.CreateOperation, metadata.IdempotencyKey, token) ?? throw PaymentSupplementRecords.Broken();
            if (B.Primary(saved.Version["primary"].AsBsonDocument) != input || saved.Version["bodyHash"] != AuthorizationStore.CommandHash(body, metadata.Reason)) throw PaymentSupplementRecords.Broken();
            if (policy is not null) await policies.FenceAsync(tx, policy, token);
            await declarations.SupplementParentFenceAsync(actor, authority, tx, parent ?? throw PaymentSupplementRecords.Broken(), false, token);
        });
    }
    internal async Task<BsonDocument> AttachAsync(ActorContext actor, PaymentSupplementAttachInput input, VerifiedPaymentSupplementOriginal original,
        Func<IClientSessionHandle, PaymentSupplementAssociation, CancellationToken, Task<Action>> pin, AuthorizationCommandMetadata metadata, CancellationToken ct) {
        input = input.Validate(); Metadata(metadata); await StorageAsync(ct); var body = B.AttachBody(input);
        var existingHead = await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var head = await records.RowAsync(tx, B.Heads, new("_id", input.SupplementId), token);
            var binding = B.Primary(head["primary"].AsBsonDocument);
            var owner = await declarations.SupplementParentAsync(actor, authority, tx, binding.DeclarationId, binding.DeclarationRevision, binding, false, token);
            if (head["uploaderId"] != actor.UserId) throw PaymentSupplementRecords.Broken();
            await declarations.SupplementParentFenceAsync(actor, authority, tx, owner, false, token); return head;
        }, ct);
        var primary = B.Primary(existingHead["primary"].AsBsonDocument);
        var recovered = await LookupAsync(actor, primary.DeclarationId, primary.DeclarationRevision, B.AttachOperation, metadata.IdempotencyKey, input.SupplementId, ct);
        if (recovered is not null) {
            if (recovered.BodyHash != AuthorizationStore.CommandHash(body, metadata.Reason)) throw new PersistenceConflictException("idempotency_key_reused", "Kunci lampiran telah dipakai untuk permintaan lain.");
            return recovered.Result;
        }
        var versionId = (await ids.NextAsync(B.Versions, ct)).Value; var pinId = (await ids.NextAsync("evidencePins", ct)).Value;
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        PaymentSupplementHistory? history = null; PaymentSupplementParent? parent = null; BsonDocument? policy = null; Action? finalPin = null;
        return await authorization.ExecuteCommandAsync(actor, B.AttachOperation, metadata, body, async (authority, tx, token) => {
            history = await records.HistoryAsync(tx, input.SupplementId, token);
            var priorReceipt = await LookupVersionAsync(tx, actor.UserId, B.AttachOperation, metadata.IdempotencyKey, token);
            if (history.Head["uploaderId"] != actor.UserId || priorReceipt is null && (history.Head["revision"] != input.ExpectedRevision || history.Attachment is not null)) throw PaymentDraftRecords.Changed();
            parent = await declarations.SupplementParentAsync(actor, authority, tx, primary.DeclarationId, primary.DeclarationRevision, primary, false, token);
            policy = priorReceipt is null ? await policies.RequireAsync(tx, parent.PolicyScope, token) : null;
            finalPin = null; return Command(actor, authority, parent);
        }, async (tx, token) => {
            var prior = history ?? throw PaymentSupplementRecords.Broken(); var source = parent ?? throw PaymentSupplementRecords.Broken();
            var approval = policy ?? throw PaymentSupplementRecords.Broken(); var now = Now;
            var persistedOriginal = await evidence.RequireOriginalAsync(tx, input.SupplementId, input.ReservationId, input.Evidence, actor.UserId, token);
            original.Require(actor, input, persistedOriginal, clock);
            if (input.Evidence == source.Binding.PrimaryEvidence) throw PaymentSupplementRules.Invalid();
            var attachment = new BsonDocument { { "_id", input.SupplementId }, { "schemaVersion", 1 }, { "supplementRevision", 2L }, { "historyVersionId", versionId },
                { "primary", B.Primary(source.Binding) }, { "primarySubmissionHash", source.Submission["snapshotHash"] }, { "scope", source.PolicyScope },
                { "reservationId", input.ReservationId }, { "evidence", PaymentSubmissionBson.Reference(input.Evidence) }, { "uploaderId", actor.UserId },
                { "actorGrantVersion", actor.GrantVersion }, { "pinId", pinId }, { "original", UnitToko.Platform.DocumentProcessing.PdfDocumentExtractionRecords.Original(persistedOriginal) },
                { "policy", B.PolicySnapshot(approval) }, { "attachedAt", now.UtcDateTime }, { "requestKey", metadata.IdempotencyKey },
                { "bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason) }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId } };
            attachment.Add("snapshotHash", B.Hash(attachment));
            var previous = prior.Versions[^1]; var version = Version(versionId, input.SupplementId, 2, source, actor.UserId, previous["_id"], previous["snapshotHash"], B.PolicySnapshot(approval));
            version["reservationId"] = input.ReservationId; version["evidence"] = PaymentSubmissionBson.Reference(input.Evidence); version["attachmentHash"] = attachment["snapshotHash"];
            B.Metadata(version, B.AttachOperation, auditId, actor.UserId, actor.GrantVersion, metadata, body, now);
            await records.Rows(B.Attachments).InsertOneAsync(tx, attachment, cancellationToken: token);
            await records.Rows(B.Versions).InsertOneAsync(tx, version, cancellationToken: token);
            await records.Rows("auditEvents").InsertOneAsync(tx, B.Audit(version, attachment), cancellationToken: token);
            await records.Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", B.JobKind },
                { "sourceKey", B.JobKey(input.SupplementId) }, { "payload", B.JobPayload(attachment) }, { "createdAt", now.UtcDateTime }, { "dueAt", now.UtcDateTime },
                { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            finalPin = await pin(tx, new(input.SupplementId, 2, pinId, actor.UserId, actor.GrantVersion, input.ReservationId, input.Evidence,
                source.Binding, source.Submission["snapshotHash"].AsString, attachment["snapshotHash"].AsString, persistedOriginal, now, metadata.Reason, metadata.CorrelationId), token);
            var next = prior.Head.DeepClone().AsBsonDocument; next["revision"] = 2L; next["state"] = "attached"; next["versionId"] = versionId;
            next["versionHash"] = version["snapshotHash"]; next["updatedAt"] = now.UtcDateTime; next["fence"] = checked(prior.Head["fence"].AsInt64 + 1);
            if ((await records.Rows(B.Heads).ReplaceOneAsync(tx, prior.Head, next, cancellationToken: token)).ModifiedCount != 1) throw PaymentDraftRecords.Changed();
            return B.Result(version, attachment);
        }, ct, async (authority, tx, token) => {
            var saved = await LookupVersionAsync(tx, actor.UserId, B.AttachOperation, metadata.IdempotencyKey, token) ?? throw PaymentSupplementRecords.Broken();
            if (saved.Version["supplementId"] != input.SupplementId || saved.Version["bodyHash"] != AuthorizationStore.CommandHash(body, metadata.Reason)) throw PaymentSupplementRecords.Broken();
            if (policy is not null) await policies.FenceAsync(tx, policy, token);
            await declarations.SupplementParentFenceAsync(actor, authority, tx, parent ?? throw PaymentSupplementRecords.Broken(), false, token);
            finalPin?.Invoke(); token.ThrowIfCancellationRequested();
        });
    }
    private static BsonDocument Version(long versionId, long supplementId, long revision, PaymentSupplementParent parent, long uploader,
        BsonValue? previous, BsonValue? previousHash, BsonDocument policy) => new() { { "_id", versionId }, { "schemaVersion", 1 }, { "supplementId", supplementId },
        { "revision", revision }, { "state", revision == 1 ? "draft" : "attached" }, { "primary", B.Primary(parent.Binding) },
        { "primarySubmissionHash", parent.Submission["snapshotHash"] }, { "uploaderId", uploader }, { "scope", parent.PolicyScope }, { "policy", policy },
        { "previousVersionId", previous ?? BsonNull.Value }, { "previousHash", previousHash ?? BsonNull.Value },
        { "attachmentHash", BsonNull.Value }, { "reservationId", BsonNull.Value }, { "evidence", BsonNull.Value } };
}
