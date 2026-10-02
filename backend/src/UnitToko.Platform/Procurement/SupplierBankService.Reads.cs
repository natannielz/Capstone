using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using B = UnitToko.Platform.Procurement.SupplierBankBson;
using S = UnitToko.Platform.Procurement.SupplierBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class SupplierBankService
{
    private async Task AuditReadAsync(Context context, string purpose, BsonDocument selection, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(context.Tx, new BsonDocument { { "_id", (await ids.NextAsync("auditEvents", ct)).Value }, { "schemaVersion", 1 },
            { "kind", B.Prefix + "details.read" }, { "actorId", context.Actor.UserId }, { "actorGrantVersion", context.Actor.GrantVersion }, { "owner", S.Owner(context.Owner) },
            { "purpose", purpose }, { "selection", selection }, { "createdAt", Now.UtcDateTime } }, cancellationToken: ct);
    }
    private async Task QueryFenceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string[] permissions, CancellationToken ct) {
        var clauses = permissions.Distinct().SelectMany(permission => Query(actor, authority, permission).Clauses).Where(clause => clause.Scope.Kind == GrantScopeKind.FulfillmentShop).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        var deadline = await LiveSessionAsync(actor, tx, ct); foreach (var permission in permissions) _ = Query(actor, authority, permission);
        if (deadline is { } expiry && clock.UtcNow >= expiry) throw new AuthorizationDeniedException("stale_or_inactive_session"); ct.ThrowIfCancellationRequested();
    }
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, CancellationToken ct = default) {
        await StorageAsync(ct); return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var owners = Query(actor, authority, Permissions.SupplierBankRead).Clauses.Where(clause => clause.Scope.Kind == GrantScopeKind.FulfillmentShop)
                .Select(clause => new SupplierOwner(clause.Scope.OrganizationId!.Value, clause.Scope.ShopId!.Value, clause.Scope.ChannelId!.Value)).Distinct()
                .OrderBy(owner => owner.OrganizationId).ThenBy(owner => owner.ShopId).ThenBy(owner => owner.ChannelId).ToArray();
            if (owners.Length > 256) throw B.Changed(); var items = new BsonArray(); var contexts = new List<Context>();
            foreach (var owner in owners) {
                var context = await ContextAsync(actor, authority, tx, owner, Permissions.SupplierBankRead, false, token); await PeopleAsync(context, false, null, token);
                var item = new BsonDocument("owner", S.Owner(owner)); item.AddRange(S.SourceView(context.Sources)); items.Add(item); contexts.Add(context); await FenceAsync(context, token);
            }
            await QueryFenceAsync(actor, authority, tx, [Permissions.SupplierBankRead], token); foreach (var context in contexts) Current(context); token.ThrowIfCancellationRequested(); return new BsonDocument("items", items);
        }, ct);
    }
    public async Task<BsonDocument> SuppliersAsync(ActorContext actor, SupplierOwner owner, long? beforeId = null, CancellationToken ct = default) {
        SupplierRules.Owner(owner); if (beforeId is <= 0) throw SupplierBankRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, owner, Permissions.SupplierBankRead, false, token); await PeopleAsync(context, false, null, token);
            var filter = new BsonDocument { { "owner", S.Owner(owner) }, { "version", new BsonDocument("$gt", 0L) } }; if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(S.Heads).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray(); var contexts = new List<Context>();
            foreach (var raw in rows.Take(25)) {
                var current = await ContextAsync(actor, authority, tx, owner, Permissions.SupplierBankRead, false, token); await PeopleAsync(current, false, null, token);
                await SupplierAsync(current, raw["_id"].AsInt64, null, null, true, token); await HeadAsync(current, raw["_id"].AsInt64, token);
                items.Add(new BsonDocument { { "supplier", B.SupplierView(current.Supplier!) }, { "beneficiaryId", current.Head?["_id"] ?? BsonNull.Value }, { "bankVersion", current.Head?["version"] ?? new BsonInt64(0) } });
                await FenceAsync(current, token); contexts.Add(current);
            }
            await FenceAsync(context, token); foreach (var current in contexts) Current(current); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "owner", S.Owner(owner) }, { "source", S.SourceView(context.Sources) }, { "items", items }, { "nextSupplierId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> PoliciesAsync(ActorContext actor, SupplierOwner owner, long supplierId, CancellationToken ct = default) {
        SupplierRules.Owner(owner); if (supplierId <= 0) throw SupplierBankRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, owner, Permissions.SupplierBankDetailsRead, true, token); await PeopleAsync(context, false, null, token);
            await SupplierAsync(context, supplierId, null, null, true, token);
            var rows = await Rows(B.Policies).Find(tx, new BsonDocument { { "owner", S.Owner(owner) }, { "supplierId", supplierId } }).Sort(new BsonDocument("_id", 1)).Limit(257).ToListAsync(token);
            if (rows.Count > 256) throw B.Changed(); var items = new BsonArray();
            foreach (var row in rows) { var policy = await records.RowAsync(tx, B.Policies, row["_id"].AsInt64, token); items.Add(B.PolicyView(policy)); await TouchAsync(context, B.Policies, policy, token); }
            await AuditReadAsync(context, "policy-discovery", new("supplierId", supplierId), token); await FenceAsync(context, token);
            return new BsonDocument { { "owner", S.Owner(owner) }, { "supplierId", supplierId }, { "items", items } };
        }, ct);
    }
    private async Task<BsonDocument> ProposalViewAsync(Context context, BsonDocument proposal, CancellationToken ct) {
        var observation = await records.ObservationForAsync(context.Tx, proposal, ct); var decision = await records.DecisionForAsync(context.Tx, proposal, ct);
        return new() { { "id", proposal["_id"] }, { "beneficiaryId", proposal["beneficiaryId"] }, { "version", 1L }, { "input", proposal["input"] }, { "source", S.SourceView(proposal["sources"].AsBsonDocument) },
            { "supplier", B.SupplierView(proposal["supplier"]) }, { "policy", B.PolicyView(proposal["policy"].AsBsonDocument) }, { "reason", proposal["reason"] }, { "proposedAt", proposal["recordedAt"] },
            { "state", decision is null ? "pending" : decision["decision"] == "approve" ? "approved" : "rejected" },
            { "observation", observation is null ? BsonNull.Value : new BsonDocument { { "id", observation["_id"] }, { "observed", observation["observed"] }, { "sourceReference", observation["sourceReference"] }, { "reason", observation["reason"] }, { "checkedAt", observation["checkedAt"] } } },
            { "decision", decision is null ? BsonNull.Value : new BsonDocument { { "id", decision["_id"] }, { "decision", decision["decision"] }, { "reason", decision["reason"] }, { "decidedAt", decision["recordedAt"] }, { "versionId", decision["versionId"] }, { "version", decision["bankVersion"] } } } };
    }
    private async Task HistoricalPeopleAsync(Context context, CancellationToken ct) {
        var observation = await records.ObservationForAsync(context.Tx, context.Proposal!, ct); var decision = await records.DecisionForAsync(context.Tx, context.Proposal!, ct);
        BindPeople(context, context.Proposal, observation, decision); await PeopleAsync(context, false, null, ct);
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, long proposalId, CancellationToken ct = default) {
        if (proposalId <= 0) throw SupplierBankRules.Invalid(); await StorageAsync(ct); return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, Permissions.SupplierBankDetailsRead, true, token); await HistoricalPeopleAsync(context, token);
            var result = await ProposalViewAsync(context, context.Proposal!, token); await AuditReadAsync(context, "proposal-details", new("proposalId", proposalId), token); await FenceAsync(context, token); return result;
        }, ct);
    }
    private async Task<bool> EligibleAsync(Context context, BsonDocument proposal, CancellationToken ct) {
        var input = B.Input(proposal["input"]); var supplier = new SupplierRecords(database, catalog);
        var head = await supplier.HeadAsync(context.Tx, input.SupplierId, ct); context.SupplierHead = head;
        if (head["versionId"] != input.SupplierVersionId || head["version"] != input.SupplierVersion) return false;
        var original = await supplier.ProposalAsync(context.Tx, proposal["supplier"]["proposalId"].AsInt64, ct);
        if (!context.Sources.Equals(original["sources"]) || !context.Sources.Equals(proposal["sources"])) return false;
        var supplierPolicy = await supplier.RowAsync(context.Tx, S.Policies, original["policy"]["_id"].AsInt64, ct);
        if (!S.PolicySnapshot(supplierPolicy).Equals(original["policy"]) || !supplierPolicy["active"].AsBoolean || supplierPolicy["approvedAt"].ToUniversalTime() > Now.UtcDateTime || supplierPolicy["expiresAt"].ToUniversalTime() <= Now.UtcDateTime) {
            await TouchAsync(context, S.Policies, supplierPolicy, ct); return false;
        }
        var policy = await records.RowAsync(context.Tx, B.Policies, input.PolicyId, ct); context.Policy = policy;
        var observed = await records.ObservationForAsync(context.Tx, proposal, ct) ?? throw B.Broken(); var at = Now;
        if (!B.Snapshot(policy).Equals(proposal["policy"]) || !policy["active"].AsBoolean || !policy["allowReferenceOnly"].AsBoolean || policy["approvedAt"].ToUniversalTime() > at.UtcDateTime || policy["expiresAt"].ToUniversalTime() <= at.UtcDateTime ||
            observed["checkedAt"].ToUniversalTime() > at.UtcDateTime || at - new DateTimeOffset(observed["checkedAt"].ToUniversalTime()) >= TimeSpan.FromSeconds(policy["maxObservationAgeSeconds"].AsInt64)) return false;
        await SupplierAsync(context, input.SupplierId, input.SupplierVersionId, input.SupplierVersion, true, ct);
        await PolicyAsync(context, input, true, ct); context.Observation = observed; Current(context); return true;
    }
    public async Task<BsonDocument> CurrentAsync(ActorContext actor, long supplierId, CancellationToken ct = default) {
        if (supplierId <= 0) throw SupplierBankRules.Invalid(); await StorageAsync(ct); return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await SupplierContextAsync(actor, authority, tx, supplierId, Permissions.SupplierBankRead, false, token); await HeadAsync(context, supplierId, token);
            BsonDocument? version = null; var eligible = false;
            if (context.Head is { } head && !head["versionId"].IsBsonNull) {
                version = await records.RowAsync(tx, B.Versions, head["versionId"].AsInt64, token); context.Proposal = await records.ProposalAsync(tx, version["proposalId"].AsInt64, token);
                await HistoricalPeopleAsync(context, token); eligible = await EligibleAsync(context, context.Proposal, token);
            } else await PeopleAsync(context, false, null, token);
            var result = new BsonDocument { { "supplierId", supplierId }, { "beneficiaryId", context.Head?["_id"] ?? BsonNull.Value }, { "versionId", version?["_id"] ?? BsonNull.Value },
                { "version", context.Head?["version"] ?? new BsonInt64(0) }, { "proposalId", version?["proposalId"] ?? BsonNull.Value }, { "details", version is null ? BsonNull.Value : B.Masked(version["details"]) }, { "eligible", eligible } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    private async Task HistoryAsync(Context context, CancellationToken ct) {
        var filter = new BsonDocument("owner", S.Owner(context.Owner)); long count = 0, observations = 0, decisions = 0, approvals = 0; var seen = new HashSet<long>();
        using var cursor = await Rows(B.Proposals).Find(context.Tx, filter).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var raw in cursor.Current) {
            var proposal = await records.ProposalAsync(context.Tx, raw["_id"].AsInt64, ct); count++;
            if (await records.ObservationForAsync(context.Tx, proposal, ct) is not null) observations++;
            if (await records.DecisionForAsync(context.Tx, proposal, ct) is { } decision) { decisions++; if (decision["decision"] == "approve") approvals++; }
            if (seen.Add(proposal["beneficiaryId"].AsInt64)) _ = await records.HeadAsync(context.Tx, proposal["beneficiaryId"].AsInt64, ct);
        }
        if (await Rows(B.Observations).CountDocumentsAsync(context.Tx, filter, cancellationToken: ct) != observations || await Rows(B.Decisions).CountDocumentsAsync(context.Tx, filter, cancellationToken: ct) != decisions ||
            await Rows(B.Heads).CountDocumentsAsync(context.Tx, filter, cancellationToken: ct) != seen.Count || await Rows(B.Versions).CountDocumentsAsync(context.Tx, filter, cancellationToken: ct) != approvals) throw B.Broken();
        var operations = new BsonArray { B.Prefix + "propose", B.Prefix + "observe", B.Prefix + "approve", B.Prefix + "reject" };
        foreach (var name in new[] { "auditEvents", "outboxEvents" }) if (await Rows(name).CountDocumentsAsync(context.Tx, new BsonDocument { { "owner", S.Owner(context.Owner) }, { "kind", new BsonDocument("$in", operations) } }, cancellationToken: ct) != count + observations + decisions) throw B.Broken();
    }
    public async Task<BsonDocument> ListAsync(ActorContext actor, SupplierOwner owner, long? beforeId = null, CancellationToken ct = default) {
        SupplierRules.Owner(owner); if (beforeId is <= 0) throw SupplierBankRules.Invalid(); await StorageAsync(ct); return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, owner, Permissions.SupplierBankRead, false, token);
            // ponytail: Full retained scoped reconstruction detects missing histories/effects; measured release load can justify an indexed reconciliation proof.
            await HistoryAsync(context, token); var filter = new BsonDocument("owner", S.Owner(owner)); if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(B.Proposals).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray();
            foreach (var raw in rows.Take(25)) {
                var proposal = context.Proposal = await records.ProposalAsync(tx, raw["_id"].AsInt64, token); context.Head = await records.HeadAsync(tx, proposal["beneficiaryId"].AsInt64, token);
                await HistoricalPeopleAsync(context, token); var decision = await records.DecisionForAsync(tx, proposal, token);
                items.Add(new BsonDocument { { "id", proposal["_id"] }, { "beneficiaryId", proposal["beneficiaryId"] }, { "supplier", B.SupplierView(proposal["supplier"]) }, { "baseVersion", proposal["input"]["baseVersion"] },
                    { "bankVersion", context.Head["version"] }, { "details", B.Masked(proposal["input"]["details"]) }, { "state", decision is null ? "pending" : decision["decision"] == "approve" ? "approved" : "rejected" }, { "proposedAt", proposal["recordedAt"] } });
                await FenceAsync(context, token);
            }
            if (rows.Count == 0) { await PeopleAsync(context, false, null, token); await FenceAsync(context, token); } Current(context); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "owner", S.Owner(owner) }, { "items", items }, { "nextProposalId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument?> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        var permission = B.Permission(operation); Metadata(new(key, "Recover original supplier bank operation", "supplier-bank-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var permissions = new[] { permission, Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead }; foreach (var required in permissions) _ = Query(actor, authority, required);
            var row = await ForKeyAsync(actor, authority, tx, operation, key, token);
            if (row is null) { await QueryFenceAsync(actor, authority, tx, permissions, token); return null; }
            var context = await ProposalContextAsync(actor, authority, tx, row["proposalId"].AsInt64, permission, true, token); BindPeople(context, row);
            await HistoricalPeopleAsync(context, token); await AuditReadAsync(context, "operation-recovery", new("proposalId", row["proposalId"]), token); await FenceAsync(context, token); return B.Result(row);
        }, ct);
    }
}
