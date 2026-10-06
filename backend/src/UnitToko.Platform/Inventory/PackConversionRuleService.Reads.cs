using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Inventory.PackConversionRuleBson;

namespace UnitToko.Platform.Inventory;

public sealed partial class PackConversionRuleService
{
    public Task<ImmutableArray<StockWarehouseOption>> WarehouseChoicesAsync(ActorContext actor, AuthorizationReadMetadata audit, CancellationToken ct = default) =>
        new UnitToko.Platform.Catalog.CatalogAuthoringService(database, unit, ids, clock).PackConversionWarehouseOptionsAsync(actor, audit, ct);
    public Task<CatalogPage<StockCountPackageOption>> PackageChoicesAsync(ActorContext actor, GrantScope location, PackConversionChoiceRole role,
        int offset, int limit, string? search, AuthorizationReadMetadata audit, CancellationToken ct = default) =>
        new UnitToko.Platform.Catalog.CatalogAuthoringService(database, unit, ids, clock).PackConversionPackagesAsync(actor, location, role, offset, limit, search, audit, ct);
    private async Task<BsonDocument> AuthorizedProposalAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long id, CancellationToken ct) {
        var route = await Rows(B.Proposals).Find(tx, new BsonDocument("_id", id)).Project(new BsonDocument("input.location", 1)).SingleOrDefaultAsync(ct)
            ?? throw new UnitToko.Platform.Authorization.AuthorizationDeniedException("resource_unavailable");
        var context = new Context(actor, authority, tx, StockCountBson.Scope(route["input"]["location"].AsBsonDocument)); _ = Current(context);
        return await records.ProposalAsync(tx, id, ct);
    }
    public async Task<BsonDocument> ReadProposalAsync(ActorContext actor, long id, CancellationToken ct = default) {
        if (id <= 0) throw PackConversionRulePolicy.Invalid(); await PackConversionRuleStorage.RequireAsync(database, ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var proposal = await AuthorizedProposalAsync(actor, authority, tx, id, token); var decision = await records.DecisionAsync(tx, proposal, token);
            var context = new Context(actor, authority, tx, B.Input(proposal["input"].AsBsonDocument).Location);
            var canReview = await ReviewEligibilityAsync(context, proposal, token); await FenceAsync(context, false, token);
            return new BsonDocument { { "proposal", proposal }, { "decision", decision is null ? BsonNull.Value : decision }, { "canReview", canReview && decision is null }, { "physicalConversionAvailable", false } };
        }, ct);
    }
    public async Task<BsonDocument> ListRulesAsync(ActorContext actor, GrantScope location, long? beforeId = null, CancellationToken ct = default) {
        StockCountPolicy.Location(location); if (beforeId is <= 0) throw PackConversionRulePolicy.Invalid(); await PackConversionRuleStorage.RequireAsync(database, ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = new Context(actor, authority, tx, location); _ = Current(context); await PeopleAsync(context, null, token);
            var filter = UnitToko.Platform.Catalog.CatalogRepository.OwnerFilter("input.location", location);
            if (beforeId is { } previous) filter &= new BsonDocument("_id", new BsonDocument("$lt", previous));
            var rows = await Rows(B.Rules).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray();
            foreach (var row in rows.Take(25)) items.Add((await records.RuleAsync(tx, row["_id"].AsInt64, token)).Rule);
            await FenceAsync(context, false, token);
            return new BsonDocument { { "items", items }, { "nextRuleId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value }, { "physicalConversionAvailable", false } };
        }, ct);
    }
    public async Task<BsonDocument> ListProposalsAsync(ActorContext actor, GrantScope location, long? beforeId = null, CancellationToken ct = default) {
        StockCountPolicy.Location(location); if (beforeId is <= 0) throw PackConversionRulePolicy.Invalid(); await PackConversionRuleStorage.RequireAsync(database, ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = new Context(actor, authority, tx, location); _ = Current(context); await PeopleAsync(context, null, token);
            var filter = UnitToko.Platform.Catalog.CatalogRepository.OwnerFilter("input.location", location);
            if (beforeId is { } previous) filter &= new BsonDocument("_id", new BsonDocument("$lt", previous));
            var rows = await Rows(B.Proposals).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray();
            foreach (var raw in rows.Take(25)) {
                var proposal = await AuthorizedProposalAsync(actor, authority, tx, raw["_id"].AsInt64, token);
                if (B.Input(proposal["input"].AsBsonDocument).Location != location) throw B.Broken();
                var decision = await records.DecisionAsync(tx, proposal, token);
                var rowContext = new Context(actor, authority, tx, location);
                var canReview = await ReviewEligibilityAsync(rowContext, proposal, token); await FenceAsync(rowContext, false, token);
                items.Add(new BsonDocument { { "proposal", proposal }, { "decision", decision is null ? BsonNull.Value : decision }, { "canReview", canReview && decision is null }, { "physicalConversionAvailable", false } });
            }
            await FenceAsync(context, false, token);
            return new BsonDocument { { "items", items }, { "nextProposalId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value }, { "physicalConversionAvailable", false } };
        }, ct);
    }
    public async Task<BsonDocument?> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        if (operation is not (B.Propose or B.Decide) || !StockCountBson.Key(key)) throw PackConversionRulePolicy.Invalid(); await PackConversionRuleStorage.RequireAsync(database, ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var table = operation == B.Propose ? B.Proposals : B.Decisions;
            var route = await Rows(table).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "requestKey", key } }).SingleOrDefaultAsync(token);
            if (route is null) {
                var query = ScopePolicy.Compile(actor, authority, UnitToko.Modules.Identity.Permissions.StockConvert, clock.UtcNow);
                UnitToko.Platform.Authorization.AuthorizationStore.Require(query);
                _ = await records.ForKeyAsync(tx, actor.UserId, operation, key, token);
                var people = await new UnitToko.Platform.Identity.PersonRegistryService(database, clock).ResolveProofsAsync(tx, [actor.UserId], token);
                await new UnitToko.Platform.Authorization.PersonAuthorizationFence(database, clock).TouchAsync(tx, people, token);
                await new UnitToko.Platform.Authorization.HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
                    query.Clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
                    authority.Grants.Where(grant => query.Clauses.Any(clause => clause.GrantId == grant.Id)),
                    authority.Memberships.Where(member => query.Clauses.Any(clause => clause.MembershipId == member.Id)), tx, token);
                people.RequireCurrent(tx, clock.UtcNow);
                UnitToko.Platform.Authorization.AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, UnitToko.Modules.Identity.Permissions.StockConvert, clock.UtcNow));
                token.ThrowIfCancellationRequested(); return null;
            }
            var proposalId = operation == B.Propose ? route["_id"].AsInt64 : route["proposalId"].AsInt64;
            var proposal = await AuthorizedProposalAsync(actor, authority, tx, proposalId, token);
            var row = await records.ForKeyAsync(tx, actor.UserId, operation, key, token) ?? throw B.Broken();
            var context = new Context(actor, authority, tx, B.Input(proposal["input"].AsBsonDocument).Location);
            await PeopleAsync(context, operation == B.Decide ? proposal : null, token); await FenceAsync(context, false, token); return B.Result(row);
        }, ct);
    }
    public async Task<PackConversionAdmission> ReadAdmissionAsync(ActorContext actor, PackConversionAdmissionInput input, CancellationToken ct = default) {
        input = PackConversionRulePolicy.Copy(input); await PackConversionRuleStorage.RequireAsync(database, ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var proposal = await AuthorizedProposalAsync(actor, authority, tx, input.RuleId, token);
            var approved = await records.RuleAsync(tx, input.RuleId, token);
            if (approved.Rule["version"] != input.RuleVersion) throw B.Changed();
            var rule = B.Input(proposal["input"].AsBsonDocument); var context = await PrepareAsync(actor, authority, tx, rule, token);
            if (context.Catalogue != proposal["catalog"]) throw B.Changed();
            var quantities = PackConversionRuleCatalogue.Quantities(rule, context.Catalogue ?? throw B.Broken(), input.RuleId, input.Steps);
            var balance = await new InventoryRepository(database).ReadAsync(tx, input.SourceBalanceId, token);
            if (balance.PhysicalPoolId != input.PoolId || balance.Balance.Revision != input.SourceBalanceRevision ||
                balance.Balance.Key.Location.ToScope() != rule.Location || balance.Balance.Key.SkuId.Value != rule.SourceSkuId || balance.Balance.Key.DefinitionId.Value != rule.SourceDefinitionId) throw B.Changed();
            var source = await new StockSourceSetupService(database, unit, ids, clock).ReadPackConversionSourceAsync(actor, authority, tx,
                rule.Location, input.PoolId, input.PoolRevision, token);
            var available = StockBalancePolicy.AvailablePackages(balance.Balance); var blockers = ImmutableArray.CreateBuilder<string>();
            if (available < quantities.SourcePackages) blockers.Add("insufficient_unheld_packages");
            if (!source["active"].AsBoolean) blockers.Add("source_inactive");
            // No genuine activation or available-lot value-transfer owner exists
            // in96. Never interpret raw active=true or posted HELD receipt value
            // as that proof; no write-ready binding/token is returned here.
            blockers.Add("source_activation_authority_unavailable"); blockers.Add("transferable_lot_value_authority_unavailable");
            await FenceAsync(context, true, token);
            return new PackConversionAdmission(input.RuleId, input.RuleVersion, "blocked", quantities, available, blockers.ToImmutable(), false);
        }, ct);
    }
}
