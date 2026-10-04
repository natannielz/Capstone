using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using B = UnitToko.Platform.Procurement.ProcurementDisbursementBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementDisbursementService
{
    private CommandAuthorization Command(Context context) {
        var query = Require(context).Single(value => value.Permission == Permissions.DisbursementRecord);
        return new(new(true, "allowed", query.Clauses.Where(clause => clause.Matches(context.Resource)).Select(clause => clause.GrantId).Distinct().ToImmutableArray()),
            ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory), Permissions.DisbursementRecord,
            new(context.Resource, new(1, 1, ApprovedBy: context.Source!["funding"]["decision"]["actorId"].AsInt64)));
    }
    private async Task EffectsAsync(Context context, BsonDocument row, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(context.Tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(context.Tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "sourceKey", B.SourceKey(row) },
            { "kind", row["operation"] }, { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "location", row["location"] }, { "authorizationId", row["authorizationId"] },
            { "payload", B.Result(row) }, { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
    private async Task<Context> HistoricalContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long draftId, string permission, CancellationToken ct) {
        var route = await Rows(B.Drafts).Find(tx, new BsonDocument("_id", draftId)).Project(new BsonDocument("location", 1)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        var context = ContextFor(actor, authority, tx, R.Location(route["location"]), permission);
        context.History = new(records, tx, () => Current(context));
        context.Draft = await records.DraftAsync(tx, draftId, ct, history: context.History); context.Source = context.Draft["source"].AsBsonDocument;
        await PeopleAsync(context, permission == Permissions.DisbursementRecord ? context.Source : null, context.Draft, ct);
        context.Capacity = await CapacityAsync(tx, context.Source["funding"].AsBsonDocument, ct, history: context.History); return context;
    }
    private async Task<Context> DraftContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long draftId, bool current, CancellationToken ct) {
        if (!current) {
            var historical = await HistoricalContextAsync(actor, authority, tx, draftId, Permissions.DisbursementRecord, ct);
            if (historical.Draft!["actorId"] != actor.UserId) throw new AuthorizationDeniedException("scope_denied");
            return historical;
        }
        return await CurrentDraftContextAsync(actor, authority, tx, draftId, Permissions.DisbursementRecord, ct);
    }
    private async Task<Context> CurrentDraftContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long draftId, string permission, CancellationToken ct) {
        var raw = await Rows(B.Drafts).Find(tx, new BsonDocument("_id", draftId)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        var context = ContextFor(actor, authority, tx, R.Location(raw["location"]), permission);
        if (raw["actorId"] != actor.UserId) throw new AuthorizationDeniedException("scope_denied");
        if (!raw["recordedDisbursementId"].IsBsonNull) throw B.Changed();
        await CaptureAsync(context, B.Input(raw["input"]), ct); ProcurementDisbursementRecords.Equal(context.Source!, raw["source"]);
        context.Draft = await records.DraftAsync(tx, draftId, ct, context.Proof);
        await PeopleAsync(context, context.Source, context.Draft, ct); Current(context);
        return context;
    }
    private async Task<BsonDocument?> ExistingAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string operation, string key, CancellationToken ct) {
        _ = Query(actor, authority, Permissions.DisbursementRecord); _ = Query(actor, authority, Permissions.ProcurementDisbursementRead);
        var table = operation == B.DraftOperation ? B.Drafts : B.Records;
        var route = await Rows(table).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "operation", operation }, { "requestKey", key } }).Project(new BsonDocument("location", 1)).SingleOrDefaultAsync(ct);
        if (route is not null) _ = ContextFor(actor, authority, tx, R.Location(route["location"]), Permissions.DisbursementRecord);
        return await records.ForKeyAsync(tx, actor.UserId, operation, key, ct);
    }
    public async Task<BsonDocument> PreviewAsync(ActorContext actor, ProcurementDisbursementInput input, CancellationToken ct = default) {
        input = ProcurementDisbursementRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = ContextFor(actor, authority, tx, input.Location, Permissions.DisbursementRecord); await CaptureAsync(context, input, token);
            var result = BusinessView(input, context.Source!, context.Capacity!.View()); result.Add("binding", B.Binding(actor.UserId, actor.GrantVersion, input, context.Source!, context.Capacity.View())); result.Add("observedAt", Now.UtcDateTime);
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> CreateDraftAsync(ActorContext actor, ProcurementDisbursementInput input, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = ProcurementDisbursementRules.Copy(input); B.RequireBinding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Drafts, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value; var body = B.DraftBody(input, binding); Context? context = null;
        return await authorization.ExecuteCommandAsync(actor, B.DraftOperation, metadata, body, async (authority, tx, token) => {
            var saved = await ExistingAsync(actor, authority, tx, B.DraftOperation, metadata.IdempotencyKey, token);
            if (saved is not null) context = await DraftContextAsync(actor, authority, tx, saved["draftId"].AsInt64, false, token);
            else { context = ContextFor(actor, authority, tx, input.Location, Permissions.DisbursementRecord); await CaptureAsync(context, input, token); ProcurementDisbursementRecords.Equal(binding, B.Binding(actor.UserId, actor.GrantVersion, input, context.Source!, context.Capacity!.View())); }
            return Command(context);
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); Current(active); var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "draftId", id }, { "version", 1L },
                { "location", R.Location(input.Location) }, { "authorizationId", input.AuthorizationId }, { "input", B.Input(input) }, { "source", active.Source!.DeepClone() }, { "capacity", active.Capacity!.View() },
                { "binding", binding }, { "people", new BsonArray(active.People!.Evidence.OrderBy(person => person.UserId).Select(ProcurementFundingBson.Person)) }, { "fence", 0L }, { "recordedDisbursementId", BsonNull.Value } };
            R.Metadata(row, B.DraftOperation, audit, actor, metadata, body, Now); row["snapshotHash"] = B.DraftHash(row);
            await Rows(B.Drafts).InsertOneAsync(tx, row, cancellationToken: token); await EffectsAsync(active, row, token); return B.Result(row);
        }, ct, async (_, tx, token) => {
            var row = await records.ForKeyAsync(tx, actor.UserId, B.DraftOperation, metadata.IdempotencyKey, token, context!.Proof) ?? throw B.Broken();
            ProcurementDisbursementRecords.Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason)); context!.Draft = row; await FenceAsync(context, token);
        }, validateCurrent: () => Current(context ?? throw B.Broken()));
    }
    public async Task<BsonDocument> PreviewRecordAsync(ActorContext actor, long draftId, long expectedRevision, long reservationId, EvidenceRef reference, CancellationToken ct = default) {
        if (draftId <= 0 || expectedRevision != 1 || reservationId <= 0 || reference is null) throw ProcurementDisbursementRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await DraftContextAsync(actor, authority, tx, draftId, true, token); var draft = context.Draft!; var capacity = context.Capacity!.View();
            var result = new BsonDocument { { "draft", await DraftViewAsync(tx, draft, token) }, { "capacity", capacity },
                { "binding", B.RecordBinding(actor.UserId, actor.GrantVersion, draft["snapshotHash"].AsString, reservationId, reference, capacity) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    internal async Task<BsonDocument> RecordWithProofAsync(ActorContext actor, long draftId, long expectedRevision, long reservationId, EvidenceRef reference, string binding,
        AuthorizationCommandMetadata metadata, Func<IClientSessionHandle, long, IProcurementDisbursementEvidenceUploadTarget, CancellationToken, Task<Action>> pin, CancellationToken ct) {
        if (draftId <= 0 || expectedRevision != 1 || reservationId <= 0 || reference is null) throw ProcurementDisbursementRules.Invalid(); B.RequireBinding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Records, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value; var pinId = (await ids.NextAsync("evidencePins", ct)).Value; var allocationId = (await ids.NextAsync(B.Allocations, ct)).Value;
        var body = B.RecordBody(draftId, expectedRevision, reservationId, reference, binding); Context? context = null; BsonDocument? fresh = null; Action? finalPin = null;
        return await authorization.ExecuteCommandAsync(actor, B.RecordOperation, metadata, body, async (authority, tx, token) => {
            fresh = null; finalPin = null; var saved = await ExistingAsync(actor, authority, tx, B.RecordOperation, metadata.IdempotencyKey, token);
            context = await DraftContextAsync(actor, authority, tx, draftId, saved is null, token);
            if (saved is null) ProcurementDisbursementRecords.Equal(binding, B.RecordBinding(actor.UserId, actor.GrantVersion, context.Draft!["snapshotHash"].AsString, reservationId, reference, context.Capacity!.View()));
            return Command(context);
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); Current(active); var draft = active.Draft!;
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "draftId", draftId }, { "version", 1L }, { "draftHash", draft["snapshotHash"] },
                { "location", draft["location"] }, { "authorizationId", draft["authorizationId"] }, { "source", draft["source"] }, { "input", draft["input"] }, { "capacity", active.Capacity!.View() },
                { "binding", binding }, { "people", new BsonArray(active.People!.Evidence.OrderBy(person => person.UserId).Select(ProcurementFundingBson.Person)) },
                { "state", "recorded_unverified" }, { "reservationId", reservationId }, { "evidence", PaymentSubmissionBson.Reference(reference) }, { "pinId", pinId }, { "allocationId", allocationId } };
            R.Metadata(row, B.RecordOperation, audit, actor, metadata, body, Now); await Rows(B.Records).InsertOneAsync(tx, row, cancellationToken: token); await EffectsAsync(active, row, token);
            finalPin = await pin(tx, id, new RecordingEvidenceTarget(this, active, id), token);
            if ((await Rows(B.Drafts).UpdateOneAsync(tx, draft, Builders<BsonDocument>.Update.Set("recordedDisbursementId", id), cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
            draft["recordedDisbursementId"] = id; fresh = row; return B.Result(row);
        }, ct, async (_, tx, token) => {
            if (fresh is not null) await ConsumeAsync(context!, fresh, token);
            var row = await records.ForKeyAsync(tx, actor.UserId, B.RecordOperation, metadata.IdempotencyKey, token, context!.Proof) ?? throw B.Broken(); ProcurementDisbursementRecords.Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason));
            context.Capacity = await CapacityAsync(tx, row["source"]["funding"].AsBsonDocument, token, context.Proof,
                verifiedRecord: new(tx, row, context.Capacity!)); await FenceAsync(context, token); finalPin?.Invoke();
        }, validateCurrent: () => { Current(context ?? throw B.Broken()); finalPin?.Invoke(); });
    }
    internal async Task<BsonDocument?> RecoverRecordBodyAsync(ActorContext actor, long draftId, string key, string bodyHash, CancellationToken ct) {
        await StorageAsync(ct); return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var row = await ExistingAsync(actor, authority, tx, B.RecordOperation, key, token); if (row is null) { await FenceQueryAsync(actor, authority, tx, Permissions.DisbursementRecord, token); return null; }
            if (row["draftId"] != draftId || row["bodyHash"] != bodyHash) throw new UnitToko.Platform.Mongo.PersistenceConflictException("idempotency_key_reused", "Idempotency key was used with a different request");
            var context = await DraftContextAsync(actor, authority, tx, draftId, false, token); await FenceAsync(context, token); return B.Result(row);
        }, ct);
    }
}
