using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Organizations;
using B = UnitToko.Platform.Procurement.ProcurementSettlementBson;
using D = UnitToko.Platform.Procurement.ProcurementDisbursementBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementSettlementService
{
    private async Task FenceQueryAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, bool write, CancellationToken ct) {
        var permissions = write ? new[] { Permissions.ProcurementSettlementRead, Permissions.ProcurementDisbursementRead, Permissions.DisbursementVerify } : [Permissions.ProcurementSettlementRead, Permissions.ProcurementDisbursementRead];
        var clauses = permissions.SelectMany(value => Query(actor, authority, value).Clauses).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        DateTimeOffset? expiry = null;
        if (actor.Session is { } session) {
            var live = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct)
                ?? throw new AuthorizationDeniedException("stale_or_inactive_session"); expiry = new DateTimeOffset(new[] { live["idleExpiresAt"].ToUniversalTime(), live["absoluteExpiresAt"].ToUniversalTime() }.Min());
        }
        foreach (var permission in permissions) _ = Query(actor, authority, permission);
        if (expiry is { } until && clock.UtcNow >= until) throw new AuthorizationDeniedException("stale_or_inactive_session"); ct.ThrowIfCancellationRequested();
    }
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, CancellationToken ct = default) {
        await StorageAsync(ct); return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var query = Query(actor, authority, Permissions.ProcurementSettlementRead); var choices = new HashSet<ProcurementLocation>();
            foreach (var clause in query.Clauses) {
                var scope = clause.Scope;
                if (scope.Kind == GrantScopeKind.FulfillmentWarehouse) choices.Add(new(scope.OrganizationId!.Value, scope.ShopId!.Value, scope.WarehouseId!.Value, scope.ChannelId!.Value));
                if (scope.Kind == GrantScopeKind.FulfillmentShop) foreach (var node in authority.Directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Warehouse && node.ParentId == scope.ShopId && node.Status == AuthorizationStatus.Active))
                    choices.Add(new(scope.OrganizationId!.Value, scope.ShopId!.Value, node.Id, scope.ChannelId!.Value));
            }
            if (choices.Count > 256) throw B.Changed(); var result = new BsonArray(); var contexts = new List<Context>();
            foreach (var location in choices.OrderBy(value => value.OrganizationId).ThenBy(value => value.ShopId).ThenBy(value => value.WarehouseId).ThenBy(value => value.ChannelId)) {
                var context = ContextFor(actor, authority, tx, location); await PeopleAsync(context, null, token); var item = new BsonDocument("location", R.Location(location));
                foreach (var (name, kind, id) in new[] { ("organization", ScopeNodeKind.FulfillmentOrganization, location.OrganizationId), ("shop", ScopeNodeKind.Shop, location.ShopId), ("warehouse", ScopeNodeKind.Warehouse, location.WarehouseId), ("channel", ScopeNodeKind.Channel, location.ChannelId) }) {
                    var source = await organizations.ReadAddressProfileAsync(tx, new(kind, id), token);
                    foreach (var node in source["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node)) if (authority.Directory.Find(node.Id, node.Kind) != node) throw B.Changed();
                    item.Add(name, R.Label(source)); await organizations.FenceAddressProfileAsync(tx, source, true, token);
                }
                result.Add(item); await FenceAsync(context, token); contexts.Add(context);
            }
            await FenceQueryAsync(actor, authority, tx, false, token); foreach (var context in contexts) Current(context); return new BsonDocument("items", result);
        }, ct);
    }
    public async Task<BsonDocument> DisbursementsAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw ProcurementSettlementRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = ContextFor(actor, authority, tx, location); var filter = new BsonDocument("location", R.Location(location)); if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(D.Records).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray(); var contexts = new List<Context>();
            foreach (var raw in rows.Take(25)) {
                var selected = await SourceAsync(actor, authority, tx, raw["_id"].AsInt64, false, false, token); var saved = await records.ForDisbursementAsync(tx, raw["_id"].AsInt64, token);
                if (saved is not null) saved = await records.ReadAsync(tx, saved["_id"].AsInt64, token, selected.Source); await PeopleAsync(selected, saved, token);
                items.Add(new BsonDocument { { "disbursement", B.DisbursementView(selected.Source!.Record) }, { "settlement", saved is null ? BsonNull.Value : B.Result(saved) } }); await FenceAsync(selected, token); contexts.Add(selected);
            }
            await PeopleAsync(context, null, token); await AuditReadAsync(context, "settlement-records", new(), token); await FenceAsync(context, token); foreach (var selected in contexts) Current(selected);
            return new BsonDocument { { "location", R.Location(location) }, { "items", items }, { "nextDisbursementId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> OptionsAsync(ActorContext actor, long disbursementId, CancellationToken ct = default) {
        Revision(disbursementId, 1); await StorageAsync(ct); return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await SourceAsync(actor, authority, tx, disbursementId, false, true, token); await PeopleAsync(context, null, token); var record = context.Source!.Record;
            var policies = await Rows(B.Policies).Find(tx, new BsonDocument { { "location", record["location"] }, { "payingAccountId", record["input"]["payingAccountId"] }, { "payingAccountVersionId", record["input"]["payingAccountVersionId"] } }).Sort(new BsonDocument("_id", 1)).Limit(257).ToListAsync(token);
            if (policies.Count > 256) throw B.Changed(); var items = new BsonArray(); var selected = new List<BsonDocument>();
            foreach (var raw in policies) {
                var policy = await records.RowAsync(tx, B.Policies, raw["_id"].AsInt64, token);
                if (!policy["active"].AsBoolean || !policy["allowReferenceOnly"].AsBoolean || policy["approvedAt"].ToUniversalTime() > Now.UtcDateTime || policy["expiresAt"].ToUniversalTime() <= Now.UtcDateTime) continue;
                B.PolicyAt(policy, Now); items.Add(B.PolicyView(policy)); selected.Add(policy);
                if ((await Rows(B.Policies).UpdateOneAsync(tx, policy, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
            }
            await AuditReadAsync(context, "settlement-options", new("disbursementId", disbursementId), token); await FenceAsync(context, token); foreach (var policy in selected) B.PolicyAt(policy, Now);
            return new BsonDocument { { "disbursement", B.DisbursementView(record) }, { "items", items } };
        }, ct);
    }
    public async Task<BsonDocument> BankImportsAsync(ActorContext actor, long disbursementId, long? beforeId = null, CancellationToken ct = default) {
        Revision(disbursementId, 1); if (beforeId is <= 0) throw ProcurementSettlementRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await SourceAsync(actor, authority, tx, disbursementId, false, true, token); var input = context.Source!.Record["input"]; await PeopleAsync(context, null, token);
            await AuditReadAsync(context, "settlement-bank-imports", new("disbursementId", disbursementId), token);
            var result = await banks.ProcurementSettlementImportsAsync(actor, authority, tx, input["payingAccountId"].AsInt64, input["payingAccountVersionId"].AsInt64, beforeId, token, inner => FenceAsync(context, inner));
            result.Add("disbursementId", disbursementId); result.Add("payingAccountId", input["payingAccountId"]); result.Add("payingAccountVersionId", input["payingAccountVersionId"]);
            Current(context); token.ThrowIfCancellationRequested(); return result;
        }, ct);
    }
    public async Task<BsonDocument> DebitsAsync(ActorContext actor, long disbursementId, long importId, CancellationToken ct = default) {
        Revision(disbursementId, 1); if (importId <= 0) throw ProcurementSettlementRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await SourceAsync(actor, authority, tx, disbursementId, false, true, token); var input = context.Source!.Record["input"]; await PeopleAsync(context, null, token);
            await AuditReadAsync(context, "settlement-debits", new("disbursementId", disbursementId), token);
            var result = await banks.ProcurementSettlementDebitsAsync(actor, authority, tx, input["payingAccountId"].AsInt64, input["payingAccountVersionId"].AsInt64, importId, Money.Parse(input["amount"].AsString), InvoiceBson.Date(input["transferDate"]), token, inner => FenceAsync(context, inner));
            result.Add("disbursementId", disbursementId); result.Add("importId", importId); result.Add("payingAccountId", input["payingAccountId"]); result.Add("payingAccountVersionId", input["payingAccountVersionId"]);
            Current(context); token.ThrowIfCancellationRequested(); return result;
        }, ct);
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, long disbursementId, CancellationToken ct = default) {
        Revision(disbursementId, 1); await StorageAsync(ct); return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await SourceAsync(actor, authority, tx, disbursementId, false, false, token); var row = await records.ForDisbursementAsync(tx, disbursementId, token);
            if (row is not null) row = await records.ReadAsync(tx, row["_id"].AsInt64, token, context.Source); await PeopleAsync(context, row, token);
            await AuditReadAsync(context, "settlement-read", new("disbursementId", disbursementId), token); await FenceAsync(context, token);
            return new BsonDocument { { "disbursement", B.DisbursementView(context.Source!.Record) }, { "settlement", row is null ? BsonNull.Value : B.View(row) } };
        }, ct);
    }
    public async Task<BsonDocument> ListAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw ProcurementSettlementRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = ContextFor(actor, authority, tx, location); var filter = new BsonDocument("location", R.Location(location)); if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(B.Events).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray(); var contexts = new List<Context>();
            await records.OrphansAsync(tx, token);
            foreach (var raw in rows.Take(25)) {
                var selected = await SourceAsync(actor, authority, tx, raw["disbursementId"].AsInt64, false, false, token);
                var row = await records.ReadAsync(tx, raw["_id"].AsInt64, token, selected.Source); ProcurementSettlementRecords.Equal(row["location"], R.Location(location));
                await PeopleAsync(selected, row, token); items.Add(B.View(row)); await FenceAsync(selected, token); contexts.Add(selected);
            }
            await PeopleAsync(context, null, token); await AuditReadAsync(context, "settlement-history", new(), token); await FenceAsync(context, token); foreach (var selected in contexts) Current(selected);
            return new BsonDocument { { "location", R.Location(location) }, { "items", items }, { "nextSettlementId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
}
