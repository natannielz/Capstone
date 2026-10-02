using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using B = UnitToko.Platform.Procurement.ProcurementFundingBson;
using P = UnitToko.Platform.Procurement.PurchaseOrderBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementFundingService
{
    private async Task FenceQueryAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string permission, CancellationToken ct) {
        var query = Query(actor, authority, permission); var clauses = query.Clauses;
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        var deadline = await LiveSessionAsync(actor, tx, ct); _ = Query(actor, authority, permission);
        if (deadline is { } expires && clock.UtcNow >= expires) throw new AuthorizationDeniedException("stale_or_inactive_session"); ct.ThrowIfCancellationRequested();
    }
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, CancellationToken ct = default) {
        await StorageAsync(ct); return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var query = Query(actor, authority, Permissions.FundingRead); var candidates = new HashSet<ProcurementLocation>();
            foreach (var clause in query.Clauses) {
                var scope = clause.Scope;
                if (scope.Kind == GrantScopeKind.FulfillmentWarehouse) candidates.Add(new(scope.OrganizationId!.Value, scope.ShopId!.Value, scope.WarehouseId!.Value, scope.ChannelId!.Value));
                if (scope.Kind == GrantScopeKind.FulfillmentShop) foreach (var node in authority.Directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Warehouse && node.ParentId == scope.ShopId && node.Status == AuthorizationStatus.Active))
                    candidates.Add(new(scope.OrganizationId!.Value, scope.ShopId!.Value, node.Id, scope.ChannelId!.Value));
            }
            var locations = candidates.Where(location => ScopePolicy.IsResourceActive(ResourceFor(location), authority.Directory) && query.Matches(ResourceFor(location)))
                .OrderBy(location => location.OrganizationId).ThenBy(location => location.ShopId).ThenBy(location => location.WarehouseId).ThenBy(location => location.ChannelId).ToArray();
            if (locations.Length > 256) throw B.Changed(); var contexts = new List<Context>(); var items = new BsonArray();
            foreach (var location in locations) {
                var context = await ContextAsync(actor, authority, tx, location, Permissions.FundingRead, token); await PeopleAsync(context, null, null, token);
                var item = new BsonDocument("location", R.Location(location)); foreach (var name in R.SourceNames) item.Add(name, R.Label(context.Sources[name])); items.Add(item); contexts.Add(context);
            }
            foreach (var context in contexts) await FenceAsync(context, token); await FenceQueryAsync(actor, authority, tx, Permissions.FundingRead, token);
            foreach (var context in contexts) Current(context); token.ThrowIfCancellationRequested(); return new BsonDocument("items", items);
        }, ct);
    }
    public async Task<BsonDocument> OrdersAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw ProcurementFundingRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, location, Permissions.FundingRead, token); await PeopleAsync(context, null, null, token);
            var policies = await Rows(B.Policies).Find(tx, new BsonDocument("location", R.Location(location))).Sort(new BsonDocument("_id", 1)).Limit(257).ToListAsync(token);
            if (policies.Count > 256) throw B.Changed(); var policyViews = new BsonArray();
            foreach (var raw in policies) { var policy = await records.RowAsync(tx, B.Policies, raw["_id"].AsInt64, token); policyViews.Add(B.PolicyView(policy)); await TouchAsync(context, B.Policies, policy, token); }
            var filter = new BsonDocument("location", R.Location(location)); if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(P.Orders).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray(); var captures = new List<PurchaseOrderFundingCapture>();
            var states = new List<FundingCapacityState>();
            foreach (var row in rows.Take(25)) {
                var source = await orders.FundingSourceAsync(database, tx, row["_id"].AsInt64, row["version"].AsInt64, row["approvalId"].AsInt64, token);
                if (Money.Parse(source["proposal"]["input"]["paymentTerms"]["requiredAdvance"].AsString).Value == 0) continue;
                var capture = await orders.CaptureFundingAsync(database, actor, authority, tx, row["_id"].AsInt64, row["version"].AsInt64, row["approvalId"].AsInt64, Permissions.FundingRead, token);
                captures.Add(capture); var capacity = await CapacityAsync(tx, source, token); states.Add(capacity);
                // Fence before capturing the next order: several orders can share the same supplier head and policy.
                await orders.FenceFundingAsync(database, actor, authority, tx, capture, token);
                if (Money.Parse(capacity.View()["remaining"].AsString).Value == 0) continue;
                items.Add(new BsonDocument { { "order", PurchaseOrderService.FundingOrderView(source) }, { "capacity", capacity.View() } });
            }
            foreach (var state in states) if (state.Head is { } head) await TouchAsync(context, B.Heads, head, token);
            await FenceAsync(context, token);
            foreach (var capture in captures) capture.RequireCurrent(); Current(context); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "location", R.Location(location) }, { "source", R.SourceView(context.Sources) }, { "items", items }, { "policies", policyViews }, { "nextOrderId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    private static BsonDocument BusinessView(ProcurementFundingInput input, BsonDocument sources, BsonDocument order, BsonDocument policy, BsonDocument capacity) => new() {
        { "input", B.Input(input) }, { "source", R.SourceView(sources) }, { "order", PurchaseOrderService.FundingOrderView(order) }, { "policy", B.PolicyView(policy) }, { "capacity", capacity } };
    private async Task<BsonDocument> RequestViewAsync(Context context, BsonDocument request, CancellationToken ct) {
        var result = BusinessView(B.Input(request["input"]), request["sources"].AsBsonDocument, request["orderSource"].AsBsonDocument, request["policy"].AsBsonDocument, request["capacity"].AsBsonDocument);
        var decision = await records.DecisionForAsync(context.Tx, request, ct); result.AddRange(new BsonDocument {
            { "id", request["_id"] }, { "version", 1L }, { "reason", request["reason"] }, { "requestedAt", request["recordedAt"] }, { "state", decision is null ? "requested" : decision["decision"] == "approve" ? "approved" : "rejected" },
            { "decision", decision is null ? BsonNull.Value : new BsonDocument { { "id", decision["_id"] }, { "decision", decision["decision"] }, { "reason", decision["reason"] }, { "decidedAt", decision["recordedAt"] },
                { "authorizationId", decision["authorizationId"] }, { "authorizedAmount", decision["authorizedAmount"] } } } }); return result;
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, long requestId, CancellationToken ct = default) {
        if (requestId <= 0) throw ProcurementFundingRules.Invalid(); await StorageAsync(ct); return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await RequestContextAsync(actor, authority, tx, requestId, Permissions.FundingRead, token); var request = context.Proposal!; var decision = await records.DecisionForAsync(tx, request, token);
            BindPeople(context, request, decision); await PeopleAsync(context, decision?["actorId"].AsInt64, decision is null ? null : request["actorId"].AsInt64, token);
            context.Capacity = await CapacityAsync(tx, request["orderSource"].AsBsonDocument, token); var result = await RequestViewAsync(context, request, token); await FenceAsync(context, token); return result;
        }, ct);
    }
    private async Task HistoryAsync(Context context, CancellationToken ct) {
        var filter = new BsonDocument("location", R.Location(context.Key)); long requests = 0, decisions = 0; var seenOrders = new HashSet<long>();
        using var cursor = await Rows(B.Requests).Find(context.Tx, filter).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var raw in cursor.Current) {
            var request = await records.RequestAsync(context.Tx, raw["_id"].AsInt64, ct); requests++; if (await records.DecisionForAsync(context.Tx, request, ct) is not null) decisions++;
            if (seenOrders.Add(request["orderId"].AsInt64)) _ = await CapacityAsync(context.Tx, request["orderSource"].AsBsonDocument, ct);
        }
        if (await Rows(B.Decisions).CountDocumentsAsync(context.Tx, filter, cancellationToken: ct) != decisions) throw B.Broken();
        foreach (var table in new[] { "auditEvents", "outboxEvents" }) if (await Rows(table).CountDocumentsAsync(context.Tx, new BsonDocument { { "location", R.Location(context.Key) }, { "kind", new BsonRegularExpression(@"^procurement\.funding\.") } }, cancellationToken: ct) != requests + decisions) throw B.Broken();
    }
    public async Task<BsonDocument> ListAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw ProcurementFundingRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, location, Permissions.FundingRead, token);
            // ponytail: Retained scoped history is reconstructed to detect missing effects and capacity. An indexed reconciliation proof requires measured release-load evidence.
            await HistoryAsync(context, token); var filter = new BsonDocument("location", R.Location(location)); if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(B.Requests).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray(); var proofs = new List<PersonAuthorityProof>();
            foreach (var row in rows.Take(25)) {
                var request = await records.RequestAsync(tx, row["_id"].AsInt64, token); var decision = await records.DecisionForAsync(tx, request, token); BindPeople(context, request, decision);
                await PeopleAsync(context, decision?["actorId"].AsInt64, decision is null ? null : request["actorId"].AsInt64, token); proofs.Add(context.People!); items.Add(await RequestViewAsync(context, request, token));
            }
            if (rows.Count == 0) await PeopleAsync(context, null, null, token); await FenceAsync(context, token);
            foreach (var proof in proofs) await new PersonAuthorizationFence(database, clock).TouchAsync(tx, proof, token); Current(context); foreach (var proof in proofs) proof.RequireCurrent(tx, clock.UtcNow); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "location", R.Location(location) }, { "items", items }, { "nextRequestId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument?> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        var permission = B.Permission(operation); Metadata(new(key, "Recover original funding authorization operation", "procurement-funding-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            _ = Query(actor, authority, permission); var row = await ForKeyAsync(actor, authority, tx, operation, key, token);
            if (row is null) { await FenceQueryAsync(actor, authority, tx, permission, token); return null; }
            var context = await RequestContextAsync(actor, authority, tx, row["requestId"].AsInt64, permission, token); var request = context.Proposal!; BindPeople(context, request, operation == B.Prefix + "request" ? null : row);
            await PeopleAsync(context, operation == B.Prefix + "request" ? null : actor.UserId, operation == B.Prefix + "request" ? null : request["actorId"].AsInt64, token);
            context.Capacity = await CapacityAsync(tx, request["orderSource"].AsBsonDocument, token); await FenceAsync(context, token); return B.Result(row);
        }, ct);
    }
}
