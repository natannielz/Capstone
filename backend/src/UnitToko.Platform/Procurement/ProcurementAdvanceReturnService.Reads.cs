using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementAdvanceReturnBson;
using P = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementAdvanceReturnService
{
    private AuthorizationQuery ReadQuery(ActorContext actor, AuthorizationSnapshot authority, ProcurementLocation location)
    {
        var resource = Resource(location);
        var read = ScopePolicy.Compile(actor, authority, Permissions.ProcurementAdvanceReturnRead, Now);
        var procurement = ScopePolicy.Compile(actor, authority, Permissions.ProcurementRead, Now);
        AuthorizationStore.Require(read); AuthorizationStore.Require(procurement);
        if (!ScopePolicy.IsResourceActive(resource, authority.Directory) || !read.Matches(resource) || !procurement.Matches(resource))
            throw new AuthorizationDeniedException("scope_denied");
        return read;
    }
    private async Task CompleteReadAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ProcurementLocation? location, CancellationToken ct)
    {
        var clauses = new[] { Permissions.ProcurementAdvanceReturnRead, Permissions.ProcurementRead }
            .SelectMany(permission => ScopePolicy.Compile(actor, authority, permission, Now).Clauses)
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
        await StorageAsync(ct); var named = await requests.TargetsAsync(actor, ct);
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
                var sample = new ProcurementAdvanceReturnInput(location, 1, raw["_id"].AsInt64, raw["revision"].AsInt64,
                    DateOnly.FromDateTime(Now.UtcDateTime), "policy-read", [new(1, new UnitToko.Modules.Accounting.Money(1))]);
                items.Add(PolicyView(await PolicyAsync(tx, sample, token)));
            }
            await CompleteReadAsync(actor, authority, tx, location, token);
            return new() { { "items", items }, { "nextPolicyId", raws.Count > 25 ? raws[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> ReceiptsAsync(ActorContext actor, ProcurementLocation location, long? beforeReceiptId = null,
        CancellationToken ct = default)
    {
        ProcurementRequestRules.Location(location); if (beforeReceiptId is <= 0) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            _ = ReadQuery(actor, authority, location);
            var filter = F.Eq("scope.fulfillmentOrganizationId", location.OrganizationId) & F.Eq("scope.shopId", location.ShopId) &
                F.Eq("scope.channelId", location.ChannelId) & F.Eq("scope.warehouseId", BsonNull.Value);
            if (beforeReceiptId is { } before) filter &= F.Lt("_id", before);
            var raws = await Rows(BankReceiptBson.Receipts).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = new BsonArray(); var bankFences = new List<Action>();
            foreach (var raw in raws.Take(25)) {
                var owned = await Rows(PaymentVerificationBson.Parties).Find(tx, F.Eq("_id", raw["_id"])).SingleOrDefaultAsync(token);
                if (owned is not null) continue;
                var bank = await bankReceipts.SupplierReturnSourceAsync(actor, authority, tx, raw["_id"].AsInt64, false, token);
                Equal(BankReceiptBson.Snapshot(bank.Receipt), BankReceiptBson.Snapshot(raw)); bankFences.Add(bank.RequireCurrent);
                await new PaymentReceiptPurposeRecords(database).RequireUnclaimedForSupplierAsync(tx, raw, token);
                items.Add(new BsonDocument { { "receiptId", raw["_id"] }, { "receiptHash", raw["snapshotHash"] },
                    { "receiptAmount", InvoiceBson.Money(raw["amount"]).ToString() }, { "bookedDate", raw["bookedDate"] },
                    { "bankReference", bank.BankRow["reference"] }, { "bankAccountId", bank.Account["accountId"] },
                    { "bankAccountVersionId", bank.Account["_id"] } });
            }
            await CompleteReadAsync(actor, authority, tx, location, token);
            foreach (var fence in bankFences) fence();
            var page = new BsonArray(items.Take(25));
            return new() { { "location", R.Location(location) }, { "items", page },
                { "nextReceiptId", raws.Count > 25 ? raws[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
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
    public async Task<BsonDocument> AdvancesAsync(ActorContext actor, ProcurementLocation location, long receiptId,
        long? beforeSettlementId = null, CancellationToken ct = default)
    {
        ProcurementRequestRules.Location(location); if (receiptId <= 0 || beforeSettlementId is <= 0) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            _ = ReadQuery(actor, authority, location);
            var bank = await bankReceipts.SupplierReturnSourceAsync(actor, authority, tx, receiptId, false, token);
            if (bank.Receipt["scope"]["fulfillmentOrganizationId"] != location.OrganizationId ||
                bank.Receipt["scope"]["shopId"] != location.ShopId || bank.Receipt["scope"]["channelId"] != location.ChannelId ||
                !bank.Receipt["scope"]["warehouseId"].IsBsonNull) throw B.Broken();
            await new PaymentReceiptPurposeRecords(database).RequireUnclaimedForSupplierAsync(tx, bank.Receipt, token);
            var filter = F.Eq("location", R.Location(location)); if (beforeSettlementId is { } before) filter &= F.Lt("_id", before);
            var raws = await Rows(ProcurementSettlementBson.Events).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = new BsonArray(); var returnProofs = new ProcurementAdvanceReturnRecords.OwnerProofCache(tx);
            foreach (var raw in raws.Take(25)) {
                var advance = await settlements.ReadPayableSourceAsync(database, tx, raw["_id"].AsInt64, token);
                items.Add(new BsonDocument { { "advance", AdvanceChoice(advance) },
                    { "capacity", (await payables.ReadAdvanceCapacityAsync(tx, advance, token, returnProofs)).State } });
            }
            await CompleteReadAsync(actor, authority, tx, location, token); bank.RequireCurrent();
            var page = new BsonArray(items.Take(25));
            return new() { { "location", R.Location(location) }, { "receiptId", receiptId }, { "items", page },
                { "nextSettlementId", raws.Count > 25 ? raws[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    private async Task<BsonDocument> ReadRowAsync(IClientSessionHandle tx, long id, CancellationToken ct)
    {
        var proposal = await records.ProposalAsync(tx, id, ct);
        var decision = await records.DecisionAsync(tx, proposal, ct);
        if (decision is not null && decision["decision"] == "approve") {
            var returnProofs = new ProcurementAdvanceReturnRecords.OwnerProofCache(tx);
            foreach (var selected in B.Input(proposal["input"]).Lines) {
                var advance = await settlements.ReadPayableSourceAsync(database, tx, selected.SettlementId, ct);
                _ = await payables.ReadAdvanceCapacityAsync(tx, advance, ct, returnProofs);
            }
        }
        return new() { { "proposal", ProposalView(proposal) }, { "decision", decision is null ? BsonNull.Value : B.Result(decision) },
            { "decisionReason", decision is null ? BsonNull.Value : decision["reason"] } };
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, long proposalId, CancellationToken ct = default)
    {
        if (proposalId <= 0) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var header = await Rows(B.Proposals).Find(tx, F.Eq("_id", proposalId) & ProcurementAdvanceReturnSchemas.Validator(B.Proposals))
                .Project(new BsonDocument { { "_id", 1 }, { "location", 1 } }).SingleOrDefaultAsync(token) ?? throw B.Broken();
            _ = ReadQuery(actor, authority, R.Location(header["location"]));
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
            var permission = operation == B.ProposeOperation ? Permissions.ProcurementAdvanceReturnPropose : Permissions.ProcurementAdvanceReturnDecide;
            AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, permission, Now));
            var table = operation == B.ProposeOperation ? B.Proposals : B.Decisions;
            var rows = await Rows(table).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", key)).Limit(2).ToListAsync(token);
            if (rows.Count > 1) throw B.Broken();
            if (rows.Count == 0) {
                if (await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", key)).AnyAsync(token) ||
                    await Rows("auditEvents").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("kind", operation) & F.Eq("requestKey", key)).AnyAsync(token) ||
                    await Rows("outboxEvents").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("kind", operation) & F.Eq("requestKey", key)).AnyAsync(token)) throw B.Broken();
                await CompleteLookupAsync(actor, authority, tx, permission, null, token);
                return new BsonDocument { { "state", "not_confirmed" }, { "result", BsonNull.Value } };
            }
            var row = rows[0]; var location = R.Location(row["location"]);
            var query = ScopePolicy.Compile(actor, authority, permission, Now);
            if (!ScopePolicy.IsResourceActive(Resource(location), authority.Directory) || !query.Matches(Resource(location)))
                throw new AuthorizationDeniedException("scope_denied");
            if (operation == B.ProposeOperation) row = await records.ProposalAsync(tx, row["_id"].AsInt64, token);
            else {
                var proposal = await records.ProposalAsync(tx, row["proposalId"].AsInt64, token);
                row = await records.DecisionAsync(tx, proposal, token) ?? throw B.Broken();
            }
            if (operation == B.DecideOperation && row["decision"] == "approve") {
                var returnProofs = new ProcurementAdvanceReturnRecords.OwnerProofCache(tx);
                foreach (var selected in B.Input(row["input"]).Lines) {
                    var advance = await settlements.ReadPayableSourceAsync(database, tx, selected.SettlementId, token);
                    _ = await payables.ReadAdvanceCapacityAsync(tx, advance, token, returnProofs);
                }
            }
            await CompleteLookupAsync(actor, authority, tx, permission, location, token);
            return new BsonDocument { { "state", "confirmed" }, { "result", B.Result(row) } };
        }, ct);
    }
    private async Task CompleteLookupAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        string permission, ProcurementLocation? location, CancellationToken ct)
    {
        var query = ScopePolicy.Compile(actor, authority, permission, Now); AuthorizationStore.Require(query);
        var clauses = query.Clauses.Where(clause => location is null || clause.Matches(Resource(location))).ToArray();
        if (location is { } scoped && (!ScopePolicy.IsResourceActive(Resource(scoped), authority.Directory) ||
            !query.Matches(Resource(scoped)))) throw new AuthorizationDeniedException("scope_denied");
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        var expiry = await SessionAsync(actor, tx, ct);
        if (expiry is { } until && Now >= until) throw new AuthorizationDeniedException("stale_or_inactive_session");
        query = ScopePolicy.Compile(actor, authority, permission, Now); AuthorizationStore.Require(query);
        if (location is { } current && (!ScopePolicy.IsResourceActive(Resource(current), authority.Directory) ||
            !query.Matches(Resource(current)))) throw new AuthorizationDeniedException("scope_denied");
    }
}
