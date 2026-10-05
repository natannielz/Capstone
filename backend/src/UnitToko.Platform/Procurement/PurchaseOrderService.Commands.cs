using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Procurement.PurchaseOrderBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class PurchaseOrderService
{
    private static BsonArray Capacity(Context context) => context.Request is null ? [] : new(context.Request.Capacity.Where(row => context.ActiveInput!.Lines.Any(line => line.RequestSkuId == row["requestSkuId"].AsInt64)));
    private async Task CaptureAsync(Context context, PurchaseOrderInput input, CancellationToken ct) {
        context.ActiveInput = input; Current(context);
        context.Request = await requests.CapturePurchaseOrderAsync(database, context.Actor, context.Authority, context.Tx, input.RequestId, input.RequestVersion, input.RequestDecisionId, context.Permission, input.Lines, ct);
        Equal(context.Request.Request["location"], R.Location(input.Location)); Equal(context.Request.Sources, context.Sources);
        if (R.Input(context.Request.Request["input"]).Route == ProcurementRoute.OmiRegular && input.ExternalOrderReference is null) throw PurchaseOrderRules.Invalid();
        if (input.CommercialReference.IssuedDate > AccountingCalendar.BusinessDate(Now)) throw B.Changed();
        context.Supplier = await suppliers.CapturePurchaseOrderAsync(database, context.Actor, context.Authority, context.Tx, input, context.Permission, ct);
        var original = R.Input(context.Request.Request["input"]);
        for (var index = 0; index < input.Lines.Length; index++) {
            var position = Array.FindIndex(original.Lines.ToArray(), line => line.SkuId == input.Lines[index].RequestSkuId);
            if (position < 0) throw B.Changed(); Equal(context.Supplier.Snapshot["mappings"][index]["source"]["catalog"], context.Request.Request["catalog"][position]);
        }
        Current(context);
    }
    private static string Binding(Context context, PurchaseOrderInput input) => PurchaseOrderInboundBindingBson.Binding(context.Actor.UserId, context.Actor.GrantVersion, input,
        context.Request!.Request, context.Request.Decision, context.Sources, context.Supplier!.Snapshot, Capacity(context), context.InboundSelection, ReceivingSources(context));
    private async Task EffectsAsync(Context context, BsonDocument row, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(context.Tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(context.Tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "sourceKey", B.SourceKey(row) },
            { "kind", row["operation"] }, { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "location", row["location"] }, { "requestId", row["requestId"] },
            { "payload", B.Result(row) }, { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
    private async Task RequireAllocationsAsync(Context context, BsonDocument proposal, CancellationToken ct) {
        var source = await records.RequestAsync(context.Tx, B.Input(proposal["input"]), ct);
        await requests.RequirePurchaseOrderAllocationsAsync(database, context.Tx, source.Request, source.Decision, ct);
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
                new(selected.Resource, permission == Permissions.ProcurementApprove ? new ApprovalRequest(1, 1, RequestedBy: selected.Proposal!["actorId"].AsInt64) : null));
        }, (_, token) => write(selected ?? throw B.Broken(), token), ct, async (_, tx, token) => {
            var context = selected ?? throw B.Broken(); var row = await records.ForKeyAsync(tx, actor.UserId, operation, metadata.IdempotencyKey, token) ?? throw B.Broken();
            Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason)); var proposal = await records.ProposalAsync(tx, row["proposalId"].AsInt64, token);
            if (context.Allocate) {
                await requests.AllocatePurchaseOrderAsync(database, actor, context.Authority, tx, context.Request!, row["orderId"].AsInt64, row["_id"].AsInt64,
                    row["snapshotHash"].AsString, new(row["recordedAt"].ToUniversalTime()), token); context.Allocate = false;
            }
            await RequireAllocationsAsync(context, proposal, token); BindPeople(context, proposal, operation == B.Prefix + "propose" ? null : row);
            await PeopleAsync(context, operation == B.Prefix + "propose" ? null : actor.UserId, operation == B.Prefix + "propose" ? null : proposal["actorId"].AsInt64, token);
            await FenceAsync(context, token);
        }, validateCurrent: () => Current(selected ?? throw B.Broken()));
    }
    public Task<BsonDocument> PreviewAsync(ActorContext actor, PurchaseOrderInput input, CancellationToken ct = default) => PreviewInboundCoreAsync(actor, input, null, ct);
    public Task<BsonDocument> PreviewInboundAsync(ActorContext actor, PurchaseOrderInput input, PurchaseOrderInboundPoolSelection selection, CancellationToken ct = default) =>
        PreviewInboundCoreAsync(actor, input, PurchaseOrderInboundRules.Copy(input, selection), ct);
    private async Task<BsonDocument> PreviewInboundCoreAsync(ActorContext actor, PurchaseOrderInput input, PurchaseOrderInboundPoolSelection? selection, CancellationToken ct) {
        input = PurchaseOrderRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, input.Location, Permissions.ProcurementPrepare, token); await PeopleAsync(context, null, null, token); await CaptureAsync(context, input, token); await CaptureInboundAsync(context, input, selection, token);
            var result = BusinessView(input, context.Request!.Request, context.Request.Decision, context.Sources, context.Supplier!.Snapshot, Capacity(context));
            if (selection is not null) result.Add("inboundPools", new BsonDocument { { "allocations", PurchaseOrderInboundBindingBson.Selection(input, selection) }, { "receivingPools", new BsonArray(ReceivingSources(context).Select(value => UnitToko.Platform.Inventory.StockSourceSetupService.HeldReceiptPoolView(value.AsBsonDocument))) } });
            result.Add("binding", Binding(context, input)); result.Add("observedAt", Now.UtcDateTime); await FenceAsync(context, token); return result;
        }, ct);
    }
    public Task<BsonDocument> ProposeAsync(ActorContext actor, PurchaseOrderInput input, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) =>
        ProposeInboundCoreAsync(actor, input, null, binding, metadata, ct);
    public Task<BsonDocument> ProposeInboundAsync(ActorContext actor, PurchaseOrderInput input, PurchaseOrderInboundPoolSelection selection, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) =>
        ProposeInboundCoreAsync(actor, input, PurchaseOrderInboundRules.Copy(input, selection), binding, metadata, ct);
    private async Task<BsonDocument> ProposeInboundCoreAsync(ActorContext actor, PurchaseOrderInput input, PurchaseOrderInboundPoolSelection? selection, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct) {
        input = PurchaseOrderRules.Copy(input); B.RequireBinding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Proposals, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value; var body = PurchaseOrderInboundBindingBson.Body(input, binding, selection);
        return await CommandAsync(actor, B.Prefix + "propose", metadata, body, async (authority, tx, saved, token) => {
            if (saved is not null) { var original = await ProposalContextAsync(actor, authority, tx, saved["proposalId"].AsInt64, Permissions.ProcurementPrepare, token); await PeopleAsync(original, null, null, token); return original; }
            var context = await ContextAsync(actor, authority, tx, input.Location, Permissions.ProcurementPrepare, token); await PeopleAsync(context, null, null, token); await CaptureAsync(context, input, token); await CaptureInboundAsync(context, input, selection, token);
            Equal(binding, Binding(context, input)); return context;
        }, async (context, token) => {
            Current(context); var now = Now; var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "proposalId", id }, { "version", 1L }, { "requestId", input.RequestId },
                { "location", R.Location(input.Location) }, { "input", B.Input(input) }, { "requestHash", context.Request!.Request["snapshotHash"] }, { "requestDecisionHash", context.Request.Decision["snapshotHash"] },
                { "sources", context.Sources.DeepClone() }, { "supplier", context.Supplier!.Snapshot.DeepClone() }, { "capacity", Capacity(context).DeepClone() }, { "binding", binding },
                { "people", new BsonArray(context.People!.Evidence.OrderBy(person => person.UserId).Select(B.Person)) } };
            B.Metadata(row, B.Prefix + "propose", auditId, actor, metadata, body, now); await Rows(B.Proposals).InsertOneAsync(context.Tx, row, cancellationToken: token);
            await WriteInboundSelectionAsync(context, row, token); await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
    private async Task<Context> ProposalContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long proposalId, string permission, CancellationToken ct) {
        _ = Query(actor, authority, permission); var route = await Rows(B.Proposals).Find(tx, new BsonDocument("_id", proposalId)).Project(new BsonDocument("location", 1)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        var context = await ContextAsync(actor, authority, tx, R.Location(route["location"]), permission, ct); context.Proposal = await records.ProposalAsync(tx, proposalId, ct);
        context.InboundRecord = await InboundRecords.SelectionAsync(tx, context.Proposal, ct); BindPeople(context, context.Proposal); return context;
    }
    private async Task PrepareDecisionAsync(Context context, PurchaseOrderDecision decision, CancellationToken ct) {
        if (await records.DecisionForAsync(context.Tx, context.Proposal!, ct) is not null) throw B.Changed();
        if (decision == PurchaseOrderDecision.Approve) {
            await CaptureAsync(context, B.Input(context.Proposal!["input"]), ct); await CaptureInboundAsync(context, B.Input(context.Proposal["input"]), context.InboundRecord is null ? null : PurchaseOrderInboundBindingBson.Selection(B.Input(context.Proposal["input"]), context.InboundRecord["allocations"]), ct); Equal(context.Sources, context.Proposal["sources"]); Equal(context.Supplier!.Snapshot, context.Proposal["supplier"]);
            if (context.InboundRecord is not null) Equal(PurchaseOrderInboundBindingBson.StableSources(ReceivingSources(context)), PurchaseOrderInboundBindingBson.StableSources(context.InboundRecord["receivingSources"].AsBsonArray));
        } else await RequireAllocationsAsync(context, context.Proposal!, ct);
        await PeopleAsync(context, context.Actor.UserId, context.Proposal!["actorId"].AsInt64, ct);
    }
    private static string DecisionBinding(Context context, PurchaseOrderDecision decision) => PurchaseOrderInboundBindingBson.DecisionBinding(context.Actor.UserId, context.Actor.GrantVersion, context.Proposal!, decision,
        context.Sources, context.Supplier?.Snapshot, Capacity(context), context.InboundRecord, ReceivingSources(context));
    public async Task<BsonDocument> PreviewDecisionAsync(ActorContext actor, long proposalId, PurchaseOrderDecision decision, CancellationToken ct = default) {
        if (proposalId <= 0 || !Enum.IsDefined(decision)) throw PurchaseOrderRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, Permissions.ProcurementApprove, token); await PrepareDecisionAsync(context, decision, token);
            var result = new BsonDocument { { "proposal", await ProposalViewAsync(context, context.Proposal!, token) }, { "decision", B.Decision(decision) }, { "capacity", B.CapacityView(Capacity(context)) },
                { "binding", DecisionBinding(context, decision) }, { "observedAt", Now.UtcDateTime } }; await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> DecideAsync(ActorContext actor, long proposalId, long expectedVersion, PurchaseOrderDecision decision, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (proposalId <= 0 || expectedVersion != 1 || !Enum.IsDefined(decision)) throw PurchaseOrderRules.Invalid(); B.RequireBinding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Decisions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value; var orderId = decision == PurchaseOrderDecision.Approve ? (await ids.NextAsync(B.Orders, ct)).Value : (long?)null;
        var operation = B.Prefix + B.Decision(decision); var body = B.DecideBody(proposalId, expectedVersion, decision, binding);
        return await CommandAsync(actor, operation, metadata, body, async (authority, tx, saved, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, Permissions.ProcurementApprove, token);
            if (saved is null) { await PrepareDecisionAsync(context, decision, token); Equal(binding, DecisionBinding(context, decision)); context.Allocate = decision == PurchaseOrderDecision.Approve; }
            else { BindPeople(context, context.Proposal!, saved); await PeopleAsync(context, actor.UserId, context.Proposal!["actorId"].AsInt64, token); }
            return context;
        }, async (context, token) => {
            Current(context); var now = Now; var proposal = context.Proposal!; if (now.UtcDateTime < proposal["recordedAt"].ToUniversalTime()) throw B.Changed();
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "proposalId", proposalId }, { "version", 1L }, { "requestId", proposal["requestId"] }, { "location", proposal["location"] },
                { "proposalHash", proposal["snapshotHash"] }, { "decision", B.Decision(decision) }, { "orderId", orderId is { } next ? new BsonInt64(next) : BsonNull.Value },
                { "orderVersion", orderId.HasValue ? new BsonInt64(1) : BsonNull.Value }, { "reviewSources", context.Sources.DeepClone() },
                { "reviewSupplier", context.Supplier is null ? BsonNull.Value : context.Supplier.Snapshot.DeepClone() }, { "reviewCapacity", Capacity(context).DeepClone() }, { "binding", binding },
                { "people", new BsonArray(context.People!.Evidence.OrderBy(person => person.UserId).Select(B.Person)) } };
            B.Metadata(row, operation, auditId, actor, metadata, body, now);
            if (orderId is { } actual) {
                var order = new BsonDocument { { "_id", actual }, { "schemaVersion", 1 }, { "version", 1L }, { "proposalId", proposalId }, { "proposalHash", proposal["snapshotHash"] },
                    { "approvalId", id }, { "approvalHash", row["snapshotHash"] }, { "location", proposal["location"] }, { "requestId", proposal["requestId"] },
                    { "requestDecisionId", proposal["input"]["requestDecisionId"] }, { "recordedAt", now.UtcDateTime } }; order.Add("snapshotHash", B.Hash(order));
                await Rows(B.Orders).InsertOneAsync(context.Tx, order, cancellationToken: token);
            }
            await Rows(B.Decisions).InsertOneAsync(context.Tx, row, cancellationToken: token); await WriteInboundDecisionAsync(context, row, token); await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
}
