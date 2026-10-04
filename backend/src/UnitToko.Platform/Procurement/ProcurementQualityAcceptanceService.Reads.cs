using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Procurement.ProcurementQualityAcceptanceBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementQualityAcceptanceService
{
    private static BsonDocument HeldView(BsonDocument receipt, BsonDocument balance) => new() {
        { "balanceId", receipt["balanceId"] }, { "lotId", receipt["lotId"] }, { "poolId", receipt["poolId"] },
        { "onHand", balance["onHand"] }, { "reserved", balance["reserved"] }, { "held", balance["held"] },
        { "expired", balance["expired"] }, { "revision", balance["version"] } };
    private static BsonDocument PolicyView(BsonDocument row) => new() { { "id", row["_id"] }, { "revision", row["revision"] },
        { "active", row["active"] }, { "location", row["location"] },
        { "qualityProcedureReference", row["qualityProcedureReference"] }, { "putawayProcedureReference", row["putawayProcedureReference"] },
        { "allowNonExpiring", row["allowNonExpiring"] }, { "nonExpiringProcedureReference", row["nonExpiringProcedureReference"] },
        { "maximumObservationAgeMilliseconds", row["maximumObservationAgeMilliseconds"] }, { "approvalReference", row["approvalReference"] },
        { "approvedAt", row["approvedAt"] }, { "expiresAt", row["expiresAt"] } };
    private async Task FenceQueryAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        IEnumerable<string> permissions, CancellationToken ct) {
        var required = permissions.Distinct().ToArray(); var clauses = required.SelectMany(permission => Query(actor, authority, permission).Clauses).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        var expiry = await SessionAsync(actor, tx, ct); foreach (var permission in required) _ = Query(actor, authority, permission);
        if (expiry is { } until && clock.UtcNow >= until) throw new AuthorizationDeniedException("stale_or_inactive_session");
        ct.ThrowIfCancellationRequested();
    }
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var query = Query(actor, authority, Permissions.ProcurementQualityAcceptanceRead);
            var candidates = new HashSet<ProcurementLocation>();
            foreach (var clause in query.Clauses) {
                var scope = clause.Scope;
                if (scope.Kind == GrantScopeKind.FulfillmentWarehouse) candidates.Add(new(scope.OrganizationId!.Value, scope.ShopId!.Value, scope.WarehouseId!.Value, scope.ChannelId!.Value));
                if (scope.Kind == GrantScopeKind.FulfillmentShop)
                    foreach (var node in authority.Directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Warehouse && node.ParentId == scope.ShopId && node.Status == AuthorizationStatus.Active))
                        candidates.Add(new(scope.OrganizationId!.Value, scope.ShopId!.Value, node.Id, scope.ChannelId!.Value));
            }
            var locations = candidates.Where(location => ScopePolicy.IsResourceActive(Resource(location), authority.Directory) && query.Matches(Resource(location)))
                .OrderBy(location => location.OrganizationId).ThenBy(location => location.ShopId).ThenBy(location => location.WarehouseId).ThenBy(location => location.ChannelId).ToArray();
            if (locations.Length > 256) throw B.Changed("procurement_quality_acceptance_target_limit");
            var nodes = locations.SelectMany(location => ScopePolicy.RequiredNodes(Resource(location), authority.Directory)).Distinct().ToArray();
            var profiles = await Rows("organizationProfiles").Find(tx, new BsonDocument("_id", new BsonDocument("$in", new BsonArray(nodes.Select(node => new BsonInt64(node.Id)))))).ToListAsync(token);
            BsonDocument Label(long id, ScopeNodeKind kind) {
                var node = authority.Directory.Find(id, kind) ?? throw B.Broken();
                var profile = profiles.SingleOrDefault(value => value.GetValue("_id", BsonNull.Value) == new BsonInt64(id)) ?? throw B.Broken();
                if (profile["kind"] != AuthorizationBson.Name(kind) || profile["version"].AsInt64 <= 0 ||
                    profile["parentId"] != (node.ParentId is { } parent ? new BsonInt64(parent) : BsonNull.Value)) throw B.Broken();
                var normalized = OrganizationChangePolicy.Normalize(new(OrganizationChangeKind.Create, kind, node.ParentId,
                    profile["code"].AsString, profile["name"].AsString));
                if (normalized.Code != profile["code"].AsString || normalized.Name != profile["name"].AsString) throw B.Broken();
                return new() { { "id", id }, { "code", normalized.Code }, { "name", normalized.Name },
                    { "scopeVersion", node.Version }, { "profileVersion", profile["version"] } };
            }
            var items = new BsonArray(locations.Select(location => new BsonDocument { { "location", R.Location(location) },
                { "organization", Label(location.OrganizationId, ScopeNodeKind.FulfillmentOrganization) },
                { "shop", Label(location.ShopId, ScopeNodeKind.Shop) }, { "warehouse", Label(location.WarehouseId, ScopeNodeKind.Warehouse) },
                { "channel", Label(location.ChannelId, ScopeNodeKind.Channel) } }));
            await FenceQueryAsync(actor, authority, tx, [Permissions.ProcurementQualityAcceptanceRead], token);
            return new BsonDocument("items", items);
        }, ct);
    }
    public async Task<BsonDocument> ReceiptsAsync(ActorContext actor, ProcurementLocation location, long? beforeReceiptId = null,
        CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeReceiptId is <= 0) throw ProcurementQualityAcceptanceRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var context = new Context(actor, authority, tx, location, Permissions.ProcurementQualityAcceptanceRead, true); _ = Require(context);
            var filter = new BsonDocument("location", R.Location(location)); if (beforeReceiptId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(ProcurementStockReceiptBson.Receipts).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = new BsonArray(); foreach (var raw in rows.Take(25)) {
                var receipt = await new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, sources).ReadAsync(tx, raw["_id"].AsInt64, token);
                var balance = await Rows("stockBalances").Find(tx, new BsonDocument("_id", receipt["balanceId"])).SingleOrDefaultAsync(token) ?? throw B.Broken();
                ProcurementQualityAcceptanceRecords.Equal(balance, InventoryBson.Balance(InventoryHeldReceiptWriter.After(receipt)));
                var capacity = await new ProcurementQualityAcceptanceCapacityReader(database, sources).ReadAsync(tx, receipt, token);
                items.Add(new BsonDocument { { "receipt", ProcurementStockReceiptBson.View(receipt) },
                    { "heldBalance", HeldView(receipt, balance) }, { "capacity", capacity.State } });
            }
            await FenceAsync(context, token); return new() { { "location", R.Location(location) }, { "items", items },
                { "nextReceiptId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> PoliciesAsync(ActorContext actor, long receiptId, long? beforePolicyId = null,
        CancellationToken ct = default) {
        if (receiptId <= 0 || beforePolicyId is <= 0) throw ProcurementQualityAcceptanceRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var route = await Rows(ProcurementStockReceiptBson.Receipts).Find(tx, new BsonDocument("_id", receiptId))
                .Project(new BsonDocument("location", 1)).SingleOrDefaultAsync(token) ?? throw new AuthorizationDeniedException("resource_unavailable");
            var location = R.Location(route["location"]); var context = new Context(actor, authority, tx, location, Permissions.ProcurementQualityAcceptanceRead, true); _ = Require(context);
            var receipt = await new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, sources).ReadAsync(tx, receiptId, token);
            ProcurementQualityAcceptanceRecords.Equal(receipt["location"], route["location"]);
            var filter = new BsonDocument("location", receipt["location"]); if (beforePolicyId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(B.Policies).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = new BsonArray(); foreach (var row in rows.Take(25)) {
                var input = new ProcurementQualityAcceptanceInput(location, receiptId, 1, 1, row["_id"].AsInt64,
                    row["revision"].AsInt64, Now, "review", "review", "review", "sealed_undamaged",
                    receipt["input"]["lotReference"].AsString,
                    receipt["input"]["expiresOn"].IsBsonNull ? null : InvoiceBson.Date(receipt["input"]["expiresOn"]),
                    receipt["input"]["expiresOn"].IsBsonNull ? "review" : null, "review");
                try { items.Add(PolicyView(await records.PolicyAsync(tx, input, Now, true, token))); }
                catch (PersistenceConflictException error) when (error.Code is "procurement_quality_acceptance_policy_unavailable" or "procurement_quality_acceptance_nonexpiring_unavailable") { }
            }
            await FenceAsync(context, token); return new() { { "receiptId", receiptId }, { "items", items },
                { "nextPolicyId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    private async Task<BsonDocument> ProposalViewAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        var receipt = await new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, sources)
            .ReadAsync(tx, row["input"]["receiptId"].AsInt64, ct);
        return new() { { "id", row["_id"] }, { "version", 1L }, { "input", row["input"] },
            { "receipt", ProcurementStockReceiptBson.View(receipt) }, { "heldBalance", HeldView(receipt, row["heldBalance"].AsBsonDocument) },
            { "policy", PolicyView(row["policy"].AsBsonDocument) }, { "capacity", row["capacity"] },
            { "recordedAt", row["recordedAt"] }, { "reason", row["reason"] } };
    }
    private async Task<BsonDocument> ReadViewAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct) {
        var decision = await records.DecisionAsync(tx, proposal, ct);
        return new() { { "proposal", await ProposalViewAsync(tx, proposal, ct) },
            { "decision", decision is null ? BsonNull.Value : B.Result(decision) },
            { "decisionReason", decision is null ? BsonNull.Value : decision["reason"] },
            { "checkerObservation", decision is null ? BsonNull.Value : decision["observation"] } };
    }
    public async Task<BsonDocument?> ReadAsync(ActorContext actor, long proposalId, CancellationToken ct = default) {
        if (proposalId <= 0) throw ProcurementQualityAcceptanceRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var route = await Rows(B.Proposals).Find(tx, new BsonDocument("_id", proposalId)).Project(new BsonDocument("location", 1)).SingleOrDefaultAsync(token);
            if (route is null) return null;
            var location = R.Location(route["location"]); var context = new Context(actor, authority, tx, location, Permissions.ProcurementQualityAcceptanceRead, false); _ = Require(context);
            var proposal = await records.ProposalAsync(tx, proposalId, token); var result = await ReadViewAsync(tx, proposal, token);
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> HistoryAsync(ActorContext actor, ProcurementLocation location, long? beforeProposalId = null,
        CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeProposalId is <= 0) throw ProcurementQualityAcceptanceRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var context = new Context(actor, authority, tx, location, Permissions.ProcurementQualityAcceptanceRead, false); _ = Require(context);
            var filter = new BsonDocument("location", R.Location(location)); if (beforeProposalId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(B.Proposals).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = new BsonArray(); foreach (var raw in rows.Take(25)) {
                var proposal = await records.ProposalAsync(tx, raw["_id"].AsInt64, token); items.Add(await ReadViewAsync(tx, proposal, token));
            }
            await FenceAsync(context, token); return new() { { "location", R.Location(location) }, { "items", items },
                { "nextProposalId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        if (operation is not (B.ProposeOperation or B.DecideOperation)) throw ProcurementQualityAcceptanceRules.Invalid();
        if (key is not { Length: > 0 and <= 128 } || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')))
            throw ProcurementQualityAcceptanceRules.Invalid();
        await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            _ = Query(actor, authority, operation == B.ProposeOperation ? Permissions.ProcurementQualityAcceptancePropose : Permissions.ProcurementQualityAcceptanceDecide);
            var row = await records.ForKeyAsync(tx, actor.UserId, operation, key, token);
            if (row is null) {
                await FenceQueryAsync(actor, authority, tx, [operation == B.ProposeOperation ? Permissions.ProcurementQualityAcceptancePropose : Permissions.ProcurementQualityAcceptanceDecide], token);
                return new() { { "state", "not_confirmed" }, { "result", BsonNull.Value } };
            }
            var location = R.Location(row["location"]); var context = new Context(actor, authority, tx, location,
                operation == B.ProposeOperation ? Permissions.ProcurementQualityAcceptancePropose : Permissions.ProcurementQualityAcceptanceDecide, false);
            _ = Require(context); await FenceAsync(context, token);
            return new() { { "state", "confirmed" }, { "result", B.Result(row) } };
        }, ct);
    }
}
