using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementRequestService
{
    private async Task CaptureCatalogAsync(Context context, ProcurementRequestInput input, CancellationToken ct) =>
        context.Catalog = await catalog.CaptureProcurementAsync(database, context.Actor, context.Authority, context.Tx, context.Key, context.Permission, input.Lines, ct);
    public async Task<BsonDocument> PreviewAsync(ActorContext actor, ProcurementRequestInput input, CancellationToken ct = default) {
        input = ProcurementRequestRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, input.Location, Permissions.ProcurementPrepare, token);
            await PeopleAsync(context, null, null, token); await CaptureCatalogAsync(context, input, token);
            var result = new BsonDocument { { "input", B.Input(input) }, { "binding", B.Binding(actor.UserId, actor.GrantVersion, input, context.Sources, context.Catalog!.Sources) },
                { "observedAt", Now.UtcDateTime }, { "source", B.SourceView(context.Sources) }, { "lines", B.Lines(input, context.Catalog.Sources) } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    private async Task EffectsAsync(Context context, BsonDocument row, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(context.Tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(context.Tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
            { "sourceKey", B.SourceKey(row) }, { "kind", row["operation"] }, { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "location", row["location"] },
            { "payload", B.Result(row) }, { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
    private Task<BsonDocument> CommandAsync(ActorContext actor, string operation, AuthorizationCommandMetadata metadata, object body,
        Func<AuthorizationSnapshot, IClientSessionHandle, BsonDocument?, CancellationToken, Task<Context>> capture,
        Func<Context, CancellationToken, Task<BsonDocument>> write, CancellationToken ct) {
        Context? selected = null;
        return authorization.ExecuteCommandAsync(actor, operation, metadata, body, async (authority, tx, token) => {
            var permission = B.Permission(operation); _ = Query(actor, authority, permission);
            var saved = await ForKeyAsync(actor, authority, tx, operation, metadata.IdempotencyKey, token);
            selected = await capture(authority, tx, saved, token); var query = Require(selected);
            return new(new(true, "allowed", query.Clauses.Where(clause => clause.Matches(selected.Resource)).Select(clause => clause.GrantId).Distinct().ToImmutableArray()),
                ScopePolicy.RequiredNodes(selected.Resource, authority.Directory), permission,
                new(selected.Resource, permission == Permissions.ProcurementApprove ? new ApprovalRequest(1, 1, RequestedBy: selected.Proposal!["actorId"].AsInt64) : null));
        }, (_, token) => write(selected ?? throw B.Broken(), token), ct, async (_, tx, token) => {
            var context = selected ?? throw B.Broken(); var row = await records.ForKeyAsync(tx, actor.UserId, operation, metadata.IdempotencyKey, token) ?? throw B.Broken();
            Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason));
            var request = await records.RequestAsync(tx, row["requestId"].AsInt64, token);
            BindPeople(context, request, operation == B.Prefix + "submit" ? null : row);
            await PeopleAsync(context, operation == B.Prefix + "submit" ? null : actor.UserId, operation == B.Prefix + "submit" ? null : request["actorId"].AsInt64, token);
            await FenceAsync(context, token);
        }, validateCurrent: () => Current(selected ?? throw B.Broken()));
    }
    public async Task<BsonDocument> SubmitAsync(ActorContext actor, ProcurementRequestInput input, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = ProcurementRequestRules.Copy(input); B.RequireBinding(binding); Metadata(metadata);
        if (metadata.Reason != input.Reason) throw ProcurementRequestRules.Invalid(); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Requests, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value; var body = B.SubmitBody(input, binding);
        return await CommandAsync(actor, B.Prefix + "submit", metadata, body, async (authority, tx, saved, token) => {
            var context = await ContextAsync(actor, authority, tx, input.Location, Permissions.ProcurementPrepare, token);
            if (saved is null) { await CaptureCatalogAsync(context, input, token); Equal(binding, B.Binding(actor.UserId, actor.GrantVersion, input, context.Sources, context.Catalog!.Sources)); }
            else BindPeople(context, saved);
            await PeopleAsync(context, null, null, token); return context;
        }, async (context, token) => {
            Equal(binding, B.Binding(actor.UserId, actor.GrantVersion, input, context.Sources, context.Catalog!.Sources)); Current(context); var now = Now;
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "requestId", id }, { "version", 1L }, { "location", B.Location(input.Location) },
                { "sources", context.Sources.DeepClone() }, { "catalog", context.Catalog.Sources.DeepClone() }, { "input", B.Input(input) }, { "binding", binding },
                { "people", new BsonArray(context.People!.Evidence.OrderBy(p => p.UserId).Select(B.Person)) } };
            B.Metadata(row, B.Prefix + "submit", auditId, actor, metadata, body, now);
            await Rows(B.Requests).InsertOneAsync(context.Tx, row, cancellationToken: token); await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
    private async Task<Context> RequestContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long requestId, string permission, CancellationToken ct) {
        _ = Query(actor, authority, permission);
        var route = await Rows(B.Requests).Find(tx, new BsonDocument("_id", requestId)).Project(new BsonDocument("location", 1)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        var context = await ContextAsync(actor, authority, tx, B.Location(route["location"]), permission, ct);
        context.Proposal = await records.RequestAsync(tx, requestId, ct); BindPeople(context, context.Proposal); return context;
    }
    private async Task PendingAsync(Context context, ProcurementRequestDecision decision, CancellationToken ct) {
        if (context.Proposal is null || await records.DecisionForAsync(context.Tx, context.Proposal, ct) is not null) throw B.Changed();
        if (decision == ProcurementRequestDecision.Approve) {
            Equal(context.Proposal["sources"], context.Sources);
            if (context.Catalog is null) await CaptureCatalogAsync(context, B.Input(context.Proposal["input"]), ct);
            Equal(context.Proposal["catalog"], context.Catalog!.Sources);
        }
    }
    private static BsonArray ReviewCatalog(Context context) => context.Catalog?.Sources ?? [];
    public async Task<BsonDocument> PreviewDecisionAsync(ActorContext actor, long requestId, ProcurementRequestDecision decision, CancellationToken ct = default) {
        if (requestId <= 0 || !Enum.IsDefined(decision)) throw ProcurementRequestRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await RequestContextAsync(actor, authority, tx, requestId, Permissions.ProcurementApprove, token); var request = context.Proposal!;
            await PendingAsync(context, decision, token); await PeopleAsync(context, actor.UserId, request["actorId"].AsInt64, token);
            var result = new BsonDocument { { "request", await RequestViewAsync(context, request, token) }, { "decision", B.Decision(decision) },
                { "binding", B.DecisionBinding(actor.UserId, actor.GrantVersion, request, decision, context.Sources, ReviewCatalog(context)) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> DecideAsync(ActorContext actor, long requestId, long expectedVersion, ProcurementRequestDecision decision, string binding,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (requestId <= 0 || expectedVersion != 1 || !Enum.IsDefined(decision)) throw ProcurementRequestRules.Invalid();
        B.RequireBinding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Decisions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var operation = B.Prefix + B.Decision(decision); var body = B.DecideBody(requestId, expectedVersion, decision, binding);
        return await CommandAsync(actor, operation, metadata, body, async (authority, tx, saved, token) => {
            var context = await RequestContextAsync(actor, authority, tx, requestId, Permissions.ProcurementApprove, token);
            if (saved is null) { await PendingAsync(context, decision, token); Equal(binding, B.DecisionBinding(actor.UserId, actor.GrantVersion, context.Proposal!, decision, context.Sources, ReviewCatalog(context))); }
            else BindPeople(context, context.Proposal!, saved);
            await PeopleAsync(context, actor.UserId, context.Proposal!["actorId"].AsInt64, token); return context;
        }, async (context, token) => {
            await PendingAsync(context, decision, token); var request = context.Proposal!;
            Equal(binding, B.DecisionBinding(actor.UserId, actor.GrantVersion, request, decision, context.Sources, ReviewCatalog(context)));
            Current(context); var now = Now; if (now.UtcDateTime < request["recordedAt"].ToUniversalTime()) throw B.Changed();
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "requestId", requestId }, { "version", 1L },
                { "location", request["location"].DeepClone() }, { "sources", request["sources"].DeepClone() }, { "requestHash", request["snapshotHash"] },
                { "reviewSources", context.Sources.DeepClone() }, { "reviewCatalog", ReviewCatalog(context).DeepClone() }, { "decision", B.Decision(decision) },
                { "binding", binding }, { "people", new BsonArray(context.People!.Evidence.OrderBy(p => p.UserId).Select(B.Person)) } };
            B.Metadata(row, operation, auditId, actor, metadata, body, now);
            await Rows(B.Decisions).InsertOneAsync(context.Tx, row, cancellationToken: token); await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
}
