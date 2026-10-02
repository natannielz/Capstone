using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Procurement.ProcurementFundingBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementFundingService
{
    private async Task CaptureAsync(Context context, ProcurementFundingInput input, CancellationToken ct) {
        context.Order = await orders.CaptureFundingAsync(database, context.Actor, context.Authority, context.Tx, input.OrderId, input.OrderVersion, input.OrderApprovalId, context.Permission, ct);
        Equal(context.Order.Source["order"]["location"], R.Location(input.Location)); context.Capacity = await CapacityAsync(context.Tx, context.Order.Source, ct);
        var policy = await records.RowAsync(context.Tx, B.Policies, input.PolicyId, ct); Equal(policy["revision"], input.PolicyRevision);
        if (context.Proposal is { } request) Equal(B.PolicySnapshot(policy), request["policy"]);
        ProcurementFundingRecords.PolicyAt(policy, context.Order.Source, Now); context.Policy = policy;
        ProcurementFundingRecords.CapacityInput(input, policy, context.Capacity.View()); Current(context);
    }
    private static string Binding(Context context, ProcurementFundingInput input) => B.Binding(context.Actor.UserId, context.Actor.GrantVersion, input,
        context.Order!.Source, context.Sources, context.Policy!, context.Capacity!.View());
    private async Task EffectsAsync(Context context, BsonDocument row, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(context.Tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(context.Tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "sourceKey", B.SourceKey(row) },
            { "kind", row["operation"] }, { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "location", row["location"] }, { "orderId", row["orderId"] },
            { "payload", B.Result(row) }, { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
    private Task<BsonDocument> CommandAsync(ActorContext actor, string operation, AuthorizationCommandMetadata metadata, object body,
        Func<AuthorizationSnapshot, IClientSessionHandle, BsonDocument?, CancellationToken, Task<Context>> capture,
        Func<Context, CancellationToken, Task<BsonDocument>> write, CancellationToken ct) {
        Context? selected = null;
        return authorization.ExecuteCommandAsync(actor, operation, metadata, body, async (authority, tx, token) => {
            var permission = B.Permission(operation); _ = Query(actor, authority, permission); var saved = await ForKeyAsync(actor, authority, tx, operation, metadata.IdempotencyKey, token);
            selected = await capture(authority, tx, saved, token); var query = Require(selected);
            return new(new(true, "allowed", query.Clauses.Where(clause => clause.Matches(selected.Resource)).Select(clause => clause.GrantId).Distinct().ToImmutableArray()),
                ScopePolicy.RequiredNodes(selected.Resource, authority.Directory), permission,
                new(selected.Resource, permission == Permissions.FundingApprove ? new ApprovalRequest(1, 1, RequestedBy: selected.Proposal!["actorId"].AsInt64) : null));
        }, (_, token) => write(selected ?? throw B.Broken(), token), ct, async (_, tx, token) => {
            var context = selected ?? throw B.Broken(); var row = await records.ForKeyAsync(tx, actor.UserId, operation, metadata.IdempotencyKey, token) ?? throw B.Broken();
            Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason)); var request = await records.RequestAsync(tx, row["requestId"].AsInt64, token);
            context.Capacity = await CapacityAsync(tx, request["orderSource"].AsBsonDocument, token); BindPeople(context, request, operation == B.Prefix + "request" ? null : row);
            await PeopleAsync(context, operation == B.Prefix + "request" ? null : actor.UserId, operation == B.Prefix + "request" ? null : request["actorId"].AsInt64, token);
            await FenceAsync(context, token);
        }, validateCurrent: () => Current(selected ?? throw B.Broken()));
    }
    public async Task<BsonDocument> PreviewAsync(ActorContext actor, ProcurementFundingInput input, CancellationToken ct = default) {
        input = ProcurementFundingRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, input.Location, Permissions.FundingRequest, token); await PeopleAsync(context, null, null, token); await CaptureAsync(context, input, token);
            var result = BusinessView(input, context.Sources, context.Order!.Source, context.Policy!, context.Capacity!.View());
            result.Add("binding", Binding(context, input)); result.Add("observedAt", Now.UtcDateTime); await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> RequestAsync(ActorContext actor, ProcurementFundingInput input, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = ProcurementFundingRules.Copy(input); B.RequireBinding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Requests, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value; var body = B.RequestBody(input, binding);
        return await CommandAsync(actor, B.Prefix + "request", metadata, body, async (authority, tx, saved, token) => {
            if (saved is not null) { var original = await RequestContextAsync(actor, authority, tx, saved["requestId"].AsInt64, Permissions.FundingRequest, token); await PeopleAsync(original, null, null, token); return original; }
            var context = await ContextAsync(actor, authority, tx, input.Location, Permissions.FundingRequest, token); await PeopleAsync(context, null, null, token); await CaptureAsync(context, input, token);
            Equal(binding, Binding(context, input)); return context;
        }, async (context, token) => {
            Current(context); var now = Now; var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "requestId", id }, { "version", 1L }, { "orderId", input.OrderId },
                { "location", R.Location(input.Location) }, { "input", B.Input(input) }, { "orderSource", context.Order!.Source.DeepClone() }, { "sources", context.Sources.DeepClone() },
                { "policy", B.PolicySnapshot(context.Policy!) }, { "capacity", context.Capacity!.View() }, { "binding", binding }, { "people", new BsonArray(context.People!.Evidence.OrderBy(person => person.UserId).Select(B.Person)) } };
            B.Metadata(row, B.Prefix + "request", auditId, actor, metadata, body, now); await Rows(B.Requests).InsertOneAsync(context.Tx, row, cancellationToken: token);
            await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
    private async Task<Context> RequestContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long requestId, string permission, CancellationToken ct) {
        _ = Query(actor, authority, permission); var route = await Rows(B.Requests).Find(tx, new BsonDocument("_id", requestId)).Project(new BsonDocument("location", 1)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        var context = await ContextAsync(actor, authority, tx, R.Location(route["location"]), permission, ct); context.Proposal = await records.RequestAsync(tx, requestId, ct); BindPeople(context, context.Proposal); return context;
    }
    private async Task PrepareDecisionAsync(Context context, ProcurementFundingDecision decision, CancellationToken ct) {
        if (await records.DecisionForAsync(context.Tx, context.Proposal!, ct) is not null) throw B.Changed();
        if (decision == ProcurementFundingDecision.Approve) {
            await CaptureAsync(context, B.Input(context.Proposal!["input"]), ct); Equal(context.Sources, context.Proposal["sources"]); Equal(context.Order!.Source, context.Proposal["orderSource"]);
        } else context.Capacity = await CapacityAsync(context.Tx, context.Proposal!["orderSource"].AsBsonDocument, ct);
        await PeopleAsync(context, context.Actor.UserId, context.Proposal!["actorId"].AsInt64, ct);
    }
    private static string DecisionBinding(Context context, ProcurementFundingDecision decision) => B.DecisionBinding(context.Actor.UserId, context.Actor.GrantVersion, context.Proposal!, decision,
        context.Sources, context.Policy, decision == ProcurementFundingDecision.Approve ? context.Capacity!.View() : null);
    public async Task<BsonDocument> PreviewDecisionAsync(ActorContext actor, long requestId, ProcurementFundingDecision decision, CancellationToken ct = default) {
        if (requestId <= 0 || !Enum.IsDefined(decision)) throw ProcurementFundingRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await RequestContextAsync(actor, authority, tx, requestId, Permissions.FundingApprove, token); await PrepareDecisionAsync(context, decision, token);
            var result = new BsonDocument { { "request", await RequestViewAsync(context, context.Proposal!, token) }, { "decision", B.Decision(decision) },
                { "capacity", decision == ProcurementFundingDecision.Approve ? context.Capacity!.View() : BsonNull.Value }, { "binding", DecisionBinding(context, decision) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> DecideAsync(ActorContext actor, long requestId, long expectedVersion, ProcurementFundingDecision decision, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (requestId <= 0 || expectedVersion != 1 || !Enum.IsDefined(decision)) throw ProcurementFundingRules.Invalid(); B.RequireBinding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Decisions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var allocationId = decision == ProcurementFundingDecision.Approve ? (await ids.NextAsync(B.Allocations, ct)).Value : (long?)null;
        var operation = B.Prefix + B.Decision(decision); var body = B.DecideBody(requestId, expectedVersion, decision, binding);
        return await CommandAsync(actor, operation, metadata, body, async (authority, tx, saved, token) => {
            var context = await RequestContextAsync(actor, authority, tx, requestId, Permissions.FundingApprove, token);
            if (saved is null) { await PrepareDecisionAsync(context, decision, token); Equal(binding, DecisionBinding(context, decision)); }
            else { BindPeople(context, context.Proposal!, saved); await PeopleAsync(context, actor.UserId, context.Proposal!["actorId"].AsInt64, token); }
            return context;
        }, async (context, token) => {
            Current(context); var request = context.Proposal!; var now = Now; if (now.UtcDateTime < request["recordedAt"].ToUniversalTime()) throw B.Changed();
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "requestId", requestId }, { "version", 1L }, { "orderId", request["orderId"] }, { "location", request["location"] },
                { "requestHash", request["snapshotHash"] }, { "decision", B.Decision(decision) }, { "authorizationId", allocationId is { } allocated ? new BsonInt64(allocated) : BsonNull.Value },
                { "authorizedAmount", allocationId.HasValue ? request["input"]["amount"] : BsonNull.Value }, { "reviewSources", context.Sources.DeepClone() },
                { "reviewPolicy", context.Policy is null ? BsonNull.Value : B.PolicySnapshot(context.Policy) }, { "reviewCapacity", allocationId.HasValue ? context.Capacity!.View() : BsonNull.Value },
                { "binding", binding }, { "people", new BsonArray(context.People!.Evidence.OrderBy(person => person.UserId).Select(B.Person)) } };
            B.Metadata(row, operation, auditId, actor, metadata, body, now); if (allocationId.HasValue) await ReserveAsync(context, row, token);
            await Rows(B.Decisions).InsertOneAsync(context.Tx, row, cancellationToken: token); await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
}
