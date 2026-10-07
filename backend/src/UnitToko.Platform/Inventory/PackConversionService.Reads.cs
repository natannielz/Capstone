using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Inventory.PackConversionBson;

namespace UnitToko.Platform.Inventory;

public sealed partial class PackConversionService
{
    private AuthorizationQuery DiscoveryAuthority(ActorContext actor, AuthorizationSnapshot authority, GrantScope location) {
        StockCountPolicy.Location(location);
        var query = ScopePolicy.Compile(actor, authority, Permissions.StockConvert, Now); AuthorizationStore.Require(query);
        var resource = CatalogRepository.Resource(location);
        if (!ScopePolicy.IsResourceActive(resource, authority.Directory) || !query.Matches(resource)) throw new AuthorizationDeniedException("scope_denied");
        return query;
    }
    private async Task FenceDiscoveryAsync(ActorContext actor, AuthorizationSnapshot authority, GrantScope location, IClientSessionHandle tx, CancellationToken ct) {
        var query = DiscoveryAuthority(actor, authority, location); var resource = CatalogRepository.Resource(location);
        var people = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, [actor.UserId], ct);
        if (people.Evidence.Any(person => person.Verification != PersonVerification.Verified)) throw new AuthorizationDeniedException("person_evidence_invalid");
        await new PersonAuthorizationFence(database, clock).TouchAsync(tx, people, ct);
        var clauses = query.Clauses.Where(clause => clause.Matches(resource)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(ScopePolicy.RequiredNodes(resource, authority.Directory)).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        people.RequireCurrent(tx, Now); DiscoveryAuthority(actor, authority, location);
    }
    /// <summary>Actual selected-rule balances, with each candidate admitted by the
    /// real current owner. Only a proven insufficient available quantity is skipped.</summary>
    public async Task<BsonDocument> SourcesAsync(ActorContext actor, GrantScope location, long ruleId, long ruleVersion,
        long beforeSourceBalanceId = 0, int limit = 25, CancellationToken ct = default) {
        if (ruleId <= 0 || ruleVersion <= 0 || beforeSourceBalanceId is < 0 or >= long.MaxValue - 2 || limit is < 1 or > 100)
            throw PackConversionExecutionPolicy.Invalid();
        await StorageAsync(ct);
        var page = await authorization.ReadAsync(actor, async (authority, tx, token) => {
            DiscoveryAuthority(actor, authority, location);
            var approved = await rules.RuleAsync(tx, ruleId, token);
            var input = PackConversionRuleBson.Input(approved.Rule["input"].AsBsonDocument);
            if (input.Location != location) throw new AuthorizationDeniedException("scope_denied");
            if (approved.Rule["version"] != ruleVersion || await catalogue.CurrentAsync(tx, input, token) != approved.Rule["catalog"]) throw B.Changed();
            var stockLocation = new StockLocation(new(location.OrganizationId!.Value), new(location.ShopId!.Value), new(location.WarehouseId!.Value), new(location.ChannelId!.Value));
            var filter = new BsonDocument { { "location", StockSourceSetupBson.Location(stockLocation) }, { "skuId", input.SourceSkuId }, { "definitionId", input.SourceDefinitionId } };
            if (beforeSourceBalanceId > 0) filter.Add("_id", new BsonDocument("$lt", beforeSourceBalanceId));
            var rows = await Rows("stockBalances").Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(limit + 1).ToListAsync(token);
            await FenceDiscoveryAsync(actor, authority, location, tx, token);
            return (Ids: rows.Take(limit).Select(row => row["_id"].AsInt64).ToArray(), Next: rows.Count > limit ? rows[limit - 1]["_id"].AsInt64 : (long?)null);
        }, ct);
        var items = new BsonArray(); long unavailable = 0;
        foreach (var id in page.Ids) {
            try {
                var item = await authorization.ReadAsync(actor, async (authority, tx, token) => {
                    var input = PackConversionExecutionPolicy.Copy(new PackConversionInput(location, ruleId, ruleVersion, id, 1));
                    var context = new Context(actor, authority, tx, input);
                    await PeopleAsync(context, token); await PrepareAsync(context, token);
                    var receipt = await Rows(ProcurementStockReceiptBson.Receipts).Find(tx, new BsonDocument("_id", context.SourceLot!["sourceId"])).SingleOrDefaultAsync(token) ?? throw B.Broken();
                    var available = StockBalancePolicy.AvailablePackages(context.Before!.Balance);
                    var result = new BsonDocument { { "sourceBalanceId", id }, { "sourceBalanceRevision", context.Before.Balance.Revision },
                        { "ruleId", ruleId }, { "ruleVersion", ruleVersion }, { "catalog", context.Catalogue! },
                        { "receiptReference", receipt["input"]["receiptReference"] }, { "lotReference", receipt["input"]["lotReference"] },
                        { "lotId", context.SourceLot["_id"] }, { "expiresOn", context.SourceLot["expiresOn"] },
                        { "availableSourcePackages", available }, { "remainingPackages", context.SourceValue!["remainingPackages"] },
                        { "remainingValue", context.SourceValue["remainingValue"] }, { "maximumSteps", Math.Min(available / context.RuleInput!.SourcePackagesPerStep, DomainValues.MaximumLineQuantity) },
                        { "policy", LotValuePolicy.ProportionalFloorPolicy } };
                    await FenceAsync(context, true, token); return result;
                }, ct);
                items.Add(item);
            } catch (DomainValidationException error) when (error.Code == "insufficient_available_stock") { unavailable++; }
        }
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            await FenceDiscoveryAsync(actor, authority, location, tx, token);
            return new BsonDocument { { "items", items }, { "unavailableCount", unavailable }, { "nextSourceBalanceId", page.Next is { } next ? new BsonInt64(next) : BsonNull.Value } };
        }, ct);
    }
    /// <summary>Committed owning history, scope applied before paging. Corrupt or
    /// unavailable provenance is refused rather than returned as an empty page.</summary>
    public async Task<BsonDocument> ListAsync(ActorContext actor, GrantScope location, long beforeConversionId = 0, int limit = 25, CancellationToken ct = default) {
        if (beforeConversionId is < 0 or >= long.MaxValue - 2 || limit is < 1 or > 100) throw PackConversionExecutionPolicy.Invalid();
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            DiscoveryAuthority(actor, authority, location);
            var filter = new BsonDocument("input.location", StockCountBson.Scope(location));
            if (beforeConversionId > 0) filter.Add("_id", new BsonDocument("$lt", beforeConversionId));
            var page = await Rows(B.Conversions).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(limit + 1).ToListAsync(token);
            var items = new BsonArray();
            foreach (var raw in page.Take(limit)) {
                var input = B.Input(raw["input"]);
                if (input.Location != location) throw B.Broken();
                var context = new Context(actor, authority, tx, input); Current(context);
                var row = await ConversionAsync(tx, raw["_id"].AsInt64, token);
                await PeopleAsync(context, token); await FenceAsync(context, false, token);
                items.Add(await ExecutionLabelsAsync(tx, row, token));
            }
            await FenceDiscoveryAsync(actor, authority, location, tx, token);
            return new BsonDocument { { "items", items }, { "nextConversionId", page.Count > limit ? page[limit - 1]["_id"] : BsonNull.Value } };
        }, ct);
    }
    private async Task<BsonDocument> ExecutionLabelsAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        var receipt = await Rows(ProcurementStockReceiptBson.Receipts).Find(tx, new BsonDocument("_id", row["sourceLot"]["sourceId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var view = new BsonDocument { { "conversion", row }, { "receiptReference", receipt["input"]["receiptReference"] },
            { "sourceLotReference", receipt["input"]["lotReference"] } };
        return view;
    }
    public Task<BsonDocument?> ReadExecutionAsync(ActorContext actor, long id, CancellationToken ct = default) =>
        ReadConversionAsync(actor, id, true, ct);
}
