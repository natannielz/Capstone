using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementStockReceiptBson;
using I = UnitToko.Platform.Procurement.ProcurementInspectionBson;
using Q = UnitToko.Platform.Procurement.ProcurementQuarantineDispositionBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal sealed class ProcurementStockReceiptWriteProof(BsonDocument row, HeldReceiptPoolCapture pool, ActorContext actor,
    AuthorizationSnapshot authority, IClientSessionHandle tx, Action current)
{
    private readonly long transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
    private readonly string hash = B.Hash(row);
    internal BsonDocument Row { get; } = row;
    internal HeldReceiptPoolCapture Pool { get; } = pool;
    internal AuthorizationSnapshot Authority { get; } = authority;
    internal void Require(ActorContext value, IClientSessionHandle session) {
        if (value != actor || Authority.UserId != actor.UserId || Authority.GrantVersion != actor.GrantVersion || !ReferenceEquals(tx, session) || !tx.IsInTransaction || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction || B.Hash(Row) != hash) throw B.Broken(); Pool.RequireCurrent(); current();
    }
}

public sealed partial class ProcurementStockReceiptService
{
    private readonly IMongoDatabase database; private readonly IdAllocator ids; private readonly IClock clock; private readonly AuthorizationStore authorization;
    private readonly ProcurementStockReceiptRecords records; private readonly SupplierClaimRecords claims; private readonly ProcurementQuarantineDisposition dispositions;
    private readonly PurchaseOrderService orders; private readonly ProcurementReceivingService receiving; private readonly StockSourceSetupService sources;
    public ProcurementStockReceiptService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, CatalogAuthoringService catalog,
        PurchaseOrderService orders, ProcurementReceivingService receiving, StockSourceSetupService sources) {
        this.database = database; this.ids = ids; this.clock = clock; this.orders = orders; this.receiving = receiving; this.sources = sources; authorization = new(database, unit, clock);
        records = new(database, unit, clock, catalog, orders, sources); claims = new(database, unit, clock, catalog, orders); dispositions = new(database, claims);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private static StockLocation Location(ProcurementLocation location) => new(new(location.OrganizationId), new(location.ShopId), new(location.WarehouseId), new(location.ChannelId));
    private static ResourceScope Resource(ProcurementLocation location) => new(FulfillmentOrganizationId: location.OrganizationId, ShopId: location.ShopId, WarehouseId: location.WarehouseId, ChannelId: location.ChannelId);
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementLocation location, string permission) {
        internal readonly ActorContext Actor = actor; internal readonly AuthorizationSnapshot Authority = authority; internal readonly IClientSessionHandle Tx = tx;
        internal readonly long Transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; internal readonly ProcurementLocation Location = location; internal readonly string Permission = permission;
        internal PurchaseOrderReceivingCapture? Order; internal HeldReceiptPoolCapture? Pool; internal ProcurementInspectionHistory? History; internal BsonDocument? Inspection, Receipt;
        internal ProcurementQuarantineCapacity? Capacity; internal PersonAuthorityProof? People; internal DateTimeOffset? SessionExpiry;
        internal readonly List<(string Table, BsonDocument Row)> Fences = [];
        internal readonly List<ResourceScope> ExtraResources = [];
    }
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot authority, string permission) { var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query); return query; }
    private AuthorizationQuery[] Require(Context context) => new[] { Permissions.ProcurementRead, context.Permission }.Distinct().Select(permission => {
        var query = Query(context.Actor, context.Authority, permission); var resource = Resource(context.Location);
        if (!ScopePolicy.IsResourceActive(resource, context.Authority.Directory) || !query.Matches(resource)) throw new AuthorizationDeniedException("scope_denied"); return query;
    }).ToArray();
    private void Current(Context context) {
        _ = Require(context); foreach (var resource in context.ExtraResources) if (!ScopePolicy.IsResourceActive(resource, context.Authority.Directory) || !Query(context.Actor, context.Authority, context.Permission).Matches(resource)) throw new AuthorizationDeniedException("scope_denied");
        if (!context.Tx.IsInTransaction || !ReferenceEquals(context.Tx.Client, database.Client) || context.Tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != context.Transaction) throw B.Broken();
        context.Order?.RequireContext(orders, context.Actor, context.Authority, context.Tx); context.Pool?.RequireContext(sources, context.Actor, context.Authority, context.Tx);
        context.People?.RequireCurrent(context.Tx, clock.UtcNow); if (context.SessionExpiry is { } expiry && clock.UtcNow >= expiry) throw new AuthorizationDeniedException("stale_or_inactive_session");
    }
    private async Task StorageAsync(CancellationToken ct) {
        await orders.RequireReceivingStorageAsync(ct); await sources.RequireHeldReceiptStorageAsync(ct); await ProcurementStockReceiptSchemas.RequireDispositionStorageAsync(database, ct);
        foreach (var name in ProcurementStockReceiptSchemas.Collections) await InventoryRepository.RequireStorageAsync(database, name, ProcurementStockReceiptSchemas.Validator(name), ProcurementStockReceiptSchemas.Keys(name), ct);
        await InventoryRepository.RequireStorageAsync(database, "lots", InventoryLotSchemas.Validator(), new BsonDocument("_id", 1), ct);
        await InventoryRepository.RequireStorageAsync(database, "stockBalances", InventorySchemas.BalanceValidator(), InventorySchemas.BalanceKey(), ct);
        await InventoryRepository.RequireStorageAsync(database, "movements", InventoryMovementSchemas.Validator(), InventoryMovementSchemas.Key(), ct);
        foreach (var name in ProcurementInspectionSchemas.Collections) await InventoryRepository.RequireStorageAsync(database, name, ProcurementInspectionSchemas.Validator(name), ProcurementInspectionSchemas.Keys(name), ct);
        foreach (var name in SupplierClaimSchemas.Collections.Where(name => name != Q.PhysicalAllocations)) await InventoryRepository.RequireStorageAsync(database, name, SupplierClaimSchemas.Validator(name), SupplierClaimSchemas.Keys(name), ct);
        foreach (var name in ProcurementReceivingSchemas.Collections) await InventoryRepository.RequireStorageAsync(database, name, ProcurementReceivingSchemas.Validator(name), ProcurementReceivingSchemas.Keys(name), ct);
        foreach (var name in SupplierDocumentSchemas.Collections) await InventoryRepository.RequireStorageAsync(database, name, SupplierDocumentSchemas.Validator(name), SupplierDocumentSchemas.Keys(name), ct);
        foreach (var name in ProcurementStockReceiptSchemas.Collections.Concat(ProcurementInspectionSchemas.Collections).Concat(ProcurementReceivingSchemas.Collections).Concat(SupplierDocumentSchemas.Collections)
            .Concat(SupplierClaimSchemas.Collections.Where(name => name != Q.PhysicalAllocations)).Concat(["lots", "stockBalances", "movements"]).Distinct())
            if ((await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression"))) throw B.Broken();
    }
    private static void AddFence(Context context, string table, BsonDocument row) {
        var prior = context.Fences.SingleOrDefault(value => value.Table == table && value.Row["_id"] == row["_id"]);
        if (prior.Row is not null) { ProcurementStockReceiptRecords.Equal(prior.Row, row); return; } context.Fences.Add((table, row));
    }
    private async Task<Context> OrderAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementLocation location, long orderId, long version, long approval, string permission, CancellationToken ct) {
        var context = new Context(actor, authority, tx, location, permission); _ = Require(context);
        context.Order = await orders.CaptureReceivingAsync(actor, authority, tx, orderId, version, approval, Permissions.ProcurementRead, ct);
        ProcurementStockReceiptRecords.Equal(context.Order.Source["order"]["location"], R.Location(location));
        context.People = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, [actor.UserId], ct);
        if (context.People.Evidence.SingleOrDefault() is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 }) throw new AuthorizationDeniedException("person_evidence_invalid");
        await new PersonAuthorizationFence(database, clock).TouchAsync(tx, context.People, ct); Current(context); return context;
    }
    private async Task InspectionAsync(Context context, long inspectionId, int line, CancellationToken ct) {
        var history = await records.InspectionAsync(context.Tx, inspectionId, ct); ProcurementStockReceiptRecords.Equal(history.Source["notice"]["source"], context.Order!.Source);
        context.History = history; context.Inspection = history.Submissions.SingleOrDefault(row => row["_id"] == inspectionId) ?? throw B.Broken();
        if (line < 1 || line > context.Inspection["input"]["lines"].AsBsonArray.Count) throw B.Changed();
        AddFence(context, ProcurementReceivingBson.Heads, history.CustodyHead); AddFence(context, I.Heads, history.Head ?? throw B.Broken()); foreach (var draft in history.Drafts) AddFence(context, I.Drafts, draft);
        context.Capacity = await dispositions.ReadAsync(context.Tx, SupplierClaimBasis.PhysicalReceived, inspectionId, line, context.Inspection["snapshotHash"].AsString,
            context.Inspection["input"]["lines"][line - 1]["receivedPackages"].AsInt32, R.Location(context.Location), context.Order.Source["order"]["_id"].AsInt64, context.Order.Source["proposal"]["input"]["supplierId"].AsInt64, ct);
        if (context.Capacity.Head is { } head) AddFence(context, Q.PhysicalHeads, head); await dispositions.OrphansAsync(context.Tx, ct); await records.OrphansAsync(context.Tx, ct); Current(context);
    }
    private async Task<Context> PrepareAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementStockReceiptInput input, CancellationToken ct) {
        var context = await OrderAsync(actor, authority, tx, input.Location, input.OrderId, input.OrderVersion, input.OrderApprovalId, Permissions.StockReceive, ct);
        await InspectionAsync(context, input.InspectionId, input.InspectionLineNumber, ct); ProcurementStockReceiptRecords.Review(input, context.Order!.Source, context.Inspection!);
        if (context.Capacity!.Capacity["remainingPackages"].AsInt64 < input.Packages) throw B.Changed("procurement_stock_receipt_capacity_exceeded");
        context.Pool = await sources.CaptureHeldReceiptAsync(actor, authority, tx, Location(input.Location), input.PoolId, input.PoolRevision, Permissions.StockReceive, ct);
        if (await Rows(B.Receipts).Find(tx, new BsonDocument { { "location", R.Location(input.Location) }, { "supplierId", context.Order.Source["proposal"]["input"]["supplierId"] }, { "orderId", input.OrderId }, { "receiptReference", input.ReceiptReference } }).AnyAsync(ct)) throw B.Changed("procurement_stock_receipt_reference_exists");
        Current(context); return context;
    }
    private async Task<Context> ReceiptAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long id, string permission, CancellationToken ct) {
        var route = await Rows(B.Receipts).Find(tx, new BsonDocument("_id", id)).Project(new BsonDocument("input", 1)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable"); var input = B.Input(route["input"]);
        var context = await OrderAsync(actor, authority, tx, input.Location, input.OrderId, input.OrderVersion, input.OrderApprovalId, permission, ct);
        context.Receipt = await records.ReadAsync(tx, id, ct); await InspectionAsync(context, input.InspectionId, input.InspectionLineNumber, ct);
        var pool = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", input.PoolId)).SingleOrDefaultAsync(ct) ?? throw B.Broken(); InventoryGraph.RequirePool(pool); AddFence(context, "inventoryPools", pool);
        context.ExtraResources.Add(Resource(R.Location(pool["location"]))); Current(context);
        return context;
    }
    private async Task FenceAsync(Context context, CancellationToken ct) {
        Current(context); if (context.Order is { } order) await order.FenceAsync(ct); if (context.Pool is { } pool) await pool.FenceAsync(ct);
        foreach (var (table, row) in context.Fences) {
            if (row["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(table).UpdateOneAsync(context.Tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed(); row["fence"] = row["fence"].AsInt64 + 1;
        }
        var resource = Resource(context.Location); var resources = new[] { resource }.Concat(context.ExtraResources).Distinct().ToArray();
        var clauses = Require(context).SelectMany(query => query.Clauses.Where(clause => clause.Matches(resource))).Concat(Query(context.Actor, context.Authority, context.Permission).Clauses.Where(clause => context.ExtraResources.Any(clause.Matches))).Distinct().ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor, resources.SelectMany(value => ScopePolicy.RequiredNodes(value, context.Authority.Directory)).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        if (context.People is { } proof) await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct);
        context.SessionExpiry = await SessionAsync(context.Actor, context.Tx, ct); Current(context); ct.ThrowIfCancellationRequested();
    }
    private async Task<DateTimeOffset?> SessionAsync(ActorContext actor, IClientSessionHandle tx, CancellationToken ct) {
        if (actor.Session is not { } session) return null;
        var row = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("stale_or_inactive_session");
        return new(new[] { row["idleExpiresAt"].ToUniversalTime(), row["absoluteExpiresAt"].ToUniversalTime() }.Min());
    }
    private CommandAuthorization Command(Context context) => new(ScopePolicy.Authorize(context.Actor, context.Authority, Permissions.StockReceive, Resource(context.Location), clock.UtcNow),
        ScopePolicy.RequiredNodes(Resource(context.Location), context.Authority.Directory), Permissions.StockReceive, new(Resource(context.Location)));
    private static void Key(string key) { ProcurementStockReceiptRules.Text(key, 128); if (key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw ProcurementStockReceiptRules.Invalid(); }
    private static void Binding(string value) { if (value is not { Length: 64 } || value.Any(c => !char.IsAsciiHexDigitLower(c))) throw ProcurementStockReceiptRules.Invalid(); }
}
