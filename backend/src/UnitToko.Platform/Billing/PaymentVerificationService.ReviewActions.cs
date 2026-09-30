using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using W = UnitToko.Platform.Billing.PaymentReviewActionRecords;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Platform.Billing;

public sealed record PaymentReviewActionView(BsonDocument Source, string Binding, long ReviewRevision, BsonDocument? Latest, bool Verified, DateTimeOffset SubmittedAt, DateTimeOffset ObservedAt);
public sealed partial class PaymentVerificationService
{
    public async Task<PaymentReviewActionView> ReadReviewActionsAsync(ActorContext actor, long id, CancellationToken ct = default) {
        if (id <= 0) throw PaymentReviewActionPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await SourceAsync(new(actor, authority, tx, false), id, token); var store = new W(database);
            var latest = await store.LatestAsync(tx, id, context.Source, token); var revision = latest?["revision"].AsInt64 ?? 0;
            var decision = await records.Rows(B.Decisions).Find(tx, new BsonDocument("declarationId", id)).SingleOrDefaultAsync(token);
            if (decision is not null) _ = await SavedAsync(context, decision, token); else await records.RequireUnverifiedAsync(tx, id, token);
            var submission = await new PaymentSubmissionRecords(database).ReadAsync(tx, context.Source!["submissionId"].AsInt64, token);
            await AuditReadAsync(context, "workflow", id, token); await FenceAsync(context, token);
            return new PaymentReviewActionView(context.Source!, W.Binding(context.Source!, revision), revision, latest, decision is not null, new(submission["submittedAt"].ToUniversalTime()), Now);
        }, ct);
    }
    public async Task<BsonDocument> RecordReviewActionAsync(ActorContext actor, long id, long revision, PaymentReviewActionInput input, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (id <= 0 || revision is < 0 or >= long.MaxValue - 1) throw PaymentReviewActionPolicy.Invalid(); input = PaymentReviewActionPolicy.Copy(input); Metadata(metadata); await StorageAsync(ct);
        var body = W.Body(id, revision, input); var bodyHash = AuthorizationStore.CommandHash(body, metadata.Reason);
        var recovered = await RecoverReviewActionCoreAsync(actor, id, metadata.IdempotencyKey, bodyHash, ct); if (recovered is not null) return W.Result(recovered);
        var store = new W(database); var versionId = (await ids.NextAsync(W.Versions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        Context? context = null; PersonAuthorityProof? proof = null;
        return await authorization.ExecuteCommandAsync(actor, W.Operation, metadata, body, async (authority, tx, token) => {
            context = await SourceAsync(new(actor, authority, tx, true) { WritePermission = Permissions.PaymentReviewManage }, id, token); proof = null;
            var resources = PaymentDraftBson.Scopes(context.Source!["scopes"].AsBsonArray); var sourceRevision = context.Source["revision"].AsInt64;
            return new(ScopePolicy.Authorize(actor, authority, Permissions.PaymentReviewManage, resources[0], clock.UtcNow, new(sourceRevision, sourceRevision, RecordedBy: context.Source["recordedBy"].AsInt64)),
                [.. resources.SelectMany(r => ScopePolicy.RequiredNodes(r, authority.Directory)).Distinct()], Permissions.PaymentReviewManage,
                new(resources[0], new(sourceRevision, sourceRevision, RecordedBy: context.Source["recordedBy"].AsInt64)));
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); var source = active.Source!; await records.RequireUnverifiedAsync(tx, id, token);
            var previous = await store.LatestAsync(tx, id, source, token);
            if ((previous?["revision"].AsInt64 ?? 0) != revision || input.Binding != W.Binding(source, revision)) throw B.Changed();
            var change = PaymentReviewActionPolicy.Apply(previous?["state"].AsString ?? "pending", previous is null || previous["assigneeId"].IsBsonNull ? null : previous["assigneeId"].AsInt64, input.Action, actor.UserId);
            proof = await PeopleAsync(active, token); var at = Now;
            if (previous is not null && previous["recordedAt"].ToUniversalTime() > at.UtcDateTime) throw B.Broken();
            var row = new BsonDocument { { "_id", versionId }, { "schemaVersion", 1 }, { "declarationId", id }, { "revision", revision + 1 },
                { "previousVersionId", previous is null ? BsonNull.Value : previous["_id"] }, { "previousHash", previous is null ? BsonNull.Value : previous["snapshotHash"] },
                { "source", source }, { "binding", input.Binding }, { "action", input.Action }, { "state", change.State }, { "assigneeId", change.AssigneeId is { } user ? new BsonInt64(user) : BsonNull.Value },
                { "people", new BsonArray(proof.Evidence.OrderBy(p => p.UserId).Select(ReceivingAccountBson.Person)) }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
                { "operation", W.Operation }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", bodyHash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId },
                { "auditId", auditId }, { "recordedAt", at.UtcDateTime }, { "fence", 0L } }; row.Add("snapshotHash", B.Hash(row));
            await store.Rows(W.Versions).InsertOneAsync(tx, row, cancellationToken: token); await store.Rows("auditEvents").InsertOneAsync(tx, W.Audit(row), cancellationToken: token);
            await store.Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", W.Operation }, { "sourceKey", W.Operation + ":" + versionId },
                { "actorId", actor.UserId }, { "requestKey", metadata.IdempotencyKey }, { "payload", W.Result(row) }, { "createdAt", at.UtcDateTime }, { "dueAt", at.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            return W.Result(row);
        }, ct, async (_, tx, token) => {
            var active = context ?? throw B.Broken(); var latest = await store.LatestAsync(tx, id, active.Source, token) ?? throw B.Broken();
            if (latest["_id"] != versionId || latest["bodyHash"] != bodyHash) throw B.Broken(); await FenceAsync(active, token);
        }, validateCurrent: () => { _ = Require(context ?? throw B.Broken()); proof?.RequireCurrent(context!.Tx, clock.UtcNow); });
    }
    public async Task<BsonDocument?> RecoverReviewActionAsync(ActorContext actor, long id, string key, CancellationToken ct = default) {
        if (id <= 0) throw PaymentReviewActionPolicy.Invalid(); Metadata(new(key, "Recover payment review action", "payment-review-action-recovery")); await StorageAsync(ct);
        var row = await RecoverReviewActionCoreAsync(actor, id, key, null, ct); return row is null ? null : W.Result(row);
    }
    private Task<BsonDocument?> RecoverReviewActionCoreAsync(ActorContext actor, long id, string key, string? bodyHash, CancellationToken ct) => authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
        var context = await SourceAsync(new(actor, authority, tx, true) { WritePermission = Permissions.PaymentReviewManage }, id, token); var store = new W(database);
        var latest = await store.LatestAsync(tx, id, context.Source, token); var proof = await PeopleAsync(context, token);
        var raw = await store.Rows(W.Versions).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "requestKey", key } }).SingleOrDefaultAsync(token);
        if (raw is not null) {
            if (raw["declarationId"] != id || latest is null || bodyHash is not null && raw["bodyHash"] != bodyHash) throw B.Changed();
        } else foreach (var name in new[] { "idempotencyRecords", "auditEvents", "outboxEvents" }) {
            var field = name == "idempotencyRecords" ? "key" : "requestKey";
            if (await store.Rows(name).Find(tx, new BsonDocument { { name == "idempotencyRecords" ? "operation" : "kind", W.Operation }, { "actorId", actor.UserId }, { field, key } }).AnyAsync(token)) throw B.Broken();
        }
        await AuditReadAsync(context, "workflow.recover", id, token); await FenceAsync(context, token); proof.RequireCurrent(tx, clock.UtcNow); return raw;
    }, ct);
}
