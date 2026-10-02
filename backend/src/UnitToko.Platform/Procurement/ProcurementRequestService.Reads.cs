using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using B = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementRequestService
{
    private async Task FenceQueryAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string permission, CancellationToken ct) {
        var query = Query(actor, authority, permission); var clauses = query.Clauses;
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        var deadline = await LiveSessionAsync(actor, tx, ct); _ = Query(actor, authority, permission);
        if (deadline is { } expires && clock.UtcNow >= expires) throw new AuthorizationDeniedException("stale_or_inactive_session"); ct.ThrowIfCancellationRequested();
    }
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var query = Query(actor, authority, Permissions.ProcurementRead); var candidates = new HashSet<ProcurementLocation>();
            foreach (var clause in query.Clauses) {
                var scope = clause.Scope;
                if (scope.Kind == GrantScopeKind.FulfillmentWarehouse) candidates.Add(new(scope.OrganizationId!.Value, scope.ShopId!.Value, scope.WarehouseId!.Value, scope.ChannelId!.Value));
                if (scope.Kind == GrantScopeKind.FulfillmentShop)
                    foreach (var node in authority.Directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Warehouse && node.ParentId == scope.ShopId && node.Status == AuthorizationStatus.Active))
                        candidates.Add(new(scope.OrganizationId!.Value, scope.ShopId!.Value, node.Id, scope.ChannelId!.Value));
            }
            var locations = candidates.Where(location => ScopePolicy.IsResourceActive(ResourceFor(location), authority.Directory) && query.Matches(ResourceFor(location)))
                .OrderBy(location => location.OrganizationId).ThenBy(location => location.ShopId).ThenBy(location => location.WarehouseId).ThenBy(location => location.ChannelId).ToArray();
            if (locations.Length > 256) throw B.Changed(); var contexts = new List<Context>(); var items = new BsonArray();
            foreach (var location in locations) {
                var context = await ContextAsync(actor, authority, tx, location, Permissions.ProcurementRead, token); await PeopleAsync(context, null, null, token);
                var item = new BsonDocument("location", B.Location(location));
                foreach (var name in B.SourceNames) item.Add(name, B.Label(context.Sources[name])); items.Add(item); contexts.Add(context);
            }
            foreach (var context in contexts) await FenceAsync(context, token);
            await FenceQueryAsync(actor, authority, tx, Permissions.ProcurementRead, token); foreach (var context in contexts) Current(context);
            token.ThrowIfCancellationRequested(); return new BsonDocument("items", items);
        }, ct);
    }
    public async Task<BsonDocument> PackagesAsync(ActorContext actor, ProcurementLocation location, int offset, int limit, string? search, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); AuthorizationStore.ValidatePage(offset, limit); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, location, Permissions.ProcurementRead, token); await PeopleAsync(context, null, null, token);
            var page = await catalog.ProcurementPackagesAsync(database, actor, authority, tx, location, offset, limit, search, token); context.Catalog = page.Capture;
            var result = new BsonDocument { { "items", new BsonArray(page.Capture.Sources.Select(B.Package)) }, { "offset", offset }, { "hasMore", page.HasMore } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    private async Task<BsonDocument> RequestViewAsync(Context context, BsonDocument request, CancellationToken ct) {
        var decision = await records.DecisionForAsync(context.Tx, request, ct); BsonValue view = BsonNull.Value;
        if (decision is not null) view = new BsonDocument { { "id", decision["_id"] }, { "decision", decision["decision"] }, { "reason", decision["reason"] }, { "decidedAt", decision["recordedAt"] } };
        return new BsonDocument { { "id", request["_id"] }, { "version", 1L }, { "input", request["input"].DeepClone() }, { "source", B.SourceView(request["sources"].AsBsonDocument) },
            { "lines", B.Lines(B.Input(request["input"]), request["catalog"].AsBsonArray) }, { "submittedAt", request["recordedAt"] },
            { "state", decision is null ? "submitted" : decision["decision"] == "approve" ? "approved" : "rejected" }, { "decision", view },
            { "downstream", new BsonDocument { { "supplier", "unresolved" }, { "commercial", "unpriced" }, { "payment", "not_recorded" }, { "receipt", "not_recorded" } } } };
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, long requestId, CancellationToken ct = default) {
        if (requestId <= 0) throw ProcurementRequestRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await RequestContextAsync(actor, authority, tx, requestId, Permissions.ProcurementRead, token); var request = context.Proposal!;
            var decision = await records.DecisionForAsync(tx, request, token); BindPeople(context, request, decision);
            await PeopleAsync(context, decision?["actorId"].AsInt64, decision is null ? null : request["actorId"].AsInt64, token);
            var result = await RequestViewAsync(context, request, token); await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> ListAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw ProcurementRequestRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, location, Permissions.ProcurementRead, token);
            // Full integrity reconstruction is O(location history). If measured load requires it, introduce an indexed reconciliation proof; never omit orphan/effect checks to page faster.
            await records.HistoryAsync(tx, location, token); var filter = new BsonDocument("location", B.Location(location)); if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(B.Requests).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = new BsonArray(); var proofs = new List<PersonAuthorityProof>();
            foreach (var row in rows.Take(25)) {
                var request = await records.RequestAsync(tx, row["_id"].AsInt64, token); var decision = await records.DecisionForAsync(tx, request, token);
                BindPeople(context, request, decision); await PeopleAsync(context, decision?["actorId"].AsInt64, decision is null ? null : request["actorId"].AsInt64, token);
                proofs.Add(context.People!); items.Add(await RequestViewAsync(context, request, token));
            }
            if (rows.Count == 0) await PeopleAsync(context, null, null, token);
            await FenceAsync(context, token); foreach (var proof in proofs) await new PersonAuthorizationFence(database, clock).TouchAsync(tx, proof, token);
            Current(context); foreach (var proof in proofs) proof.RequireCurrent(tx, clock.UtcNow); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "location", B.Location(location) }, { "items", items }, { "nextRequestId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument?> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        var permission = B.Permission(operation); Metadata(new(key, "Recover original procurement request operation", "procurement-request-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            _ = Query(actor, authority, permission); var row = await ForKeyAsync(actor, authority, tx, operation, key, token);
            if (row is null) { await FenceQueryAsync(actor, authority, tx, permission, token); return null; }
            var context = await ContextAsync(actor, authority, tx, B.Location(row["location"]), permission, token);
            var request = await records.RequestAsync(tx, row["requestId"].AsInt64, token); BindPeople(context, request, operation == B.Prefix + "submit" ? null : row);
            await PeopleAsync(context, operation == B.Prefix + "submit" ? null : actor.UserId, operation == B.Prefix + "submit" ? null : request["actorId"].AsInt64, token);
            await FenceAsync(context, token); return B.Result(row);
        }, ct);
    }
}
