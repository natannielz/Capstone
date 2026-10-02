using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using B = UnitToko.Platform.Procurement.SupplierBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class SupplierService
{
    private async Task QueryFenceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string permission, CancellationToken ct) {
        var clauses = Query(actor, authority, permission).Clauses.Where(clause => permission == Permissions.ProcurementRead || clause.Scope.Kind == GrantScopeKind.FulfillmentShop).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        var deadline = await LiveSessionAsync(actor, tx, ct); _ = Query(actor, authority, permission);
        if (deadline is { } until && clock.UtcNow >= until) throw new AuthorizationDeniedException("stale_or_inactive_session"); ct.ThrowIfCancellationRequested();
    }
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, SupplierAccess access, CancellationToken ct = default) {
        var permission = B.Permission(access); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var owners = Query(actor, authority, permission).Clauses.Where(clause => clause.Scope.Kind == GrantScopeKind.FulfillmentShop)
                .Select(clause => new SupplierOwner(clause.Scope.OrganizationId!.Value, clause.Scope.ShopId!.Value, clause.Scope.ChannelId!.Value)).Distinct()
                .OrderBy(owner => owner.OrganizationId).ThenBy(owner => owner.ShopId).ThenBy(owner => owner.ChannelId).ToArray();
            if (owners.Length > 256) throw B.Changed(); var items = new BsonArray(); var contexts = new List<Context>();
            foreach (var owner in owners) {
                var context = await ContextAsync(actor, authority, tx, owner, permission, token); await PeopleAsync(context, false, null, token);
                var item = new BsonDocument("owner", B.Owner(owner)); item.AddRange(B.SourceView(context.Sources)); items.Add(item); contexts.Add(context);
            }
            foreach (var context in contexts) await FenceAsync(context, token); await QueryFenceAsync(actor, authority, tx, permission, token);
            foreach (var context in contexts) Current(context); token.ThrowIfCancellationRequested(); return new BsonDocument("items", items);
        }, ct);
    }
    public async Task<BsonDocument> LocationsAsync(ActorContext actor, SupplierAccess access, SupplierOwner owner, CancellationToken ct = default) {
        SupplierRules.Owner(owner); var permission = B.Permission(access); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, owner, permission, token); await PeopleAsync(context, false, null, token);
            var warehouses = authority.Directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Warehouse && node.ParentId == owner.ShopId && node.Status == AuthorizationStatus.Active).OrderBy(node => node.Id).ToArray();
            if (warehouses.Length > 256) throw B.Changed(); var items = new BsonArray(); var sources = new List<BsonDocument>();
            foreach (var warehouse in warehouses) {
                var source = await SourceAsync(authority, tx, ScopeNodeKind.Warehouse, warehouse.Id, token); sources.Add(source);
                var item = new BsonDocument("location", ProcurementRequestBson.Location(new ProcurementLocation(owner.OrganizationId, owner.ShopId, warehouse.Id, owner.ChannelId)));
                item.AddRange(B.SourceView(context.Sources)); item.Add("warehouse", ProcurementRequestBson.Label(source)); items.Add(item);
            }
            foreach (var source in sources) await organizations.FenceAddressProfileAsync(tx, source, true, token);
            await FenceAsync(context, token); return new BsonDocument("items", items);
        }, ct);
    }
    public async Task<BsonDocument> PoliciesAsync(ActorContext actor, SupplierAccess access, SupplierOwner owner, CancellationToken ct = default) {
        SupplierRules.Owner(owner); var permission = B.Permission(access); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, owner, permission, token); await PeopleAsync(context, false, null, token);
            var rows = await Rows(B.Policies).Find(tx, new BsonDocument("scope", B.Owner(owner))).Sort(new BsonDocument("_id", 1)).Limit(257).ToListAsync(token);
            if (rows.Count > 256) throw B.Changed(); var items = new BsonArray();
            foreach (var raw in rows) { var row = await records.RowAsync(tx, B.Policies, raw["_id"].AsInt64, token); items.Add(B.PolicyView(row)); await TouchAsync(context, B.Policies, row, token); }
            await FenceAsync(context, token); return new BsonDocument("items", items);
        }, ct);
    }
    public async Task<BsonDocument> PackagesAsync(ActorContext actor, SupplierAccess access, ProcurementLocation location, int offset, int limit, string? search, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); AuthorizationStore.ValidatePage(offset, limit); var permission = B.Permission(access); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, new(location.OrganizationId, location.ShopId, location.ChannelId), permission, token);
            await PeopleAsync(context, false, null, token); var warehouse = await SourceAsync(authority, tx, ScopeNodeKind.Warehouse, location.WarehouseId, token);
            if (warehouse["target"]["parentId"] != location.ShopId) throw new AuthorizationDeniedException("scope_denied");
            var page = await catalog.ProcurementPackagesAsync(database, actor, authority, tx, location, offset, limit, search, token, permission); context.Catalog.Add((location, page.Capture));
            var result = new BsonDocument { { "items", new BsonArray(page.Capture.Sources.Select(ProcurementRequestBson.Package)) }, { "offset", offset }, { "hasMore", page.HasMore } };
            await organizations.FenceAddressProfileAsync(tx, warehouse, true, token); await FenceAsync(context, token); return result;
        }, ct);
    }
    private async Task<BsonDocument> ProposalViewAsync(Context context, BsonDocument proposal, CancellationToken ct) {
        var observed = await records.ObservationForAsync(context.Tx, proposal, ct); var decision = await records.DecisionForAsync(context.Tx, proposal, ct);
        BsonValue observationView = observed is null ? BsonNull.Value : new BsonDocument { { "id", observed["_id"] }, { "observed", observed["observed"] },
            { "sourceReference", observed["sourceReference"] }, { "trustedChannelReference", observed["trustedChannelReference"] }, { "reason", observed["reason"] }, { "checkedAt", observed["checkedAt"] } };
        BsonValue decisionView = decision is null ? BsonNull.Value : new BsonDocument { { "id", decision["_id"] }, { "decision", decision["decision"] }, { "reason", decision["reason"] },
            { "decidedAt", decision["recordedAt"] }, { "versionId", decision["versionId"] }, { "version", decision["supplierVersion"] } };
        return new BsonDocument { { "id", proposal["_id"] }, { "supplierId", proposal["supplierId"] }, { "version", 1L }, { "input", proposal["input"] }, { "source", B.SourceView(proposal["sources"].AsBsonDocument) },
            { "mappingSources", B.MappingView(proposal["mappingSources"].AsBsonArray) }, { "policy", B.PolicyView(proposal["policy"].AsBsonDocument) }, { "reason", proposal["reason"] },
            { "proposedAt", proposal["recordedAt"] }, { "state", decision is null ? "pending" : decision["decision"] == "approve" ? "approved" : "rejected" }, { "observation", observationView }, { "decision", decisionView } };
    }
    private async Task HistoricalPeopleAsync(Context context, BsonDocument proposal, CancellationToken ct) {
        var observation = await records.ObservationForAsync(context.Tx, proposal, ct); var decision = await records.DecisionForAsync(context.Tx, proposal, ct);
        BindPeople(context, proposal, observation, decision); await PeopleAsync(context, false, null, ct);
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, SupplierAccess access, long proposalId, CancellationToken ct = default) {
        if (proposalId <= 0) throw SupplierRules.Invalid(); var permission = B.Permission(access); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, permission, token); await HistoricalPeopleAsync(context, context.Proposal!, token);
            var result = await ProposalViewAsync(context, context.Proposal!, token); await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> CurrentAsync(ActorContext actor, SupplierAccess access, long supplierId, CancellationToken ct = default) {
        if (supplierId <= 0) throw SupplierRules.Invalid(); var permission = B.Permission(access); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            _ = Query(actor, authority, permission);
            var route = await Rows(B.Heads).Find(tx, new BsonDocument("_id", supplierId)).Project(new BsonDocument("owner", 1)).SingleOrDefaultAsync(token) ?? throw new AuthorizationDeniedException("resource_unavailable");
            var context = await ContextAsync(actor, authority, tx, B.Owner(route["owner"]), permission, token); context.Head = await records.HeadAsync(tx, supplierId, token); BsonValue proposal = BsonNull.Value;
            if (!context.Head["versionId"].IsBsonNull) {
                var version = await records.RowAsync(tx, B.Versions, context.Head["versionId"].AsInt64, token); context.Proposal = await records.ProposalAsync(tx, version["proposalId"].AsInt64, token);
                await HistoricalPeopleAsync(context, context.Proposal, token); proposal = await ProposalViewAsync(context, context.Proposal, token);
            } else await PeopleAsync(context, false, null, token);
            var result = new BsonDocument { { "supplierId", supplierId }, { "versionId", context.Head["versionId"] }, { "version", context.Head["version"] }, { "proposal", proposal } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    private async Task HistoryAsync(Context context, CancellationToken ct) {
        var filter = new BsonDocument("owner", B.Owner(context.Owner)); long expected = 0, observations = 0, decisions = 0;
        using var cursor = await Rows(B.Proposals).Find(context.Tx, filter).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var raw in cursor.Current) {
            var proposal = await records.ProposalAsync(context.Tx, raw["_id"].AsInt64, ct); expected++;
            if (await records.ObservationForAsync(context.Tx, proposal, ct) is not null) observations++;
            if (await records.DecisionForAsync(context.Tx, proposal, ct) is not null) decisions++;
        }
        if (await Rows(B.Observations).CountDocumentsAsync(context.Tx, filter, cancellationToken: ct) != observations ||
            await Rows(B.Decisions).CountDocumentsAsync(context.Tx, filter, cancellationToken: ct) != decisions) throw B.Broken();
        foreach (var table in new[] { "auditEvents", "outboxEvents" })
            if (await Rows(table).CountDocumentsAsync(context.Tx, new BsonDocument { { "owner", B.Owner(context.Owner) }, { "kind", new BsonRegularExpression(@"^procurement\.supplier\.") } }, cancellationToken: ct) != expected + observations + decisions) throw B.Broken();
        using var heads = await Rows(B.Heads).Find(context.Tx, filter).ToCursorAsync(ct);
        while (await heads.MoveNextAsync(ct)) foreach (var head in heads.Current) _ = await records.HeadAsync(context.Tx, head["_id"].AsInt64, ct);
    }
    public async Task<BsonDocument> ListAsync(ActorContext actor, SupplierAccess access, SupplierOwner owner, long? beforeId = null, CancellationToken ct = default) {
        SupplierRules.Owner(owner); if (beforeId is <= 0) throw SupplierRules.Invalid(); var permission = B.Permission(access); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, owner, permission, token);
            // ponytail: O(owner history) reconstruction preserves orphan detection. An indexed reconciliation proof can replace this only after measured load warrants it.
            await HistoryAsync(context, token); var filter = new BsonDocument("owner", B.Owner(owner)); if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(B.Proposals).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray(); var proofs = new List<PersonAuthorityProof>();
            foreach (var row in rows.Take(25)) {
                var proposal = await records.ProposalAsync(tx, row["_id"].AsInt64, token); await HistoricalPeopleAsync(context, proposal, token); proofs.Add(context.People!);
                items.Add(await ProposalViewAsync(context, proposal, token));
            }
            if (rows.Count == 0) await PeopleAsync(context, false, null, token); await FenceAsync(context, token);
            foreach (var proof in proofs) await new PersonAuthorizationFence(database, clock).TouchAsync(tx, proof, token);
            Current(context); foreach (var proof in proofs) proof.RequireCurrent(tx, clock.UtcNow); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "owner", B.Owner(owner) }, { "items", items }, { "nextProposalId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument?> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        var permission = B.Permission(operation); Metadata(new(key, "Recover original supplier operation", "supplier-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            _ = Query(actor, authority, permission); var row = await ForKeyAsync(actor, authority, tx, operation, key, token);
            if (row is null) { await QueryFenceAsync(actor, authority, tx, permission, token); return null; }
            var context = await ProposalContextAsync(actor, authority, tx, row["proposalId"].AsInt64, permission, token); BindPeople(context, row);
            await PeopleAsync(context, operation != B.Prefix + "propose", null, token); await FenceAsync(context, token); return B.Result(row);
        }, ct);
    }
    public async Task<BsonDocument> ChoicesAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw SupplierRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var owner = new SupplierOwner(location.OrganizationId, location.ShopId, location.ChannelId);
            var baseContext = await ContextAsync(actor, authority, tx, owner, Permissions.ProcurementRead, token, location); await PeopleAsync(baseContext, false, null, token);
            var warehouse = await SourceAsync(authority, tx, ScopeNodeKind.Warehouse, location.WarehouseId, token); if (warehouse["target"]["parentId"] != owner.ShopId) throw new AuthorizationDeniedException("scope_denied");
            var filter = new BsonDocument { { "owner", B.Owner(owner) }, { "version", new BsonDocument("$gt", 0L) } }; if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(B.Heads).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray(); var contexts = new List<Context>();
            var deadlines = new List<DateTimeOffset>();
            foreach (var row in rows.Take(25)) {
                var context = await ContextAsync(actor, authority, tx, owner, Permissions.ProcurementRead, token, location); context.Head = await records.HeadAsync(tx, row["_id"].AsInt64, token);
                var version = await records.RowAsync(tx, B.Versions, context.Head["versionId"].AsInt64, token); context.Proposal = await records.ProposalAsync(tx, version["proposalId"].AsInt64, token);
                await HistoricalPeopleAsync(context, context.Proposal, token); var input = B.Input(context.Proposal["input"]);
                var policy = await records.RowAsync(tx, B.Policies, input.PolicyId, token);
                // The latest version has no fallback if its trust policy changed, expired or was withdrawn.
                if (!B.PolicySnapshot(policy).Equals(context.Proposal["policy"]) || !policy["active"].AsBoolean || policy["approvedAt"].ToUniversalTime() > Now.UtcDateTime || policy["expiresAt"].ToUniversalTime() <= Now.UtcDateTime) {
                    await TouchAsync(context, B.Policies, policy, token); await FenceAsync(context, token); continue;
                }
                context.Policy = policy; context.Activating = true;
                var selected = input.Mappings.Where(mapping => mapping.Location == location && mapping.ValidFrom <= Now && Now < mapping.ValidUntil).ToImmutableArray();
                if (selected.Length == 0) { await FenceAsync(context, token); continue; }
                await CaptureMappingsAsync(context, input with { Mappings = selected }, token); var mappings = new BsonArray();
                Equal(context.Sources, context.Proposal["sources"]);
                for (var index = 0; index < selected.Length; index++)
                    Equal(context.MappingSources[index], context.Proposal["mappingSources"][input.Mappings.IndexOf(selected[index])]);
                for (var index = 0; index < selected.Length; index++) { mappings.Add(new BsonDocument { { "mapping", B.Mapping(selected[index]) }, { "package", ProcurementRequestBson.Package(context.MappingSources[index]["catalog"]) } }); deadlines.Add(selected[index].ValidUntil); }
                items.Add(new BsonDocument { { "supplierId", row["_id"] }, { "versionId", version["_id"] }, { "version", version["version"] },
                    { "subjectReference", input.Details.SubjectReference }, { "displayName", input.Details.DisplayName }, { "mappings", mappings } }); contexts.Add(context);
            }
            foreach (var context in contexts) await FenceAsync(context, token); await organizations.FenceAddressProfileAsync(tx, warehouse, true, token); await FenceAsync(baseContext, token);
            foreach (var context in contexts) Current(context); if (deadlines.Any(deadline => Now >= deadline)) throw B.Changed(); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "location", ProcurementRequestBson.Location(location) }, { "items", items }, { "nextSupplierId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
}
