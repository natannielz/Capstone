using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using R = UnitToko.Platform.Inventory.ReplenishmentRecords;

namespace UnitToko.Platform.Inventory;

/// <summary>Persists explainable observations only. Actual stock, purchase orders and procurement
/// requests remain owned by their existing workflows; advice never submits or approves a purchase.</summary>
public sealed partial class ReplenishmentService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
{
    private readonly AuthorizationStore authorization = new(database, unit, clock);
    private readonly CatalogAuthoringService catalog = new(database, unit, ids, clock);
    private readonly StockSourceSetupService sources = new(database, unit, ids, clock);
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private AuthorizationQuery Require(ActorContext actor, AuthorizationSnapshot authority, GrantScope location)
    {
        StockCountPolicy.Location(location);
        var query = ScopePolicy.Compile(actor, authority, Permissions.StockRead, clock.UtcNow); AuthorizationStore.Require(query);
        var resource = CatalogRepository.Resource(location);
        if (!ScopePolicy.IsResourceActive(resource, authority.Directory) || !query.Matches(resource)) throw new AuthorizationDeniedException("scope_denied");
        return query;
    }
    private async Task FenceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, GrantScope location, CancellationToken ct)
    {
        var query = Require(actor, authority, location); var resource = CatalogRepository.Resource(location);
        var clauses = query.Clauses.Where(clause => clause.Matches(resource)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(ScopePolicy.RequiredNodes(resource, authority.Directory)).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        var current = await authorization.LoadAsync(actor, tx, ct); _ = Require(actor, current, location);
    }
    public Task<ImmutableArray<ReplenishmentPoolChoice>> SourcesAsync(ActorContext actor, GrantScope location, CancellationToken ct = default) =>
        authorization.ReadAsync(actor, async (authority, tx, token) => {
            _ = Require(actor, authority, location);
            await new StockSourceTopologyFence(database).FenceAsync(tx, location.OrganizationId!.Value, token);
            var candidates = await Rows("inventoryPools").Find(tx, F.Eq("location.organizationId", location.OrganizationId.Value) &
                F.Eq("location.shopId", location.ShopId!.Value) & F.Eq("location.warehouseId", location.WarehouseId!.Value))
                .Sort(new BsonDocument("_id", 1)).Limit(257).ToListAsync(token);
            if (candidates.Count > 256) throw R.Invalid();
            var items = ImmutableArray.CreateBuilder<ReplenishmentPoolChoice>();
            foreach (var candidate in candidates) {
                InventoryGraph.RequirePool(candidate);
                if (!Require(actor, authority, location).Matches(CatalogRepository.Resource(InventoryGraph.Location(candidate["location"].AsBsonDocument).ToScope()))) continue;
                var facts = await sources.ReadInventoryCountSourceFactsAsync(actor, authority, tx, candidate["_id"].AsInt64, Permissions.StockRead, token);
                if (!StockSourceSetupService.CountSourceMatches(facts, location)) continue;
                await sources.FenceInventoryCountAuthorityAsync(actor, authority, tx, facts.Resources, Permissions.StockRead, token);
                items.Add(new(facts.Binding.PoolId, facts.Binding.PoolRevision, facts.Binding.Label, facts.Binding.PhysicalKey, facts.Binding.Active));
            }
            await FenceAsync(actor, authority, tx, location, token); return items.ToImmutable();
        }, ct);

    public async Task<ReplenishmentEvaluation> EvaluateAsync(ActorContext actor, ReplenishmentReviewInput input,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        ReplenishmentReviewPolicy.Validate(input); AuthorizationStore.ValidateMetadata(metadata);
        if (!StockCountPolicy.CanonicalText(metadata.Reason, 1000)) throw R.Invalid();
        await R.RequireStorageAsync(database, ct);
        var id = (await ids.NextAsync(R.Collection, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        AuthorizationSnapshot authority = null!; ReplenishmentStockReader.Capture? stock = null; ReplenishmentEvaluation? result = null;
        await authorization.ExecuteCommandAsync(actor, R.Operation, metadata, input,
            async (snapshot, tx, token) => {
                authority = snapshot; stock = null; result = null; _ = Require(actor, snapshot, input.Location);
                var prior = await Rows(R.Collection).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).SingleOrDefaultAsync(token);
                if (prior is not null) _ = await GraphAsync(tx, prior, token); // A lost receipt cannot create a second evaluation.
                var resource = CatalogRepository.Resource(input.Location);
                return new(ScopePolicy.Authorize(actor, snapshot, Permissions.StockRead, resource, clock.UtcNow),
                    ScopePolicy.RequiredNodes(resource, snapshot.Directory), Permissions.StockRead, new(resource));
            }, async (tx, token) => await catalog.WithReplenishmentDefinitionAsync(database, tx, actor, input.Location,
                input.SkuId, input.ExpectedSkuRevision, input.DefinitionId, new("inventory-replenishment", metadata.CorrelationId),
                async (definition, inner) => {
                    stock = await new ReplenishmentStockReader(database, unit, ids, clock).ReadAsync(actor, authority, tx, input, inner);
                    var supply = await new ProcurementReplenishmentSupplyReader(database, unit, ids, clock).ReadAsync(actor, authority, tx,
                        StockSourceSetupService.StockCountLocation(input.Location), input.PoolId, input.SkuId, input.DefinitionId, inner);
                    var row = R.Create(id, auditId, actor, input, StockCountBson.Definition(definition), stock.Stock, supply.CoverageComplete,
                        supply.Supplies, stock.Warnings, new() { { "stock", stock.Provenance }, { "supply", supply.Provenance } }, metadata, clock.UtcNow);
                    result = R.Read(row);
                    await Rows(R.Collection).InsertOneAsync(tx, row, cancellationToken: inner);
                    await Rows("auditEvents").InsertOneAsync(tx, R.Audit(row), cancellationToken: inner);
                    await supply.CompleteAsync(inner); await stock.Complete(inner);
                    return R.Result(row);
                }, token), ct, complete: async (snapshot, tx, token) => {
                    var receipt = await ReceiptAsync(tx, actor.UserId, metadata.IdempotencyKey, token) ?? throw R.Invalid();
                    var row = await Rows(R.Collection).Find(tx, F.Eq("_id", receipt["result"]["evaluationId"])).SingleOrDefaultAsync(token) ?? throw R.Invalid();
                    result = await GraphAsync(tx, row, token);
                    if (row["issuer"] != actor.ProviderIssuer || row["subject"] != actor.ProviderSubject || result.Input != input ||
                        result.RecordedBy != actor.UserId) throw R.Invalid();
                    await FenceAsync(actor, snapshot, tx, input.Location, token);
                }, validateCurrent: () => stock?.RequireCurrent());
        return result ?? throw R.Invalid();
    }
    public async Task<ReplenishmentEvaluation?> RecoverAsync(ActorContext actor, string key, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 128 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw R.Invalid();
        await R.RequireStorageAsync(database, ct);
        return await authorization.ReadAsync<ReplenishmentEvaluation?>(actor, async (authority, tx, token) => {
            var receipt = await ReceiptAsync(tx, actor.UserId, key, token);
            var row = await Rows(R.Collection).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", key)).SingleOrDefaultAsync(token);
            if (receipt is null && row is null) { AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, Permissions.StockRead, clock.UtcNow)); return null; }
            if (receipt is null || row is null) throw R.Invalid();
            var location = R.Input(row["input"]).Location; _ = Require(actor, authority, location);
            if (row["issuer"] != actor.ProviderIssuer || row["subject"] != actor.ProviderSubject) throw new AuthorizationDeniedException("actor_changed");
            var result = await GraphAsync(tx, row, token); await FenceAsync(actor, authority, tx, location, token); return result;
        }, ct);
    }
    public async Task<ReplenishmentEvaluation?> ReadAsync(ActorContext actor, long id, CancellationToken ct = default)
    {
        if (id <= 0) throw R.Invalid(); await R.RequireStorageAsync(database, ct);
        return await authorization.ReadAsync<ReplenishmentEvaluation?>(actor, async (authority, tx, token) => {
            AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, Permissions.StockRead, clock.UtcNow));
            var row = await Rows(R.Collection).Find(tx, F.Eq("_id", id)).SingleOrDefaultAsync(token); if (row is null) return null;
            var location = R.Input(row["input"]).Location; _ = Require(actor, authority, location);
            var result = await GraphAsync(tx, row, token); await FenceAsync(actor, authority, tx, location, token); return result;
        }, ct);
    }
    private async Task<BsonDocument?> ReceiptAsync(IClientSessionHandle tx, long actorId, string key, CancellationToken ct) => await Rows("idempotencyRecords")
        .Find(tx, F.Eq("actorId", actorId) & F.Eq("operation", R.Operation) & F.Eq("key", key)).SingleOrDefaultAsync(ct);
    private async Task<ReplenishmentEvaluation> GraphAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct)
    {
        if (!await Rows(R.Collection).Find(tx, F.Eq("_id", row["_id"]) & ReplenishmentSchemas.Validator()).AnyAsync(ct)) throw R.Invalid();
        var result = R.Read(row);
        if (result.Advice.ObservedAt > clock.UtcNow) throw R.Invalid();
        var receipt = await ReceiptAsync(tx, result.RecordedBy, row["requestKey"].AsString, ct);
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        if (receipt is null || receipt["bodyHash"] != row["bodyHash"] || receipt["result"] != R.Result(row) || audit != R.Audit(row)) throw R.Invalid();
        var definition = await Rows("variantDefinitions").Find(tx, F.Eq("_id", result.Definition.DefinitionId)).SingleOrDefaultAsync(ct);
        if (definition is null || !CatalogSchemas.IsValid("variantDefinitions", definition) || definition["skuId"] != result.Definition.SkuId ||
            definition["version"] != result.Definition.DefinitionVersion || definition["packageLabel"] != result.Definition.PackageLabel ||
            definition["packageKind"] != result.Definition.PackageKind || definition["baseUnitCode"] != result.Definition.BaseUnitCode ||
            definition["baseUnitsPerPackage"] != result.Definition.BaseUnitsPerPackage) throw R.Invalid();
        await sources.RequireHeldReceiptHistoryAsync(tx, row["provenance"]["stock"]["sourceSnapshot"].AsBsonDocument, result.Advice.ObservedAt, ct);
        return result;
    }
}
