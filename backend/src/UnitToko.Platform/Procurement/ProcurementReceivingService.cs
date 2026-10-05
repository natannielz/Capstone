using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementReceivingBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

/// <summary>Records external notice facts and physical container custody; no item, stock or financial effect.</summary>
public sealed partial class ProcurementReceivingService
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly IdAllocator ids;
    private readonly AuthorizationStore authorization;
    private readonly PurchaseOrderService orders;
    private readonly ProcurementRequestService requests;
    private readonly ProcurementReceivingRecords records;
    public ProcurementReceivingService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, CatalogAuthoringService catalog,
        PurchaseOrderService orders, ProcurementRequestService requests) {
        this.database = database; this.clock = clock; this.ids = ids; this.orders = orders; this.requests = requests;
        authorization = new(database, unit, clock); records = new(database, clock, catalog, orders);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string permission, ProcurementLocation location) {
        internal ActorContext Actor = actor;
        internal AuthorizationSnapshot Authority = authority;
        internal IClientSessionHandle Tx = tx;
        internal string Permission = permission;
        internal ProcurementLocation Location = location;
        internal ResourceScope Resource => new(FulfillmentOrganizationId: Location.OrganizationId, ShopId: Location.ShopId, WarehouseId: Location.WarehouseId, ChannelId: Location.ChannelId);
        internal PurchaseOrderReceivingCapture? Order;
        internal BsonDocument? Notice;
        internal ProcurementContainerHistory? History;
        internal IReadOnlyList<HeldReceiptPoolCapture> InboundCaptures = [];
        internal DateTimeOffset? SessionExpiry;
    }
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot authority, string permission) {
        var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query); return query;
    }
    private AuthorizationQuery[] Require(Context context) => new[] { Permissions.ProcurementRead, context.Permission }.Distinct().Select(permission => {
        var query = Query(context.Actor, context.Authority, permission);
        if (!ScopePolicy.IsResourceActive(context.Resource, context.Authority.Directory) || !query.Matches(context.Resource)) throw new AuthorizationDeniedException("scope_denied"); return query;
    }).ToArray();
    private void Current(Context context) {
        _ = Require(context); context.Order?.RequireCurrent(); PurchaseOrderService.RequireApprovedInboundCurrent(context.InboundCaptures);
        if (!context.Tx.IsInTransaction || !ReferenceEquals(context.Tx.Client, database.Client)) throw B.Broken();
        if (context.SessionExpiry is { } expiry && clock.UtcNow >= expiry) throw new AuthorizationDeniedException("stale_or_inactive_session");
    }
    private async Task StorageAsync(CancellationToken ct) {
        await orders.RequireReceivingStorageAsync(ct);
        foreach (var name in ProcurementReceivingSchemas.Collections) {
            await InventoryRepository.RequireStorageAsync(database, name, ProcurementReceivingSchemas.Validator(name), ProcurementReceivingSchemas.Keys(name), ct);
            var indexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression"))) throw B.Broken();
        }
    }
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata); ProcurementReceivingRules.Text(metadata.Reason, 1000); ProcurementReceivingRules.Text(metadata.CorrelationId, 128);
        ProcurementReceivingRules.Text(metadata.IdempotencyKey, 128);
        if (metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw ProcurementReceivingRules.Invalid();
    }
    private static void Binding(string value) { if (value is not { Length: 64 } || value.Any(c => !char.IsAsciiHexDigitLower(c))) throw ProcurementReceivingRules.Invalid(); }
    private static void Revision(long id, long version) { if (id <= 0 || version is < 0 or >= long.MaxValue - 1) throw ProcurementReceivingRules.Invalid(); }
    private async Task<Context> OrderAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ProcurementLocation location, long id, long version, long approval, string permission, CancellationToken ct) {
        ProcurementRequestRules.Location(location); var context = new Context(actor, authority, tx, permission, location); _ = Require(context);
        context.Order = await orders.CaptureReceivingAsync(actor, authority, tx, id, version, approval, permission, ct);
        context.Order.RequireContext(orders, actor, authority, tx); ProcurementReceivingRecords.Equal(context.Order.Source["order"]["location"], R.Location(location));
        if (permission is Permissions.ProcurementPrepare or Permissions.ProcurementContainerReceive)
            context.InboundCaptures = await orders.CaptureApprovedInboundAsync(actor, authority, tx, context.Order.Source, ct);
        Current(context); return context;
    }
    private async Task<Context> NoticeAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long id, string permission, CancellationToken ct) {
        if (id <= 0) throw ProcurementReceivingRules.Invalid();
        var raw = await Rows(B.Notices).Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        var input = B.NoticeInput(raw["input"]);
        var context = await OrderAsync(actor, authority, tx, input.Location, input.OrderId, input.OrderVersion, input.OrderApprovalId, permission, ct);
        context.Notice = await records.NoticeAsync(tx, id, ct, context.Order); context.History = await records.HistoryAsync(tx, context.Notice, ct); return context;
    }
    private async Task FenceAsync(Context context, CancellationToken ct) {
        Current(context); if (context.Order is { } order) await order.FenceAsync(ct); await PurchaseOrderService.FenceApprovedInboundAsync(context.InboundCaptures, ct);
        if (context.Notice is { } notice && (await Rows(B.Notices).UpdateOneAsync(context.Tx, notice, Builders<BsonDocument>.Update.Set("version", notice["version"]), cancellationToken: ct)).MatchedCount != 1) throw B.Changed();
        if (context.History is { } history) {
            if (history.Head["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(B.Heads).UpdateOneAsync(context.Tx, history.Head,
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
            history.Head["fence"] = history.Head["fence"].AsInt64 + 1;
        }
        var clauses = Require(context).SelectMany(query => query.Clauses.Where(clause => clause.Matches(context.Resource))).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        context.SessionExpiry = await SessionAsync(context.Actor, context.Tx, ct); Current(context); ct.ThrowIfCancellationRequested();
    }
    private async Task<DateTimeOffset?> SessionAsync(ActorContext actor, IClientSessionHandle tx, CancellationToken ct) {
        if (actor.Session is not { } session) return null;
        var live = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct)
            ?? throw new AuthorizationDeniedException("stale_or_inactive_session");
        return new DateTimeOffset(new[] { live["idleExpiresAt"].ToUniversalTime(), live["absoluteExpiresAt"].ToUniversalTime() }.Min());
    }
    private async Task FenceQueryAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string permission, CancellationToken ct) {
        var clauses = new[] { Permissions.ProcurementRead, permission }.Distinct().SelectMany(value => Query(actor, authority, value).Clauses).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        var deadline = await SessionAsync(actor, tx, ct); foreach (var value in new[] { Permissions.ProcurementRead, permission }) _ = Query(actor, authority, value);
        if (deadline is { } expiry && clock.UtcNow >= expiry) throw new AuthorizationDeniedException("stale_or_inactive_session"); ct.ThrowIfCancellationRequested();
    }
    private CommandAuthorization Command(Context context) => new(ScopePolicy.Authorize(context.Actor, context.Authority, context.Permission, context.Resource, clock.UtcNow),
        ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory), context.Permission, new(context.Resource));
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", row["operation"] },
            { "sourceKey", B.EffectKey(row) }, { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "location", row["location"] }, { "payload", B.Result(row) },
            { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
    private async Task<BsonDocument> HeaderAsync(Context context, string operation, object body, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct) => new() {
        { "_id", (await ids.NextAsync(operation == B.NoticeOperation ? B.Notices : B.Intakes, ct)).Value }, { "schemaVersion", 1 }, { "operation", operation },
        { "actorId", context.Actor.UserId }, { "actorGrantVersion", context.Actor.GrantVersion }, { "people", new BsonArray { ReceivingAccountBson.Person(context.Order!.Person) } },
        { "location", R.Location(context.Location) }, { "recordedAt", Now.UtcDateTime }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason) },
        { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", (await ids.NextAsync("auditEvents", ct)).Value }, { "binding", binding } };
}
