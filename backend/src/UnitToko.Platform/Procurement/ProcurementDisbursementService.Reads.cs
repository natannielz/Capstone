using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Organizations;
using B = UnitToko.Platform.Procurement.ProcurementDisbursementBson;
using F = UnitToko.Platform.Procurement.ProcurementFundingBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;
using S = UnitToko.Platform.Procurement.SupplierBankBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementDisbursementService
{
    private async Task FenceQueryAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string permission, CancellationToken ct) {
        var queries = new[] { Permissions.ProcurementDisbursementRead, permission }.Distinct().Select(value => Query(actor, authority, value)).ToArray(); var clauses = queries.SelectMany(query => query.Clauses).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        DateTimeOffset? deadline = null;
        if (actor.Session is { } session) {
            var live = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct);
            if (live is null) throw new AuthorizationDeniedException("stale_or_inactive_session"); deadline = new DateTimeOffset(new[] { live["idleExpiresAt"].ToUniversalTime(), live["absoluteExpiresAt"].ToUniversalTime() }.Min());
        }
        foreach (var query in queries) _ = Query(actor, authority, query.Permission);
        if (deadline is { } expires && clock.UtcNow >= expires) throw new AuthorizationDeniedException("stale_or_inactive_session"); ct.ThrowIfCancellationRequested();
    }
    private async Task AuditReadAsync(Context context, string purpose, BsonDocument selection, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(context.Tx, new BsonDocument { { "_id", (await ids.NextAsync("auditEvents", ct)).Value }, { "schemaVersion", 1 }, { "kind", B.Prefix + "read" },
            { "actorId", context.Actor.UserId }, { "actorGrantVersion", context.Actor.GrantVersion }, { "location", R.Location(context.Location) }, { "purpose", purpose }, { "selection", selection }, { "createdAt", Now.UtcDateTime } }, cancellationToken: ct);
    }
    private static BsonDocument BusinessView(ProcurementDisbursementInput input, BsonDocument source, BsonDocument capacity) => new() {
        { "input", B.Input(input) }, { "authorization", B.AuthorizationView(source["funding"].AsBsonDocument) }, { "capacity", capacity }, { "beneficiary", B.BeneficiaryView(source["beneficiary"].AsBsonDocument) },
        { "payingAccount", source["payingAccount"] }, { "policy", B.PolicyView(source["policy"].AsBsonDocument) } };
    private async Task<BsonDocument> DraftViewAsync(IClientSessionHandle tx, BsonDocument draft, CancellationToken ct) {
        var result = BusinessView(B.Input(draft["input"]), draft["source"].AsBsonDocument, draft["capacity"].AsBsonDocument); var record = draft["recordedDisbursementId"].IsBsonNull ? null : await records.RecordAsync(tx, draft["recordedDisbursementId"].AsInt64, false, ct);
        result.Add("id", draft["_id"]); result.Add("version", 1L); result.Add("reason", draft["reason"]); result.Add("createdAt", draft["recordedAt"]); result.Add("state", record is null ? "draft" : "recorded_unverified");
        result.Add("disbursement", record is null ? BsonNull.Value : new BsonDocument { { "id", record["_id"] }, { "amount", record["input"]["amount"] }, { "recordedAt", record["recordedAt"] }, { "reservationId", record["reservationId"] }, { "evidence", record["evidence"] } }); return result;
    }
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, CancellationToken ct = default) {
        await StorageAsync(ct); return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var query = Query(actor, authority, Permissions.ProcurementDisbursementRead); var candidates = new HashSet<ProcurementLocation>();
            foreach (var clause in query.Clauses) {
                var scope = clause.Scope;
                if (scope.Kind == GrantScopeKind.FulfillmentWarehouse) candidates.Add(new(scope.OrganizationId!.Value, scope.ShopId!.Value, scope.WarehouseId!.Value, scope.ChannelId!.Value));
                if (scope.Kind == GrantScopeKind.FulfillmentShop) foreach (var node in authority.Directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Warehouse && node.ParentId == scope.ShopId && node.Status == AuthorizationStatus.Active))
                    candidates.Add(new(scope.OrganizationId!.Value, scope.ShopId!.Value, node.Id, scope.ChannelId!.Value));
            }
            if (candidates.Count > 256) throw B.Changed(); var items = new BsonArray(); var contexts = new List<Context>();
            foreach (var location in candidates.OrderBy(value => value.OrganizationId).ThenBy(value => value.ShopId).ThenBy(value => value.WarehouseId).ThenBy(value => value.ChannelId)) {
                var context = ContextFor(actor, authority, tx, location, Permissions.ProcurementDisbursementRead); await PeopleAsync(context, null, null, token); var item = new BsonDocument("location", R.Location(location));
                foreach (var (name, kind, id) in new[] { ("organization", ScopeNodeKind.FulfillmentOrganization, location.OrganizationId), ("shop", ScopeNodeKind.Shop, location.ShopId), ("warehouse", ScopeNodeKind.Warehouse, location.WarehouseId), ("channel", ScopeNodeKind.Channel, location.ChannelId) }) {
                    var source = await organizations.ReadAddressProfileAsync(tx, new(kind, id), token);
                    foreach (var node in source["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node)) if (authority.Directory.Find(node.Id, node.Kind) != node) throw B.Changed();
                    item.Add(name, R.Label(source)); await organizations.FenceAddressProfileAsync(tx, source, true, token);
                }
                items.Add(item); contexts.Add(context); await FenceAsync(context, token);
            }
            await FenceQueryAsync(actor, authority, tx, Permissions.ProcurementDisbursementRead, token); foreach (var context in contexts) Current(context); token.ThrowIfCancellationRequested(); return new BsonDocument("items", items);
        }, ct);
    }
    private async Task<(Context Context, BsonDocument Source)> AuthorizationContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long authorizationId, string permission, CancellationToken ct) {
        var allocation = await Rows(F.Allocations).Find(tx, new BsonDocument("_id", authorizationId)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        var route = await Rows(F.Requests).Find(tx, new BsonDocument("_id", allocation["requestId"])).Project(new BsonDocument("location", 1)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var context = ContextFor(actor, authority, tx, R.Location(route["location"]), permission);
        var capture = await funding.CaptureDisbursementAsync(database, actor, authority, tx, allocation["requestId"].AsInt64, allocation["decisionId"].AsInt64, authorizationId, ct);
        context.Funding = capture; await funding.FenceDisbursementAsync(database, actor, authority, tx, capture, ct); await PeopleAsync(context, null, null, ct);
        context.Capacity = await CapacityAsync(tx, capture.Source, ct); return (context, capture.Source);
    }
    public async Task<BsonDocument> AuthorizationsAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw ProcurementDisbursementRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = ContextFor(actor, authority, tx, location, Permissions.ProcurementDisbursementRead); _ = Query(actor, authority, Permissions.FundingRead); await PeopleAsync(context, null, null, token);
            var filter = new BsonDocument { { "location", R.Location(location) }, { "decision", "approve" } }; if (beforeId is { } before) filter.Add("authorizationId", new BsonDocument("$lt", before));
            var rows = await Rows(F.Decisions).Find(tx, filter).Sort(new BsonDocument("authorizationId", -1)).Limit(26).ToListAsync(token); var items = new BsonArray(); var captures = new List<Context>();
            foreach (var row in rows.Take(25)) {
                var (selected, source) = await AuthorizationContextAsync(actor, authority, tx, row["authorizationId"].AsInt64, Permissions.ProcurementDisbursementRead, token);
                ProcurementDisbursementRecords.Equal(source["request"]["location"], R.Location(location)); items.Add(new BsonDocument { { "authorization", B.AuthorizationView(source) }, { "capacity", selected.Capacity!.View() } });
                await FenceAsync(selected, token); captures.Add(selected);
            }
            await AuditReadAsync(context, "authorization-choices", new(), token); await FenceAsync(context, token); foreach (var selected in captures) Current(selected); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "location", R.Location(location) }, { "items", items }, { "nextAuthorizationId", rows.Count > 25 ? rows[24]["authorizationId"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> OptionsAsync(ActorContext actor, long authorizationId, CancellationToken ct = default) {
        if (authorizationId <= 0) throw ProcurementDisbursementRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var (context, source) = await AuthorizationContextAsync(actor, authority, tx, authorizationId, Permissions.DisbursementRecord, token); var supplierId = source["request"]["orderSource"]["proposal"]["input"]["supplierId"].AsInt64;
            var head = await Rows(S.Heads).Find(tx, new BsonDocument("supplierId", supplierId)).SingleOrDefaultAsync(token); BsonDocument? beneficiary = null;
            _ = Query(actor, authority, Permissions.SupplierBankRead); _ = Query(actor, authority, Permissions.SupplierBankDetailsRead);
            if (head is not null && head["version"].AsInt64 > 0) {
                context.Beneficiary = await beneficiaries.CaptureBeneficiaryAsync(database, actor, authority, tx, supplierId, head["versionId"].AsInt64, head["version"].AsInt64, token); beneficiary = B.BeneficiaryView(context.Beneficiary.Source);
            }
            var policies = await Rows(B.Policies).Find(tx, new BsonDocument("location", R.Location(context.Location))).Sort(new BsonDocument("_id", 1)).Limit(257).ToListAsync(token);
            if (policies.Count > 256) throw B.Changed(); var items = new BsonArray(); var bankChecks = new List<Action>();
            foreach (var raw in policies) {
                var policy = await records.RowAsync(tx, B.Policies, raw["_id"].AsInt64, token);
                if (policy["active"].AsBoolean && policy["approvedAt"].ToUniversalTime() <= Now.UtcDateTime && policy["expiresAt"].ToUniversalTime() > Now.UtcDateTime) {
                    var bank = await banks.PayoutBankSourceAsync(actor, authority, tx, policy["payingAccountId"].AsInt64, policy["payingAccountVersionId"].AsInt64, token);
                    var book = bank.Book; if (book["organizationId"] != context.Location.OrganizationId || book["shopId"] != context.Location.ShopId || book["channelId"] != context.Location.ChannelId || book["currency"] != "IDR") throw B.Broken();
                    var proposal = await new ReceivingAccountRecords(database).ProposalAsync(tx, bank.Account["proposalId"].AsInt64, token);
                    items.Add(new BsonDocument { { "policy", B.PolicyView(policy) }, { "payingAccount", B.AccountView(bank.Account, proposal["policy"]["bankName"].AsString) } });
                    await bank.FenceAsync(token); bankChecks.Add(bank.RequireCurrent); bankChecks.Add(() => ProcurementDisbursementRecords.PolicyAt(policy, Now));
                }
                await TouchAsync(context, B.Policies, policy, token);
            }
            await AuditReadAsync(context, "disbursement-options", new("authorizationId", authorizationId), token); await FenceAsync(context, token); foreach (var check in bankChecks) check(); Current(context); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "authorization", B.AuthorizationView(source) }, { "capacity", context.Capacity!.View() }, { "beneficiary", beneficiary is null ? BsonNull.Value : beneficiary }, { "items", items } };
        }, ct);
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, long draftId, CancellationToken ct = default) {
        if (draftId <= 0) throw ProcurementDisbursementRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await HistoricalContextAsync(actor, authority, tx, draftId, Permissions.ProcurementDisbursementRead, token); var result = await DraftViewAsync(tx, context.Draft!, token);
            await AuditReadAsync(context, "draft-review", new("draftId", draftId), token); await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> ListAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw ProcurementDisbursementRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = ContextFor(actor, authority, tx, location, Permissions.ProcurementDisbursementRead); await PeopleAsync(context, null, null, token);
            // ponytail: full owner reconstruction is O(location history); preserve orphan detection until measured load warrants a reconciled indexed proof.
            var rows = await Rows(B.Drafts).Find(tx, new BsonDocument("location", R.Location(location))).Sort(new BsonDocument("_id", -1)).ToListAsync(token);
            var items = new BsonArray(); var selected = rows.Where(row => beforeId is null || row["_id"].AsInt64 < beforeId).Take(26).ToArray(); long recordCount = 0;
            foreach (var raw in rows) {
                var row = await records.DraftAsync(tx, raw["_id"].AsInt64, token); _ = await CapacityAsync(tx, row["source"]["funding"].AsBsonDocument, token);
                if (!row["recordedDisbursementId"].IsBsonNull) { _ = await records.RecordAsync(tx, row["recordedDisbursementId"].AsInt64, false, token); recordCount++; }
                if (selected.Take(25).Any(value => value["_id"] == row["_id"])) items.Add(await DraftViewAsync(tx, row, token));
            }
            if (await Rows(B.Records).CountDocumentsAsync(tx, new BsonDocument("location", R.Location(location)), cancellationToken: token) != recordCount) throw B.Broken();
            foreach (var table in new[] { "auditEvents", "outboxEvents" }) if (await Rows(table).CountDocumentsAsync(tx, new BsonDocument { { "location", R.Location(location) }, { "kind", new BsonDocument("$in", new BsonArray { B.DraftOperation, B.RecordOperation }) } }, cancellationToken: token) != rows.Count + recordCount) throw B.Broken();
            await AuditReadAsync(context, "draft-history", new(), token); await FenceAsync(context, token);
            return new BsonDocument { { "location", R.Location(location) }, { "items", items }, { "nextDraftId", selected.Length > 25 ? selected[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument?> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        if (operation is not (B.DraftOperation or B.RecordOperation) || !UnitToko.Modules.Billing.ReceivingAccountPolicy.Text(key, 128)) throw ProcurementDisbursementRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var row = await ExistingAsync(actor, authority, tx, operation, key, token); if (row is null) { await FenceQueryAsync(actor, authority, tx, Permissions.DisbursementRecord, token); return null; }
            var context = await DraftContextAsync(actor, authority, tx, row["draftId"].AsInt64, false, token); await FenceAsync(context, token); return B.Result(row);
        }, ct);
    }
}
