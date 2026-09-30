using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using M = UnitToko.Platform.Billing.PaymentManualReviewRecords;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Platform.Billing;

public sealed record PaymentManualReviewView(string Binding, long ReviewRevision, BsonDocument Source, BsonDocument Failure, BsonDocument Policy, BsonDocument? Latest);

public sealed partial class PaymentVerificationService
{
    private async Task ManualStorageAsync(CancellationToken ct) {
        await StorageAsync(ct);
        foreach (var name in PaymentManualReviewSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, PaymentManualReviewSchemas.Validator(name), PaymentManualReviewSchemas.Key(name), ct);
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", B.Decisions) }, ct)).SingleOrDefaultAsync(ct);
        if (definition is null || !InventorySchemas.Contains(definition["options"]["validator"].AsBsonDocument, PaymentManualReviewSchemas.VerificationValidator())) throw B.Broken();
    }
    private async Task<BsonDocument?> SavedManualAsync(Context context, CancellationToken ct) {
        var store = new M(database); var row = await store.LatestAsync(context.Tx, context.Source!, ct);
        if (row is null) return null;
        using var history = await store.Rows(M.Versions).Find(context.Tx, new BsonDocument("declarationId", context.Source!["declarationId"])).ToCursorAsync(ct);
        while (await history.MoveNextAsync(ct)) foreach (var version in history.Current)
            await evidence.ValidateAcceptedManualFailureAsync(context.Tx, context.Source["declarationId"].AsInt64, context.Source["revision"].AsInt64, context.Source["submissionId"].AsInt64,
                PaymentSubmissionBson.Reference(context.Source["evidence"].AsBsonDocument), version["failure"].AsBsonDocument, ct);
        await store.TouchAsync(context.Tx, M.Versions, row, ct); return row;
    }
    private async Task<PersonAuthorityProof> ManualPeopleAsync(Context context, long checker, CancellationToken ct) {
        var maker = context.Source!["recordedBy"].AsInt64;
        if (maker == checker || maker != context.Source["uploaderId"].AsInt64) throw new AuthorizationDeniedException("independent_person_required");
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [maker, checker], ct);
        if (!PersonIndependencePolicy.AreIndependent(proof.Evidence.Single(p => p.UserId == checker), proof.Evidence.Single(p => p.UserId == maker))) throw new AuthorizationDeniedException("independent_person_required");
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct); return proof;
    }
    private async Task<BsonDocument> ManualPolicyAsync(Context context, string failure, CancellationToken ct) {
        var party = context.Source!["party"];
        var scope = new BsonDocument { { "fulfillmentOrganizationId", party["fulfillmentOrganizationId"] }, { "shopId", party["shopId"] }, { "channelId", party["channelId"] }, { "warehouseId", BsonNull.Value } };
        var store = new M(database);
        var row = await store.Rows(M.Policies).Find(context.Tx, Builders<BsonDocument>.Filter.Eq("scope", scope) & PaymentManualReviewSchemas.Validator(M.Policies)).SingleOrDefaultAsync(ct) ?? throw B.Changed("payment_manual_review_policy_unavailable");
        M.PolicyAt(row, Now, failure); await store.TouchAsync(context.Tx, M.Policies, row, ct);
        context.ManualCurrent.Add(() => M.PolicyAt(row, Now, failure)); return M.Snapshot(row);
    }
    private async Task<BsonDocument> CurrentReviewAsync(Context context, PaymentExtractionView extraction, CancellationToken ct,
        string? reviewSource = null, Func<BsonDocument, bool>? matchesReviewedBinding = null) {
        var manual = await SavedManualAsync(context, ct);
        if (reviewSource == "manual" && manual is null) throw B.Changed("payment_manual_review_required");
        if (manual is null || reviewSource == "ocr") return await declarations.VerificationReviewAsync(context.Tx, extraction, context.Source!["revision"].AsInt64, ct);
        if (matchesReviewedBinding is not null && extraction.State == "succeeded") {
            var recovered = await declarations.VerificationReviewAsync(context.Tx, extraction, context.Source!["revision"].AsInt64, ct);
            // The existing decision binding proves an explicit OCR preview; never silently switch a manual decision.
            if (matchesReviewedBinding(recovered)) return recovered;
        }
        var policy = await ManualPolicyAsync(context, manual["failure"]["safeFailure"].AsString, ct);
        PaymentVerificationRecords.Equal(policy, manual["policy"]);
        var proof = await ManualPeopleAsync(context, manual["actorId"].AsInt64, ct); context.ManualCurrent.Add(() => proof.RequireCurrent(context.Tx, clock.UtcNow));
        return M.Review(manual);
    }
    private async Task<BsonDocument> AcceptedReviewAsync(Context context, BsonDocument saved, CancellationToken ct) {
        if (saved.GetValue("kind", "ocr") != "manual") {
            var binding = PaymentCorrectionBson.Binding(saved["binding"].AsBsonDocument);
            var extraction = await evidence.AcceptedVerificationExtractionAsync(context.Tx, context.Actor, context.Source!["declarationId"].AsInt64, binding, ct);
            return await declarations.VerificationReviewAsync(context.Tx, extraction, context.Source["revision"].AsInt64, ct);
        }
        var manual = await SavedManualAsync(context, ct) ?? throw B.Broken(); var review = M.Review(manual);
        if (context.Verify) { var proof = await ManualPeopleAsync(context, manual["actorId"].AsInt64, ct); context.ManualCurrent.Add(() => proof.RequireCurrent(context.Tx, clock.UtcNow)); }
        PaymentVerificationRecords.Equal(saved, review); return review;
    }
    public async Task<PaymentManualReviewView> PreviewManualReviewAsync(ActorContext actor, long id, CancellationToken ct = default) {
        if (id <= 0) throw PaymentManualReviewPolicy.Invalid(); await ManualStorageAsync(ct);
        var initial = await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await SourceAsync(new(actor, authority, tx, true) { WritePermission = Permissions.PaymentReviewManual }, id, token);
            var proof = await PeopleAsync(context, token); await FenceAsync(context, token); proof.RequireCurrent(tx, clock.UtcNow); return context.Source!;
        }, ct);
        return await evidence.WithVerificationOriginalAsync<PaymentManualReviewView>(actor, id, initial["revision"].AsInt64, initial["reservationId"].AsInt64,
            PaymentSubmissionBson.Reference(initial["evidence"].AsBsonDocument), (consume, token) => authorization.ReadAsync(actor, async (authority, tx, inner) => {
                var context = await SourceAsync(new(actor, authority, tx, true) { WritePermission = Permissions.PaymentReviewManual }, id, inner);
                await records.RequireUnverifiedAsync(tx, id, inner); var proof = await PeopleAsync(context, inner);
                return await consume(tx, async (extraction, eligible, final) => {
                    await new PaymentReviewActionRecords(database).RequireOpenAsync(tx, id, context.Source, final);
                    var failed = await evidence.ManualFailureAsync(tx, extraction, context.Source!["revision"].AsInt64, final);
                    var policy = await ManualPolicyAsync(context, failed.Snapshot["safeFailure"].AsString, final); var latest = await SavedManualAsync(context, final);
                    var revision = latest?["revision"].AsInt64 ?? 0;
                    await AuditReadAsync(context, "manual.preview", id, final); await FenceAsync(context, final); proof.RequireCurrent(tx, clock.UtcNow); failed.RequireCurrent(); eligible();
                    return new PaymentManualReviewView(M.Binding(context.Source, failed.Snapshot, policy, revision), revision, context.Source, failed.Snapshot, policy, latest);
                }, inner);
            }, token), ct);
    }
    public async Task<BsonDocument> RecordManualReviewAsync(ActorContext actor, long id, long revision, PaymentManualReviewInput input,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (id <= 0 || revision is < 0 or >= long.MaxValue - 1) throw PaymentManualReviewPolicy.Invalid(); input = PaymentManualReviewPolicy.Copy(input); Metadata(metadata); await ManualStorageAsync(ct);
        var body = M.Body(id, revision, input); var bodyHash = AuthorizationStore.CommandHash(body, metadata.Reason);
        var previous = await RecoverManualCoreAsync(actor, id, metadata.IdempotencyKey, bodyHash, ct); if (previous is not null) return M.Result(previous);
        var initial = await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await SourceAsync(new(actor, authority, tx, true) { WritePermission = Permissions.PaymentReviewManual }, id, token);
            var proof = await PeopleAsync(context, token); await FenceAsync(context, token); proof.RequireCurrent(tx, clock.UtcNow); return context.Source!;
        }, ct);
        var versionId = (await ids.NextAsync(M.Versions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        return await evidence.WithVerificationOriginalAsync<BsonDocument>(actor, id, initial["revision"].AsInt64, initial["reservationId"].AsInt64,
            PaymentSubmissionBson.Reference(initial["evidence"].AsBsonDocument), (consume, token) => {
                Context? context = null; PersonAuthorityProof? proof = null; Action? eligible = null; Action? failureCurrent = null;
                return authorization.ExecuteCommandAsync(actor, M.Operation, metadata, body, async (authority, tx, inner) => {
                    context = await SourceAsync(new(actor, authority, tx, true) { WritePermission = Permissions.PaymentReviewManual }, id, inner); proof = null; eligible = null; failureCurrent = null;
                    var resources = PaymentDraftBson.Scopes(context.Source!["scopes"].AsBsonArray); var current = context.Source["revision"].AsInt64;
                    return new(ScopePolicy.Authorize(actor, authority, Permissions.PaymentReviewManual, resources[0], clock.UtcNow, new(current, current, RecordedBy: context.Source["recordedBy"].AsInt64)),
                        [.. resources.SelectMany(r => ScopePolicy.RequiredNodes(r, authority.Directory)).Distinct()], Permissions.PaymentReviewManual,
                        new(resources[0], new(current, current, RecordedBy: context.Source["recordedBy"].AsInt64)));
                }, (tx, inner) => consume(tx, async (extraction, check, final) => {
                    eligible = check; var active = context ?? throw B.Broken(); var source = active.Source!; await records.RequireUnverifiedAsync(tx, id, final);
                    await new PaymentReviewActionRecords(database).RequireOpenAsync(tx, id, source, final);
                    var store = new M(database); var latest = await SavedManualAsync(active, final);
                    if ((latest?["revision"].AsInt64 ?? 0) != revision) throw B.Changed();
                    var failure = await evidence.ManualFailureAsync(tx, extraction, source["revision"].AsInt64, final); failureCurrent = failure.RequireCurrent;
                    var policy = await ManualPolicyAsync(active, failure.Snapshot["safeFailure"].AsString, final);
                    if (input.Binding != M.Binding(source, failure.Snapshot, policy, revision)) throw B.Changed();
                    if (latest is not null && latest["fields"].Equals(PaymentCorrectionBson.Fields(input.Fields)) && latest["policy"].Equals(policy) && latest["failure"].Equals(failure.Snapshot)) throw PaymentManualReviewPolicy.Invalid();
                    proof = await PeopleAsync(active, final); var at = Now;
                    var row = new BsonDocument { { "_id", versionId }, { "schemaVersion", 1 }, { "declarationId", id }, { "revision", revision + 1 },
                        { "previousVersionId", latest is null ? BsonNull.Value : latest["_id"] }, { "previousHash", latest is null ? BsonNull.Value : latest["snapshotHash"] },
                        { "source", source }, { "failure", failure.Snapshot }, { "policy", policy }, { "binding", input.Binding }, { "fields", PaymentCorrectionBson.Fields(input.Fields) }, { "imageReviewed", true },
                        { "people", new BsonArray(proof.Evidence.OrderBy(p => p.UserId).Select(ReceivingAccountBson.Person)) }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
                        { "operation", M.Operation }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", bodyHash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId },
                        { "auditId", auditId }, { "recordedAt", at.UtcDateTime }, { "fence", 0L } }; row.Add("snapshotHash", M.Hash(row));
                    await store.Rows(M.Versions).InsertOneAsync(tx, row, cancellationToken: final); await store.Rows("auditEvents").InsertOneAsync(tx, M.Audit(row), cancellationToken: final);
                    await store.Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", M.Operation }, { "sourceKey", M.Operation + ":" + versionId },
                        { "actorId", actor.UserId }, { "requestKey", metadata.IdempotencyKey }, { "payload", M.Result(row) }, { "createdAt", at.UtcDateTime }, { "dueAt", at.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: final);
                    return M.Result(row);
                }, inner), token, async (_, tx, inner) => {
                    var active = context ?? throw B.Broken(); var saved = await SavedManualAsync(active, inner) ?? throw B.Broken();
                    if (saved["_id"] != versionId || saved["bodyHash"] != bodyHash) throw B.Broken(); await FenceAsync(active, inner);
                }, validateCurrent: () => { _ = Require(context ?? throw B.Broken()); proof?.RequireCurrent(context!.Tx, clock.UtcNow); failureCurrent?.Invoke(); eligible?.Invoke(); });
            }, ct);
    }
    public async Task<BsonDocument?> RecoverManualReviewAsync(ActorContext actor, long id, string key, CancellationToken ct = default) {
        Metadata(new(key, "Recover manual payment review", "payment-manual-review-recovery")); await ManualStorageAsync(ct);
        var row = await RecoverManualCoreAsync(actor, id, key, null, ct); return row is null ? null : M.Result(row);
    }
    private Task<BsonDocument?> RecoverManualCoreAsync(ActorContext actor, long id, string key, string? bodyHash, CancellationToken ct) => authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
        var context = await SourceAsync(new(actor, authority, tx, true) { WritePermission = Permissions.PaymentReviewManual }, id, token); var store = new M(database);
        var receipt = await store.Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "operation", M.Operation }, { "key", key } }).SingleOrDefaultAsync(token);
        var saved = await store.Rows(M.Versions).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "requestKey", key } }).SingleOrDefaultAsync(token);
        if ((receipt is null) != (saved is null)) throw B.Broken();
        if (saved is not null) {
            if (saved["declarationId"] != id || bodyHash is not null && saved["bodyHash"] != bodyHash) throw B.Changed("idempotency_key_reused");
            _ = await SavedManualAsync(context, token); saved = await store.VersionAsync(tx, saved["_id"].AsInt64, token);
        }
        var proof = await PeopleAsync(context, token); await AuditReadAsync(context, "manual.recovery", id, token); await FenceAsync(context, token); proof.RequireCurrent(tx, clock.UtcNow); return saved;
    }, ct);
}
