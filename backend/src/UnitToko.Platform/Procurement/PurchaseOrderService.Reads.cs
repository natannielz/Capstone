using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using B = UnitToko.Platform.Procurement.PurchaseOrderBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class PurchaseOrderService
{
    private async Task FenceQueryAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string permission, CancellationToken ct) {
        var query = Query(actor, authority, permission); var clauses = query.Clauses;
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        var deadline = await LiveSessionAsync(actor, tx, ct); _ = Query(actor, authority, permission);
        if (deadline is { } expires && clock.UtcNow >= expires) throw new AuthorizationDeniedException("stale_or_inactive_session"); ct.ThrowIfCancellationRequested();
    }
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, CancellationToken ct = default) { await StorageAsync(ct); return await requests.TargetsAsync(actor, ct); }
    public async Task<BsonDocument> RequestsAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw PurchaseOrderRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, location, Permissions.ProcurementRead, token); await PeopleAsync(context, null, null, token);
            var original = new ProcurementRequestRecords(database, catalog); await original.HistoryAsync(tx, location, token);
            var filter = new BsonDocument("location", R.Location(location)); if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(R.Requests).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray(); var captures = new List<ProcurementPurchaseOrderCapture>();
            foreach (var raw in rows.Take(25)) {
                var request = await original.RequestAsync(tx, raw["_id"].AsInt64, token); var decision = await original.DecisionForAsync(tx, request, token);
                if (decision is null || decision["decision"] != "approve") continue;
                var capture = await requests.CapturePurchaseOrderAsync(database, actor, authority, tx, request["_id"].AsInt64, 1, decision["_id"].AsInt64, Permissions.ProcurementRead, [], token); captures.Add(capture);
                var input = R.Input(request["input"]); var lines = R.Lines(input, request["catalog"].AsBsonArray);
                for (var index = 0; index < lines.Count; index++) { var capacity = capture.Capacity.Single(row => row["requestSkuId"] == lines[index]["skuId"]);
                    lines[index].AsBsonDocument.Add("committedPackages", capacity["committedPackages"]); lines[index].AsBsonDocument.Add("availablePackages", capacity["availablePackages"]); }
                if (capture.Capacity.All(row => row["availablePackages"] == 0)) continue;
                items.Add(new BsonDocument { { "id", request["_id"] }, { "version", 1L }, { "decisionId", decision["_id"] }, { "route", R.Route(input.Route) }, { "kind", R.Kind(input.Kind) },
                    { "businessDate", B.Date(input.BusinessDate) }, { "externalReference", request["input"]["externalReference"] }, { "reason", input.Reason }, { "source", R.SourceView(capture.Sources) }, { "lines", lines } });
            }
            foreach (var capture in captures) await requests.FencePurchaseOrderAsync(database, actor, authority, tx, capture, token);
            await FenceAsync(context, token); foreach (var capture in captures) capture.RequireCurrent(); Current(context); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "location", R.Location(location) }, { "items", items }, { "nextRequestId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    private static BsonDocument BusinessView(PurchaseOrderInput input, BsonDocument request, BsonDocument decision, BsonDocument sources, BsonDocument supplier, BsonArray capacity) => new() {
        { "input", B.Input(input) }, { "route", request["input"]["route"] }, { "source", R.SourceView(sources) },
        { "request", new BsonDocument { { "id", request["_id"] }, { "version", 1L }, { "decisionId", decision["_id"] } } }, { "supplier", B.SupplierView(supplier) },
        { "lines", B.Lines(input, request) }, { "totals", B.Totals(input) }, { "capacity", B.CapacityView(capacity) } };
    private async Task<BsonDocument> ProposalViewAsync(Context context, BsonDocument proposal, CancellationToken ct) {
        var input = B.Input(proposal["input"]); var source = await records.RequestAsync(context.Tx, input, ct); var decision = await records.DecisionForAsync(context.Tx, proposal, ct);
        var result = BusinessView(input, source.Request, source.Decision, proposal["sources"].AsBsonDocument, proposal["supplier"].AsBsonDocument, proposal["capacity"].AsBsonArray);
        result.AddRange(new BsonDocument { { "id", proposal["_id"] }, { "version", 1L }, { "reason", proposal["reason"] }, { "proposedAt", proposal["recordedAt"] },
            { "state", decision is null ? "pending" : decision["decision"] == "approve" ? "approved" : "rejected" },
            { "decision", decision is null ? BsonNull.Value : new BsonDocument { { "id", decision["_id"] }, { "decision", decision["decision"] }, { "reason", decision["reason"] },
                { "decidedAt", decision["recordedAt"] }, { "orderId", decision["orderId"] }, { "orderVersion", decision["orderVersion"] } } } }); return result;
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, long proposalId, CancellationToken ct = default) {
        if (proposalId <= 0) throw PurchaseOrderRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, Permissions.ProcurementRead, token); var proposal = context.Proposal!; var decision = await records.DecisionForAsync(tx, proposal, token);
            BindPeople(context, proposal, decision); await PeopleAsync(context, decision?["actorId"].AsInt64, decision is null ? null : proposal["actorId"].AsInt64, token);
            await RequireAllocationsAsync(context, proposal, token); var result = await ProposalViewAsync(context, proposal, token); await FenceAsync(context, token); return result;
        }, ct);
    }
    private async Task HistoryAsync(Context context, CancellationToken ct) {
        var filter = new BsonDocument("location", R.Location(context.Key)); long proposals = 0, decisions = 0, orders = 0; var requestIds = new HashSet<long>();
        using var cursor = await Rows(B.Proposals).Find(context.Tx, filter).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var raw in cursor.Current) {
            var proposal = await records.ProposalAsync(context.Tx, raw["_id"].AsInt64, ct); proposals++; var decision = await records.DecisionForAsync(context.Tx, proposal, ct);
            if (decision is not null) { decisions++; if (decision["decision"] == "approve") orders++; }
            if (requestIds.Add(proposal["requestId"].AsInt64)) await RequireAllocationsAsync(context, proposal, ct);
        }
        if (await Rows(B.Decisions).CountDocumentsAsync(context.Tx, filter, cancellationToken: ct) != decisions || await Rows(B.Orders).CountDocumentsAsync(context.Tx, filter, cancellationToken: ct) != orders) throw B.Broken();
        foreach (var table in new[] { "auditEvents", "outboxEvents" })
            if (await Rows(table).CountDocumentsAsync(context.Tx, new BsonDocument { { "location", R.Location(context.Key) }, { "kind", new BsonRegularExpression(@"^procurement\.purchase\.order\.") } }, cancellationToken: ct) != proposals + decisions) throw B.Broken();
    }
    public async Task<BsonDocument> ListAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw PurchaseOrderRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, location, Permissions.ProcurementRead, token);
            // ponytail: Full retained history reconstruction preserves orphan/quantity integrity; replace only with a measured indexed reconciliation proof.
            await HistoryAsync(context, token); var filter = new BsonDocument("location", R.Location(location)); if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(B.Proposals).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray(); var proofs = new List<PersonAuthorityProof>();
            foreach (var row in rows.Take(25)) {
                var proposal = await records.ProposalAsync(tx, row["_id"].AsInt64, token); var decision = await records.DecisionForAsync(tx, proposal, token); BindPeople(context, proposal, decision);
                await PeopleAsync(context, decision?["actorId"].AsInt64, decision is null ? null : proposal["actorId"].AsInt64, token); proofs.Add(context.People!); items.Add(await ProposalViewAsync(context, proposal, token));
            }
            if (rows.Count == 0) await PeopleAsync(context, null, null, token); await FenceAsync(context, token);
            foreach (var proof in proofs) await new PersonAuthorizationFence(database, clock).TouchAsync(tx, proof, token);
            Current(context); foreach (var proof in proofs) proof.RequireCurrent(tx, clock.UtcNow); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "location", R.Location(location) }, { "items", items }, { "nextProposalId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument?> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        var permission = B.Permission(operation); Metadata(new(key, "Recover original purchase order operation", "purchase-order-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            _ = Query(actor, authority, permission); var row = await ForKeyAsync(actor, authority, tx, operation, key, token);
            if (row is null) { await FenceQueryAsync(actor, authority, tx, permission, token); return null; }
            var context = await ProposalContextAsync(actor, authority, tx, row["proposalId"].AsInt64, permission, token); var proposal = context.Proposal!;
            BindPeople(context, proposal, operation == B.Prefix + "propose" ? null : row);
            await PeopleAsync(context, operation == B.Prefix + "propose" ? null : actor.UserId, operation == B.Prefix + "propose" ? null : proposal["actorId"].AsInt64, token);
            await RequireAllocationsAsync(context, proposal, token); await FenceAsync(context, token); return B.Result(row);
        }, ct);
    }
}
