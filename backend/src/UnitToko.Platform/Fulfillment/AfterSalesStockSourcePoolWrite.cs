using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Billing;

namespace UnitToko.Platform.Fulfillment;
/// <summary>Private-constructor witness for the actual canonical CAS after an authentic fresh
/// physical owner write and completed original command receipt; not caller supplied fence numbers.</summary>
internal sealed class AfterSalesStockSourcePoolWrite
{
    private readonly IMongoDatabase database;
    private readonly IClientSessionHandle session;
    private readonly long transaction;
    private readonly BsonDocument before, after;
    private readonly Action current;
    private bool consumed;
    private AfterSalesStockSourcePoolWrite(IMongoDatabase database, IClientSessionHandle tx, BsonDocument before,
        BsonDocument after, AfterSalesStockSourceEvent source, Action current)
    {
        this.database = database; session = tx; transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        this.before = before.DeepClone().AsBsonDocument; this.after = after.DeepClone().AsBsonDocument;
        Kind = source.Kind; SourceId = source.Id; SourceHash = source.Hash; this.current = current;
    }
    internal string Kind { get; }
    internal long SourceId { get; }
    internal string SourceHash { get; }
    internal long PoolId => before["_id"].AsInt64;
    internal long PoolRevision => before["revision"].AsInt64;
    internal long BeforeFence => before["fence"].AsInt64;
    internal long AfterFence => after["fence"].AsInt64;
    internal BsonDocument Location => before["location"].DeepClone().AsBsonDocument;
    internal static async Task<AfterSalesStockSourcePoolWrite> FenceAsync(IMongoDatabase database, AuthorizationStore authorization,
        IClientSessionHandle tx, ActorContext actor, AuthorizationSnapshot authority, AfterSalesStockSourceEvent source,
        BsonDocument actualBefore, string stockPermission, Action current, IClock clock, CancellationToken ct)
    {
        var repository = new InventoryRepository(database); repository.RequireSession(tx); current();
        var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        var before = actualBefore.DeepClone().AsBsonDocument; InventoryGraph.RequirePool(before);
        if (!source.PoolIds.Contains(before["_id"].AsInt64) || source.Audit["actorId"] != actor.UserId ||
            source.Audit["actorGrantVersion"] != actor.GrantVersion || authority.UserId != actor.UserId || authority.GrantVersion != actor.GrantVersion ||
            source.Kind is not ("request" or "decision" or "receive" or "inspect" or "verifyLot") || before["fence"].AsInt64 >= long.MaxValue - 2) throw AfterSalesStockSourceRecords.Unavailable();
        var causal = source.Kind is "request" or "decision";
        if (stockPermission != (source.Kind switch { "request" => Permissions.CaseRequest, "decision" => Permissions.CaseManage,
            "receive" => Permissions.StockReceive, _ => Permissions.StockCount })) throw AfterSalesStockSourceRecords.Unavailable();
        var resources = causal ? PaymentDraftBson.Scopes(source.Record["case"]["scopes"].AsBsonArray) :
            [CatalogRepository.Resource(InventoryGraph.Location(before["location"].AsBsonDocument).ToScope())];
        if (resources.Length == 0 || causal && resources.Any(resource => resource.FulfillmentOrganizationId != before["location"]["organizationId"].AsInt64))
            throw AfterSalesStockSourceRecords.Unavailable();
        void CanonicalCurrent() {
            current(); var admission = ScopePolicy.Compile(actor, authority, stockPermission, clock.UtcNow); AuthorizationStore.Require(admission);
            if (resources.Any(resource => !admission.Matches(resource) || !ScopePolicy.IsResourceActive(resource, authority.Directory))) throw AfterSalesStockSourceRecords.Unavailable();
        }
        CanonicalCurrent(); var query = ScopePolicy.Compile(actor, authority, stockPermission, clock.UtcNow);
        var clauses = query.Clauses.Where(clause => resources.Any(clause.Matches)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(resource => ScopePolicy.RequiredNodes(resource, authority.Directory))).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        await InventoryRepository.RequireStorageAsync(database, "inventoryPools", StockSourceSetupSchemas.Validator("inventoryPools"), StockSourceSetupSchemas.Key("inventoryPools"), ct);
        var after = before.DeepClone().AsBsonDocument; after["fence"] = checked(before["fence"].AsInt64 + 1);
        if ((await database.GetCollection<BsonDocument>("inventoryPools").UpdateOneAsync(tx, before,
            Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw AfterSalesStockSourceRecords.Changed();
        repository.RequireSession(tx); current();
        if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number) throw AfterSalesStockSourceRecords.Unavailable();
        return new(database, tx, before, after, source, CanonicalCurrent);
    }
    internal async Task RequireAsync(IMongoDatabase value, IClientSessionHandle tx, CancellationToken ct)
    {
        new InventoryRepository(value).RequireSession(tx); current();
        if (consumed || !ReferenceEquals(tx, session) || !ReferenceEquals(value.Client, database.Client) || value.DatabaseNamespace != database.DatabaseNamespace ||
            tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction) throw AfterSalesStockSourceRecords.Unavailable();
        var actual = await value.GetCollection<BsonDocument>("inventoryPools").Find(tx, new BsonDocument("_id", PoolId)).SingleOrDefaultAsync(ct);
        if (actual is null || actual != after) throw AfterSalesStockSourceRecords.Changed();
        new InventoryRepository(value).RequireSession(tx); current();
        if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction) throw AfterSalesStockSourceRecords.Unavailable();
        ct.ThrowIfCancellationRequested();
    }
    internal void Consume() => consumed = true;
    internal void RequireCurrent() {
        new InventoryRepository(database).RequireSession(session); current();
        if (session.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction) throw AfterSalesStockSourceRecords.Unavailable();
    }
}
