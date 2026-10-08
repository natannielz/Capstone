using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using A = UnitToko.Platform.Procurement.PurchaseArrivalRecords;
using B = UnitToko.Platform.Procurement.PurchaseOrderBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class PurchaseOrderService
{
    private async Task<BsonDocument> WriteArrivalAsync(ArrivalContext context, string action, PurchaseArrivalInput input,
        long? proposalId, string binding, AuthorizationCommandMetadata metadata, object body, CancellationToken ct)
    {
        var owner = context.Owner; Current(owner);
        var id = (await ids.NextAsync(A.Events, ct)).Value;
        var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "headId", context.Head["_id"] },
            { "location", context.Head["location"].DeepClone() }, { "previousRevision", context.Head["revision"] },
            { "revision", checked(context.Head["revision"].AsInt64 + 1) }, { "action", action }, { "proposalId", proposalId ?? id },
            { "input", A.Input(input) }, { "orderHash", context.Source["order"]["snapshotHash"] }, { "binding", binding },
            { "actorId", owner.Actor.UserId }, { "actorGrantVersion", owner.Actor.GrantVersion },
            { "actorPerson", B.Person(owner.People!.Evidence.Single(person => person.UserId == owner.Actor.UserId)) },
            { "recordedAt", Now.UtcDateTime }, { "requestKey", metadata.IdempotencyKey },
            { "bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason) }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId } };
        row.Add("eventHash", A.Hash(row));
        await Rows(A.Events).InsertOneAsync(owner.Tx, row, cancellationToken: ct);
        await Rows(A.Audits).InsertOneAsync(owner.Tx, A.Audit(row), cancellationToken: ct);
        var next = context.Head.DeepClone().AsBsonDocument; next["revision"] = row["revision"]; next["eventId"] = id;
        if (context.Exists) {
            if ((await Rows(A.Heads).ReplaceOneAsync(owner.Tx, context.Head, next, cancellationToken: ct)).ModifiedCount != 1) throw A.Changed();
        } else await Rows(A.Heads).InsertOneAsync(owner.Tx, next, cancellationToken: ct);
        Current(owner); return A.Result(row);
    }
    private async Task<BsonDocument?> ArrivalForKeyAsync(ActorContext actor, IClientSessionHandle tx, string action, string key, CancellationToken ct)
    {
        var row = await Rows(A.Events).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "action", action }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is null) foreach (var table in new[] { A.Audits, "idempotencyRecords" })
            if (await Rows(table).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "operation", A.Prefix + action },
                { table == "idempotencyRecords" ? "key" : "requestKey", key } }).AnyAsync(ct)) throw A.Broken();
        return row;
    }
    public async Task<BsonDocument> ProposeArrivalAsync(ActorContext actor, PurchaseArrivalInput input, string binding,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        input = PurchaseArrivalRules.Copy(input); Metadata(metadata); B.RequireBinding(binding); await ArrivalStorageAsync(ct);
        ArrivalContext? selected = null; var body = new { input, binding };
        return await authorization.ExecuteCommandAsync(actor, A.Prefix + "propose", metadata, body, async (authority, tx, token) => {
            selected = await ArrivalContextAsync(actor, authority, tx, input.OrderId, input.SkuId, Permissions.ProcurementPrepare, token);
            var previous = await ArrivalForKeyAsync(actor, tx, "propose", metadata.IdempotencyKey, token);
            if (previous is null) { RequireArrivalInput(selected, input); if (binding != ArrivalBinding(selected, input)) throw A.Changed(); }
            else if (previous["input"] != A.Input(input) || !selected.Events.Any(row => row["_id"] == previous["_id"])) throw A.Broken();
            var query = Require(selected.Owner);
            return new(new(true, "allowed", [.. query.Clauses.Where(clause => clause.Matches(selected.Owner.Resource)).Select(clause => clause.GrantId).Distinct()]),
                ScopePolicy.RequiredNodes(selected.Owner.Resource, authority.Directory), Permissions.ProcurementPrepare, new(selected.Owner.Resource));
        }, (_, token) => WriteArrivalAsync(selected!, "propose", input, null, binding, metadata, body, token), ct,
            complete: async (authority, tx, token) => {
                selected = await ArrivalContextAsync(actor, authority, tx, input.OrderId, input.SkuId, Permissions.ProcurementPrepare, token);
                await FenceArrivalAsync(selected, token);
            }, validateCurrent: () => Current(selected!.Owner));
    }
    private async Task<(ArrivalContext Context, BsonDocument Proposal)> ArrivalProposalContextAsync(ActorContext actor,
        AuthorizationSnapshot authority, IClientSessionHandle tx, long proposalId, string permission, CancellationToken ct)
    {
        if (proposalId <= 0) throw PurchaseArrivalRules.Invalid(); _ = Query(actor, authority, permission);
        var route = await Rows(A.Events).Find(tx, new BsonDocument { { "_id", proposalId }, { "action", "propose" } }).SingleOrDefaultAsync(ct)
            ?? throw new AuthorizationDeniedException("resource_unavailable");
        var input = A.Input(route["input"]);
        var context = await ArrivalContextAsync(actor, authority, tx, input.OrderId, input.SkuId, permission, ct);
        var proposal = context.Events.SingleOrDefault(row => row["_id"] == proposalId) ?? throw A.Broken();
        await ArrivalPeopleAsync(context, proposal, ct); return (context, proposal);
    }
    public async Task<BsonDocument> PreviewArrivalDecisionAsync(ActorContext actor, long proposalId, string decision, CancellationToken ct = default)
    {
        if (decision is not ("approve" or "reject")) throw PurchaseArrivalRules.Invalid(); await ArrivalStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var (context, proposal) = await ArrivalProposalContextAsync(actor, authority, tx, proposalId, Permissions.ProcurementApprove, token);
            if (context.Latest?["_id"] != proposalId || context.Latest?["action"] != "propose") throw A.Changed();
            var body = new { proposalId, expectedRevision = context.Head["revision"].AsInt64, decision };
            var result = ArrivalView(context); result.Add("input", proposal["input"].DeepClone());
            result.Add("decision", decision); result.Add("binding", ArrivalBinding(context, body));
            await FenceArrivalAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> DecideArrivalAsync(ActorContext actor, long proposalId, long expectedRevision, string decision,
        string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        if (expectedRevision <= 1 || decision is not ("approve" or "reject")) throw PurchaseArrivalRules.Invalid();
        Metadata(metadata); B.RequireBinding(binding); await ArrivalStorageAsync(ct);
        ArrivalContext? selected = null; BsonDocument? proposal = null;
        var previewBody = new { proposalId, expectedRevision, decision }; var body = new { proposalId, expectedRevision, decision, binding };
        return await authorization.ExecuteCommandAsync(actor, A.Prefix + decision, metadata, body, async (authority, tx, token) => {
            (selected, proposal) = await ArrivalProposalContextAsync(actor, authority, tx, proposalId, Permissions.ProcurementApprove, token);
            var previous = await ArrivalForKeyAsync(actor, tx, decision, metadata.IdempotencyKey, token);
            if (previous is null && (selected.Head["revision"] != expectedRevision || selected.Latest?["_id"] != proposalId ||
                binding != ArrivalBinding(selected, previewBody))) throw A.Changed();
            if (previous is not null && !selected.Events.Any(row => row["_id"] == previous["_id"])) throw A.Broken();
            var query = Require(selected.Owner);
            return new(new(true, "allowed", [.. query.Clauses.Where(clause => clause.Matches(selected.Owner.Resource)).Select(clause => clause.GrantId).Distinct()]),
                ScopePolicy.RequiredNodes(selected.Owner.Resource, authority.Directory), Permissions.ProcurementApprove,
                new(selected.Owner.Resource, new ApprovalRequest(1, 1, RequestedBy: proposal["actorId"].AsInt64)));
        }, (_, token) => WriteArrivalAsync(selected!, decision, A.Input(proposal!["input"]), proposalId, binding, metadata, body, token), ct,
            complete: async (authority, tx, token) => {
                (selected, proposal) = await ArrivalProposalContextAsync(actor, authority, tx, proposalId, Permissions.ProcurementApprove, token);
                await FenceArrivalAsync(selected, token);
            }, validateCurrent: () => { Current(selected!.Owner); selected.ReviewPeople!.RequireCurrent(selected.Owner.Tx, clock.UtcNow); });
    }
    public async Task<BsonDocument?> RecoverArrivalAsync(ActorContext actor, string action, string key, CancellationToken ct = default)
    {
        if (action is not ("propose" or "approve" or "reject")) throw PurchaseArrivalRules.Invalid();
        Metadata(new(key, "Read original arrival operation", "arrival-recovery")); await ArrivalStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            _ = Query(actor, authority, Permissions.ProcurementRead);
            var row = await ArrivalForKeyAsync(actor, tx, action, key, token); if (row is null) return null;
            var input = A.Input(row["input"]);
            var context = await ArrivalContextAsync(actor, authority, tx, input.OrderId, input.SkuId, Permissions.ProcurementRead, token);
            if (!context.Events.Any(value => value == row)) throw A.Broken();
            var result = A.Result(row); await FenceArrivalAsync(context, token); return result;
        }, ct);
    }
}
