using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using B = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementSupplierPayableService
{
    private AuthorizationQuery ReadQuery(ActorContext actor, AuthorizationSnapshot authority, ProcurementLocation location)
    {
        var resource = Resource(location);
        var read = ScopePolicy.Compile(actor, authority, Permissions.ProcurementPayableRead, Now);
        var procurement = ScopePolicy.Compile(actor, authority, Permissions.ProcurementRead, Now);
        AuthorizationStore.Require(read); AuthorizationStore.Require(procurement);
        if (!ScopePolicy.IsResourceActive(resource, authority.Directory) || !read.Matches(resource) || !procurement.Matches(resource))
            throw new AuthorizationDeniedException("scope_denied");
        return read;
    }
    private async Task CompleteReadAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ProcurementLocation? location, CancellationToken ct)
    {
        var permissions = new[] { Permissions.ProcurementPayableRead, Permissions.ProcurementRead };
        var clauses = permissions.SelectMany(permission => ScopePolicy.Compile(actor, authority, permission, Now).Clauses)
            .Where(clause => location is null || clause.Matches(Resource(location))).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        if (location is { } scoped) _ = ReadQuery(actor, authority, scoped);
        var expiry = await SessionAsync(actor, tx, ct);
        if (expiry is { } until && Now >= until) throw new AuthorizationDeniedException("stale_or_inactive_session");
    }
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, CancellationToken ct = default)
    {
        await StorageAsync(ct); var named = await valuations.PayableNamedTargetsAsync(actor, ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var items = new BsonArray();
            foreach (var item in named["items"].AsBsonArray) {
                try { _ = ReadQuery(actor, authority, R.Location(item["location"])); }
                catch (AuthorizationDeniedException) { continue; }
                items.Add(item);
            }
            await CompleteReadAsync(actor, authority, tx, null, token); return new BsonDocument("items", items);
        }, ct);
    }
    public async Task<BsonDocument> PoliciesAsync(ActorContext actor, ProcurementLocation location, long? beforePolicyId = null,
        CancellationToken ct = default)
    {
        ProcurementRequestRules.Location(location); if (beforePolicyId is <= 0) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            _ = ReadQuery(actor, authority, location);
            var filter = F.Eq("location", R.Location(location)) & F.Eq("active", true) &
                F.Lte("approvedAt", Now.UtcDateTime) & F.Gt("expiresAt", Now.UtcDateTime);
            if (beforePolicyId is { } before) filter &= F.Lt("_id", before);
            var raws = await Rows(B.Policies).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = new BsonArray(); foreach (var raw in raws.Take(25)) {
                var policyInput = new ProcurementSupplierPayableInput(ProcurementSupplierPayableKind.Recognize, location, 1, null, null,
                    null, raw["_id"].AsInt64, raw["revision"].AsInt64, DateOnly.FromDateTime(Now.UtcDateTime), "policy-read");
                items.Add(PolicyView(await PolicyAsync(tx, policyInput, token)));
            }
            await CompleteReadAsync(actor, authority, tx, location, token);
            return new() { { "items", items }, { "nextPolicyId", raws.Count > 25 ? raws[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> ValuationsAsync(ActorContext actor, ProcurementLocation location,
        long? beforeValuationAllocationId = null, CancellationToken ct = default)
    {
        ProcurementRequestRules.Location(location); if (beforeValuationAllocationId is <= 0) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            _ = ReadQuery(actor, authority, location);
            var filter = beforeValuationAllocationId is { } before ? F.Lt("_id", before) : F.Empty;
            var raws = await Rows(ProcurementStockValuationBson.Allocations).Find(tx, filter)
                .Sort(new BsonDocument("_id", -1)).Limit(1001).ToListAsync(token);
            if (raws.Count > 1000) throw B.Changed("procurement_supplier_payable_selector_coverage_unavailable");
            var items = new BsonArray(); foreach (var raw in raws) {
                var receipt = await Rows(ProcurementStockReceiptBson.Receipts).Find(tx, F.Eq("_id", raw["receiptId"])).SingleOrDefaultAsync(token)
                    ?? throw B.Broken();
                if (receipt["location"] != R.Location(location)) continue;
                var source = await valuations.ReadPayableSourceAsync(database, tx, raw["_id"].AsInt64, token);
                items.Add(new BsonDocument { { "source", RecognizeFacts(source) }, { "capacity", await RecognizeCapacityAsync(tx, source, token) } });
                if (items.Count == 26) break;
            }
            await CompleteReadAsync(actor, authority, tx, location, token);
            var page = new BsonArray(items.Take(25));
            return new() { { "items", page }, { "nextValuationAllocationId", items.Count > 25 ? page[24]["source"]["valuationAllocationId"] : BsonNull.Value } };
        }, ct);
    }
    private static BsonDocument PayableChoice(BsonDocument row) => new() { { "id", row["_id"] },
        { "valuationAllocationId", row["valuationAllocationId"] }, { "orderId", row["orderId"] },
        { "commercialReference", row["commercialReference"] }, { "invoiceDocumentReference", row["invoiceDocumentReference"] },
        { "supplierId", row["supplierId"] }, { "supplierName", row["supplierName"] }, { "location", row["location"] },
        { "amount", row["amount"] }, { "journalId", row["journalId"] }, { "recordedAt", row["recordedAt"] } };
    private static BsonDocument AdvanceChoice(ProcurementPayableAdvanceSource source)
    {
        var row = source.Settlement; var disbursement = source.Disbursement;
        var orderSource = disbursement["source"]["funding"]["request"]["orderSource"];
        var supplier = PurchaseOrderBson.SupplierView(orderSource["proposal"]["supplier"].AsBsonDocument);
        return new() { { "settlementId", row["_id"] }, { "disbursementId", row["disbursementId"] },
            { "orderId", orderSource["order"]["_id"] }, { "commercialReference", orderSource["proposal"]["input"]["commercialReference"]["reference"] },
            { "supplierId", supplier["id"] }, { "supplierName", supplier["displayName"] }, { "location", row["location"] },
            { "amount", row["amount"] }, { "journalId", row["journalId"] }, { "recordedAt", row["recordedAt"] } };
    }
    public async Task<BsonDocument> PayablesAsync(ActorContext actor, ProcurementLocation location, long? beforePayableId = null,
        CancellationToken ct = default)
    {
        ProcurementRequestRules.Location(location); if (beforePayableId is <= 0) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            _ = ReadQuery(actor, authority, location); var filter = F.Eq("location", R.Location(location));
            if (beforePayableId is { } before) filter &= F.Lt("_id", before);
            var raws = await Rows(B.Payables).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = new BsonArray(); foreach (var raw in raws.Take(25)) {
                var payable = await PayableRootAsync(tx, raw["_id"].AsInt64, token);
                items.Add(new BsonDocument { { "payable", PayableChoice(payable) }, { "capacity", await PayableOnlyCapacityAsync(tx, payable, token) } });
            }
            await CompleteReadAsync(actor, authority, tx, location, token);
            return new() { { "items", items }, { "nextPayableId", raws.Count > 25 ? raws[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> PayableAsync(ActorContext actor, long id, CancellationToken ct = default)
    {
        if (id <= 0) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var payable = await PayableRootAsync(tx, id, token); var location = R.Location(payable["location"]);
            _ = ReadQuery(actor, authority, location);
            var result = new BsonDocument { { "payable", PayableChoice(payable) }, { "capacity", await PayableOnlyCapacityAsync(tx, payable, token) } };
            await CompleteReadAsync(actor, authority, tx, location, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> AdvancesAsync(ActorContext actor, ProcurementLocation location, long payableId,
        long? beforeSettlementId = null, CancellationToken ct = default)
    {
        ProcurementRequestRules.Location(location); if (payableId <= 0 || beforeSettlementId is <= 0) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            _ = ReadQuery(actor, authority, location); var payable = await PayableRootAsync(tx, payableId, token);
            Equal(payable["location"], R.Location(location));
            var filter = F.Eq("location", R.Location(location)); if (beforeSettlementId is { } before) filter &= F.Lt("_id", before);
            var raws = await Rows(ProcurementSettlementBson.Events).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(1001).ToListAsync(token);
            if (raws.Count > 1000) throw B.Changed("procurement_supplier_payable_selector_coverage_unavailable");
            var items = new BsonArray(); foreach (var raw in raws) {
                var advance = await settlements.ReadPayableSourceAsync(database, tx, raw["_id"].AsInt64, token);
                var choice = AdvanceChoice(advance);
                if (choice["orderId"] != payable["orderId"] || choice["supplierId"] != payable["supplierId"]) continue;
                items.Add(new BsonDocument { { "advance", choice }, { "capacity", await AdvanceOnlyCapacityAsync(tx, advance, token) } });
                if (items.Count == 26) break;
            }
            await CompleteReadAsync(actor, authority, tx, location, token);
            var page = new BsonArray(items.Take(25));
            return new() { { "items", page }, { "nextSettlementId", items.Count > 25 ? page[24]["advance"]["settlementId"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> AdvanceAsync(ActorContext actor, long id, CancellationToken ct = default)
    {
        if (id <= 0) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var advance = await settlements.ReadPayableSourceAsync(database, tx, id, token);
            var location = R.Location(advance.Settlement["location"]); _ = ReadQuery(actor, authority, location);
            var result = new BsonDocument { { "advance", AdvanceChoice(advance) },
                { "capacity", await AdvanceOnlyCapacityAsync(tx, advance, token) } };
            await CompleteReadAsync(actor, authority, tx, location, token); return result;
        }, ct);
    }
    private async Task<BsonDocument> ReadRowAsync(IClientSessionHandle tx, long id, CancellationToken ct)
    {
        var verified = await ProposalWithProofAsync(tx, id, ct);
        var proposal = verified.Proposal; var decision = await DecisionAsync(tx, proposal, ct, verified.Proof);
        return new() { { "proposal", ProposalView(proposal) }, { "decision", decision is null ? BsonNull.Value : B.Result(decision) },
            { "decisionReason", decision is null ? BsonNull.Value : decision["reason"] } };
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, long proposalId, CancellationToken ct = default)
    {
        if (proposalId <= 0) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var row = await ReadRowAsync(tx, proposalId, token); var location = R.Location(row["proposal"]["input"]["location"]);
            _ = ReadQuery(actor, authority, location); await CompleteReadAsync(actor, authority, tx, location, token); return row;
        }, ct);
    }
    public async Task<BsonDocument> HistoryAsync(ActorContext actor, ProcurementLocation location, long? beforeProposalId = null,
        CancellationToken ct = default)
    {
        ProcurementRequestRules.Location(location); if (beforeProposalId is <= 0) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            _ = ReadQuery(actor, authority, location); var filter = F.Eq("location", R.Location(location));
            if (beforeProposalId is { } before) filter &= F.Lt("_id", before);
            var raws = await Rows(B.Proposals).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = new BsonArray(); foreach (var raw in raws.Take(25)) items.Add(await ReadRowAsync(tx, raw["_id"].AsInt64, token));
            await CompleteReadAsync(actor, authority, tx, location, token);
            return new() { { "location", R.Location(location) }, { "items", items },
                { "nextProposalId", raws.Count > 25 ? raws[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default)
    {
        if (operation is not (B.ProposeOperation or B.DecideOperation)) throw B.Invalid(); Key(key); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var permission = operation == B.ProposeOperation ? Permissions.ProcurementPayablePropose : Permissions.ProcurementPayableDecide;
            AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, permission, Now));
            var table = operation == B.ProposeOperation ? B.Proposals : B.Decisions;
            var rows = await Rows(table).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", key)).Limit(2).ToListAsync(token);
            if (rows.Count > 1) throw B.Broken();
            if (rows.Count == 0) {
                if (await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", key)).AnyAsync(token) ||
                    await Rows("auditEvents").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("kind", operation) & F.Eq("requestKey", key)).AnyAsync(token) ||
                    await Rows("outboxEvents").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("kind", operation) & F.Eq("requestKey", key)).AnyAsync(token)) throw B.Broken();
                return new BsonDocument { { "state", "not_confirmed" }, { "result", BsonNull.Value } };
            }
            var row = rows[0]; var location = R.Location(row["location"]);
            var query = ScopePolicy.Compile(actor, authority, permission, Now);
            if (!ScopePolicy.IsResourceActive(Resource(location), authority.Directory) || !query.Matches(Resource(location)))
                throw new AuthorizationDeniedException("scope_denied");
            if (operation == B.ProposeOperation) row = await ProposalAsync(tx, row["_id"].AsInt64, token);
            else {
                var proposal = await ProposalAsync(tx, row["proposalId"].AsInt64, token);
                row = await DecisionAsync(tx, proposal, token) ?? throw B.Broken();
            }
            await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
                ScopePolicy.RequiredNodes(Resource(location), authority.Directory), [],
                authority.Grants.Where(grant => query.Clauses.Any(clause => clause.GrantId == grant.Id && clause.Matches(Resource(location)))),
                authority.Memberships.Where(member => query.Clauses.Any(clause => clause.MembershipId == member.Id && clause.Matches(Resource(location)))), tx, token);
            var expiry = await SessionAsync(actor, tx, token);
            if (expiry is { } until && Now >= until) throw new AuthorizationDeniedException("stale_or_inactive_session");
            return new BsonDocument { { "state", "confirmed" }, { "result", B.Result(row) } };
        }, ct);
    }
}
