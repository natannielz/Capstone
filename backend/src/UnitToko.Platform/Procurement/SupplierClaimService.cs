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
using B = UnitToko.Platform.Procurement.SupplierClaimBson;
using Q = UnitToko.Platform.Procurement.ProcurementQuarantineDispositionBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

/// <summary>Records supplier resolution requests and independent internal decisions. Held goods remain quarantined;
/// shortage quantities are separate observations. No supplier message, stock movement, credit or payment is emitted.</summary>
public sealed partial class SupplierClaimService
{
    private readonly IMongoDatabase database;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly AuthorizationStore authorization;
    private readonly SupplierClaimRecords records;
    private readonly SupplierRecords supplierRecords;
    private readonly ProcurementQuarantineDisposition dispositions;
    private readonly PurchaseOrderService orders;
    private readonly ProcurementReceivingService receiving;
    public SupplierClaimService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        CatalogAuthoringService catalog, PurchaseOrderService orders, ProcurementReceivingService receiving) {
        this.database = database; this.ids = ids; this.clock = clock; this.orders = orders; this.receiving = receiving;
        authorization = new(database, unit, clock); records = new(database, unit, clock, catalog, orders); supplierRecords = new(database, catalog); dispositions = new(database, records);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementLocation location, string permission) {
        internal readonly ActorContext Actor = actor;
        internal readonly AuthorizationSnapshot Authority = authority;
        internal readonly IClientSessionHandle Tx = tx;
        internal readonly long Transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        internal readonly ProcurementLocation Location = location;
        internal readonly string Permission = permission;
        internal ResourceScope Resource => new(FulfillmentOrganizationId: Location.OrganizationId, ShopId: Location.ShopId, WarehouseId: Location.WarehouseId, ChannelId: Location.ChannelId);
        internal PurchaseOrderReceivingCapture? Order;
        internal BsonDocument? Request, Decision;
        internal SupplierClaimSources? Sources;
        internal List<ProcurementQuarantineCapacity> Capacities = [];
        internal readonly List<(string Table, BsonDocument Row)> Fences = [];
        internal PersonAuthorityProof? People;
        internal DateTimeOffset? SessionExpiry;
        internal BsonArray Capacity => new(Capacities.Select(value => value.Capacity));
    }
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot authority, string permission) {
        var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query); return query;
    }
    private AuthorizationQuery[] Require(Context context) => new[] { Permissions.ProcurementRead, context.Permission }.Distinct().Select(permission => {
        var query = Query(context.Actor, context.Authority, permission);
        if (!ScopePolicy.IsResourceActive(context.Resource, context.Authority.Directory) || !query.Matches(context.Resource)) throw new AuthorizationDeniedException("scope_denied"); return query;
    }).ToArray();
    private Context ContextFor(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementLocation location, string permission) {
        ProcurementRequestRules.Location(location); var context = new Context(actor, authority, tx, location, permission); _ = Require(context); return context;
    }
    private void Current(Context context) {
        _ = Require(context); context.People?.RequireCurrent(context.Tx, clock.UtcNow); context.Order?.RequireContext(orders, context.Actor, context.Authority, context.Tx);
        if (!context.Tx.IsInTransaction || !ReferenceEquals(context.Tx.Client, database.Client) || context.Tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != context.Transaction) throw B.Broken();
        if (context.SessionExpiry is { } expiry && clock.UtcNow >= expiry) throw new AuthorizationDeniedException("stale_or_inactive_session");
    }
    private async Task StorageAsync(CancellationToken ct) {
        await orders.RequireReceivingStorageAsync(ct);
        foreach (var (names, validator, keys) in new[] {
            (ProcurementReceivingSchemas.Collections, (Func<string, BsonDocument>)ProcurementReceivingSchemas.Validator, (Func<string, BsonDocument[]>)ProcurementReceivingSchemas.Keys),
            (ProcurementInspectionSchemas.Collections, (Func<string, BsonDocument>)ProcurementInspectionSchemas.Validator, (Func<string, BsonDocument[]>)ProcurementInspectionSchemas.Keys),
            (SupplierDocumentSchemas.Collections, (Func<string, BsonDocument>)SupplierDocumentSchemas.Validator, (Func<string, BsonDocument[]>)SupplierDocumentSchemas.Keys),
            (SupplierClaimSchemas.Collections, (Func<string, BsonDocument>)SupplierClaimSchemas.Validator, (Func<string, BsonDocument[]>)SupplierClaimSchemas.Keys) })
        foreach (var name in names) {
            if (name == Q.PhysicalAllocations) await ProcurementStockReceiptSchemas.RequireDispositionStorageAsync(database, ct);
            else {
                if (SupplierDocumentSchemas.Collections.Contains(name)) await SupplierDocumentService.RequirePredecessorDocumentStorageAsync(database, name, ct);
                else await InventoryRepository.RequireStorageAsync(database, name, validator(name), keys(name), ct);
                if ((await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression"))) throw B.Broken();
            }
        }
    }
    private static void AddFence(Context context, string table, BsonDocument row) {
        var prior = context.Fences.SingleOrDefault(value => value.Table == table && value.Row["_id"] == row["_id"]);
        if (prior.Row is not null) { SupplierClaimRecords.Equal(prior.Row, row); return; } context.Fences.Add((table, row));
    }
    private async Task PeopleAsync(Context context, BsonDocument? request, bool independent, CancellationToken ct) {
        var accounts = independent ? new[] { context.Actor.UserId, request!["actorId"].AsInt64 }.Distinct().Order().ToArray() : [context.Actor.UserId];
        if (independent) {
            var decision = ApprovalPolicy.Evaluate(context.Actor.UserId, Permissions.SupplierClaimApprove, new(1, 1, RequestedBy: request!["actorId"].AsInt64));
            if (!decision.Allowed) throw new AuthorizationDeniedException(decision.Code);
        }
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [.. accounts], ct);
        if (proof.Evidence.Any(person => person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 })) throw new AuthorizationDeniedException("person_evidence_invalid");
        if (request is not null) {
            var original = R.Person(request["people"][0]); var maker = proof.Evidence.SingleOrDefault(person => person.UserId == original.UserId);
            if (maker is not null && maker.PersonId != original.PersonId) throw new AuthorizationDeniedException("person_evidence_invalid");
            if (independent) {
                var decision = PersonApprovalPolicy.Evaluate(context.Actor.UserId, Permissions.SupplierClaimApprove, new(1, 1, RequestedBy: original.UserId), proof.Evidence);
                if (!decision.Allowed) throw new AuthorizationDeniedException(decision.Code);
                if (!PersonIndependencePolicy.AreIndependent(proof.Evidence.Single(person => person.UserId == context.Actor.UserId), original)) throw new AuthorizationDeniedException("independent_person_required");
            }
        }
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct); context.People = proof;
    }
    private async Task<Context> OrderContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementLocation location,
        long orderId, long orderVersion, long approvalId, string permission, CancellationToken ct) {
        var context = ContextFor(actor, authority, tx, location, permission);
        context.Order = await orders.CaptureReceivingAsync(actor, authority, tx, orderId, orderVersion, approvalId, Permissions.ProcurementRead, ct);
        context.Order.RequireContext(orders, actor, authority, tx); SupplierClaimRecords.Equal(context.Order.Source["order"]["location"], R.Location(location));
        var supplierId = context.Order.Source["proposal"]["input"]["supplierId"].AsInt64; var head = await supplierRecords.HeadAsync(tx, supplierId, ct);
        SupplierClaimRecords.Equal(head["owner"]["organizationId"], location.OrganizationId); SupplierClaimRecords.Equal(head["owner"]["shopId"], location.ShopId); SupplierClaimRecords.Equal(head["owner"]["channelId"], location.ChannelId);
        SupplierClaimRecords.Equal(head["subjectReference"], context.Order.Source["proposal"]["supplier"]["subjectReference"]);
        AddFence(context, SupplierBson.Heads, head); await PeopleAsync(context, null, false, ct); Current(context); return context;
    }
    private async Task AddSourcesAsync(Context context, SupplierClaimInput input, CancellationToken ct) {
        context.Sources = await records.ResolveAsync(context.Tx, input, context.Order!.Source, ct); context.Capacities.Clear();
        foreach (var history in context.Sources.Inspections.Values) {
            AddFence(context, ProcurementReceivingBson.Heads, history.CustodyHead); AddFence(context, ProcurementInspectionBson.Heads, history.Head ?? throw B.Broken());
            foreach (var draft in history.Drafts) AddFence(context, ProcurementInspectionBson.Drafts, draft);
        }
        foreach (var history in context.Sources.Documents.Values) {
            AddFence(context, SupplierDocumentBson.Heads, history.Head); AddFence(context, SupplierDocumentBson.Identities, history.Identity); AddFence(context, SupplierDocumentBson.Drafts, history.Draft);
        }
        foreach (var (table, row) in context.Sources.Fences) AddFence(context, table, row);
        for (var i = 0; i < input.Lines.Length; i++) {
            var line = input.Lines[i]; var observed = context.Sources.Lines[i]["inspection"];
            var capacity = await dispositions.ReadAsync(context.Tx, line.Basis, line.InspectionId, line.InspectionLineNumber, observed["hash"].AsString,
                Q.Total(line.Basis, observed["line"]), R.Location(context.Location), input.OrderId, context.Order.Source["proposal"]["input"]["supplierId"].AsInt64, ct);
            context.Capacities.Add(capacity); if (capacity.Head is { } head) AddFence(context, Q.Heads(capacity.Basis), head);
        }
        await dispositions.OrphansAsync(context.Tx, ct); Current(context);
    }
    private async Task<Context> ClaimContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long id, string permission, CancellationToken ct) {
        var route = await Rows(B.Requests).Find(tx, new BsonDocument("_id", id)).Project(new BsonDocument { { "location", 1 }, { "input", 1 } }).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        _ = ContextFor(actor, authority, tx, R.Location(route["location"]), permission); var input = B.Input(route["input"]);
        var context = await OrderContextAsync(actor, authority, tx, input.Location, input.OrderId, input.OrderVersion, input.OrderApprovalId, permission, ct);
        context.Request = await records.RequestAsync(tx, id, ct); context.Decision = await records.DecisionAsync(tx, context.Request, ct);
        await AddSourcesAsync(context, input, ct); await PeopleAsync(context, context.Request, permission == Permissions.SupplierClaimApprove, ct); return context;
    }
    private async Task TouchAsync(Context context, string table, BsonDocument row, CancellationToken ct) {
        if (row["fence"].AsInt64 >= long.MaxValue - 2 || (await Rows(table).UpdateOneAsync(context.Tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        row["fence"] = row["fence"].AsInt64 + 1;
    }
    private async Task FenceAsync(Context context, CancellationToken ct) {
        Current(context); if (context.Order is { } order) await order.FenceAsync(ct);
        foreach (var (table, row) in context.Fences) await TouchAsync(context, table, row, ct);
        var clauses = Require(context).SelectMany(query => query.Clauses.Where(clause => clause.Matches(context.Resource))).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        if (context.People is { } proof) await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct);
        context.SessionExpiry = await SessionAsync(context.Actor, context.Tx, ct); Current(context); ct.ThrowIfCancellationRequested();
    }
    private async Task<DateTimeOffset?> SessionAsync(ActorContext actor, IClientSessionHandle tx, CancellationToken ct) {
        if (actor.Session is not { } session) return null;
        var row = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct)
            ?? throw new AuthorizationDeniedException("stale_or_inactive_session");
        return new DateTimeOffset(new[] { row["idleExpiresAt"].ToUniversalTime(), row["absoluteExpiresAt"].ToUniversalTime() }.Min());
    }
    private async Task FenceQueryAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string permission, CancellationToken ct) {
        var clauses = new[] { Permissions.ProcurementRead, permission }.Distinct().SelectMany(value => Query(actor, authority, value).Clauses).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        var expiry = await SessionAsync(actor, tx, ct); foreach (var value in new[] { Permissions.ProcurementRead, permission }.Distinct()) _ = Query(actor, authority, value);
        if (expiry is { } until && clock.UtcNow >= until) throw new AuthorizationDeniedException("stale_or_inactive_session"); ct.ThrowIfCancellationRequested();
    }
    private CommandAuthorization Command(Context context) {
        var approval = context.Permission == Permissions.SupplierClaimApprove ? new ApprovalRequest(1, 1, RequestedBy: context.Request!["actorId"].AsInt64) : null;
        return new(ScopePolicy.Authorize(context.Actor, context.Authority, context.Permission, context.Resource, clock.UtcNow, approval),
            ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory), context.Permission, new(context.Resource, approval));
    }
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata); SupplierClaimRules.Text(metadata.Reason, 1000); SupplierClaimRules.Text(metadata.CorrelationId, 128); Key(metadata.IdempotencyKey);
    }
    private static void Key(string value) { SupplierClaimRules.Text(value, 128); if (value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw SupplierClaimRules.Invalid(); }
    private static void Binding(string value) { if (value is not { Length: 64 } || value.Any(c => !char.IsAsciiHexDigitLower(c))) throw SupplierClaimRules.Invalid(); }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", row["operation"] }, { "sourceKey", B.EffectKey(row) },
            { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "location", row["location"] }, { "orderId", row["orderId"] }, { "payload", B.Result(row) },
            { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
}
