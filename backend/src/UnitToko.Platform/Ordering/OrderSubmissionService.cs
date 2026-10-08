using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Organizations;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;

namespace UnitToko.Platform.Ordering;

public sealed record OrderCheckoutReview(OrderDraftInput Input, OrderCatalogQuote Catalog, string Review, DateTimeOffset CalculatedAt,
    Money Tax, Money Charges, Money Total, OrderSpendingState SpendingState);
public sealed record SubmittedOrderView(LongId Id, long Revision, ImmutableArray<LongId> SuborderIds, DateTimeOffset RecordedAt,
    string InitialSpendingState, OrderDraftInput Input, OrderCatalogQuote Catalog, Money Tax, Money Charges, Money Total, OrderApprovalProgress Progress, string BuyerName);

/// <summary>Ordering owns one atomic submission and invokes the Inventory writer. No default commercial
/// or stock authority exists; production construction stays closed until the owning adapter is supplied.</summary>
public sealed partial class OrderSubmissionService
{
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly IClock clock;
    private readonly IdAllocator ids;
    private readonly AuthorizationStore authorization;
    private readonly OrderQuoteService authority;
    private readonly CatalogDefinitionQueryService catalog;
    private readonly IOrderAdmissionReader? admissionReader;
    private readonly ShipmentDispatchValueRecords.HistoricalRead? stockSourceHistory;
    private readonly ShipmentDispatchService.LiveValueRead? stockSourceLiveRead;
    internal ShipmentDispatchValueRecords.HistoricalRead? StockSourceHistory => stockSourceHistory;
    internal ShipmentDispatchService.LiveValueRead? StockSourceLiveRead => stockSourceLiveRead;
    internal IClock StockSourceClock => clock;
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    public OrderSubmissionService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
        : this(database, unit, ids, clock, null) { }
    /// <summary>Constructs the actual approved buyer, commercial, postal and finite-stock admission owner.</summary>
    public static OrderSubmissionService CreateWithApprovedAdmission(IMongoDatabase database, MongoUnitOfWork unit,
        IdAllocator ids, IClock clock, OrganizationMasterDataService organizations) =>
        new(database, unit, ids, clock, new ApprovedOrderAdmissionReader(database, unit, ids, clock, organizations));
    internal OrderSubmissionService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, IOrderAdmissionReader? reader,
        ShipmentDispatchValueRecords.HistoricalRead? history = null, ShipmentDispatchService.LiveValueRead? liveRead = null)
    {
        this.database = database; this.unit = unit; this.clock = clock; this.ids = ids; stockSourceHistory = history; stockSourceLiveRead = liveRead;
        if (reader is not null && (!ReferenceEquals(reader.Database.Client, database.Client) || !reader.Database.DatabaseNamespace.Equals(database.DatabaseNamespace)))
            throw new ArgumentException("Order authority must use the same database and transaction", nameof(reader));
        admissionReader = reader; authorization = new(database, unit, clock); authority = new(database, unit, clock); catalog = new(database, authorization, clock);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static object Body(OrderDraftInput input, string review) => new { input, review };
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata); InventoryLedger.RequireText(metadata.Reason, 1000); InventoryLedger.RequireText(metadata.CorrelationId, 128);
        if (metadata.IdempotencyKey.Length > 128 || metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')))
            throw OrderAdmissionPolicy.Invalid("invalid_order_request_key");
    }
    private async Task StorageAsync(CancellationToken ct) {
        if (!await new CollectionMigrations(database, clock).IsCurrentAsync(ct)) throw OrderAdmissionPolicy.Invalid("order_storage_unavailable");
        try {
            foreach (var name in OrderSubmissionSchemas.Collections)
                await InventoryRepository.RequireStorageAsync(database, name, OrderSubmissionSchemas.Validator(name), OrderSubmissionSchemas.Key(name), ct);
        } catch (DomainValidationException error) when (error.Code == "inventory_storage_unavailable") { throw OrderAdmissionPolicy.Invalid("order_storage_unavailable"); }
    }
    private async Task<OrderCatalogQuote> QuoteAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx, OrderDraftInput input, CancellationToken ct) {
        authority.Require(actor, snapshot, input.Context);
        var facts = await catalog.ReadSelectedAsync(actor, snapshot, tx, input.Context.Scope, input.Selections.Select(line => line.SkuId.Value).ToImmutableArray(),
            token => authority.FenceAsync(actor, snapshot, tx, [input.Context], token), ct);
        return OrderQuotePolicy.Create(input.Selections, facts.Select(value => new OrderCatalogFact(value.Family, value.Sku, value.Definition, value.CurrentPrice)).ToImmutableArray());
    }
    private async Task<OrderAdmission> AdmitAsync(ActorContext actor, IClientSessionHandle tx, OrderDraftInput input, OrderCatalogQuote quote,
        CancellationToken ct, bool preview = false) {
        var reader = admissionReader ?? throw OrderAdmissionPolicy.Invalid();
        var value = preview && reader is ApprovedOrderAdmissionReader approved
            ? await approved.ReadForPreviewAsync(tx, actor, input, quote, ct)
            : await reader.ReadAndFenceAsync(tx, actor, input, quote, ct);
        OrderAdmissionPolicy.Validate(input, quote, value);
        return value with { Stock = value.Stock.ToArray().ToImmutableArray() };
    }
    public Task<OrderCheckoutReview> PreviewAsync(ActorContext actor, OrderDraftInput input, CancellationToken ct = default) {
        input = OrderDraftPolicy.Copy(input);
        return authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var quote = await QuoteAsync(actor, snapshot, tx, input, token); var admission = await AdmitAsync(actor, tx, input, quote, token, preview: true);
            await admissionReader!.CompleteAsync(tx, actor, admission, token);
            await authority.FenceAsync(actor, snapshot, tx, [input.Context], token);
            return new OrderCheckoutReview(input, quote, B.Review(input, quote, admission.Terms), clock.UtcNow, admission.Terms.Tax, admission.Terms.Charges,
                admission.Terms.Total, OrderSpendingPolicy.Initial(admission.Terms.Spending, admission.Terms.Total));
        }, ct);
    }
    public async Task<BsonDocument> SubmitAsync(ActorContext actor, OrderDraftInput input, string review, AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        input = OrderDraftPolicy.Copy(input); Metadata(metadata);
        if (review is null || review.Length != 64 || review.Any(c => !char.IsAsciiHexDigitLower(c))) throw OrderAdmissionPolicy.Invalid("invalid_order_review");
        await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Orders, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = Body(input, review); var hash = AuthorizationStore.CommandHash(body, metadata.Reason);
        AuthorizationSnapshot current = null!; OrderAdmission? admitted = null;
        return await authorization.ExecuteCommandAsync(actor, B.Operation, metadata, body, async (snapshot, tx, token) => {
            current = snapshot; admitted = null; authority.Require(actor, snapshot, input.Context);
            var original = await Rows(B.Orders).Find(tx, F.Eq("createdBy", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).Limit(2).ToListAsync(token);
            if (original.Count > 1) throw Invalid();
            // A deleted receipt must never let an old command reserve a second order.
            if (original.Count == 1) _ = await GraphAsync(tx, actor, original[0]["_id"].AsInt64, token);
            var resource = input.Context.Resource(actor.UserId);
            return new(ScopePolicy.Authorize(actor, snapshot, Permissions.OrderSubmit, resource, clock.UtcNow),
                ScopePolicy.RequiredNodes(resource, snapshot.Directory), Permissions.OrderSubmit, new(resource));
        }, async (tx, token) => {
            var quote = await QuoteAsync(actor, current, tx, input, token); admitted = await AdmitAsync(actor, tx, input, quote, token);
            if (B.Review(input, quote, admitted.Terms) != review) throw new PersistenceConflictException("order_review_changed", "Review the current branch, delivery and commercial terms");
            var state = OrderSpendingPolicy.Initial(admitted.Terms.Spending, admitted.Terms.Total);
            var groups = OrderFulfillmentPolicy.Split(quote); var suborders = new List<BsonDocument>(); var groupBySku = new Dictionary<long, long>();
            foreach (var group in groups) {
                var subId = (await ids.NextAsync(B.Suborders, token)).Value;
                foreach (var line in group.Lines) groupBySku.Add(line.Definition.SkuId.Value, subId);
                suborders.Add(new() { { "_id", subId }, { "schemaVersion", 1 }, { "kind", "ordering.suborder" }, { "orderId", id }, { "owner", CatalogRepository.Owner(group.Owner) },
                    { "skuIds", new BsonArray(group.Lines.Select(line => new BsonInt64(line.Definition.SkuId.Value))) }, { "revision", 1L }, { "fence", 0L },
                    { "status", state == OrderSpendingState.Pending ? "awaitingSpending" : "awaitingAcceptance" }, { "catalogSubtotal", new BsonDecimal128(group.CatalogSubtotal.Value) },
                    { "buyingOrganizationId", input.Context.OrganizationId.Value }, { "branchId", input.Context.BranchId.Value },
                    { "divisionId", input.Context.DivisionId.HasValue ? new BsonInt64(input.Context.DivisionId.Value.Value) : BsonNull.Value },
                    { "createdBy", actor.UserId }, { "fulfillmentOrganizationId", group.Owner.OrganizationId!.Value }, { "shopId", group.Owner.ShopId!.Value },
                    { "warehouseId", group.Owner.WarehouseId.HasValue ? new BsonInt64(group.Owner.WarehouseId.Value) : BsonNull.Value }, { "channelId", group.Owner.ChannelId!.Value } });
            }
            var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "kind", "ordering.order" }, { "revision", 1L }, { "fence", 0L },
                { "createdBy", actor.UserId }, { "buyingOrganizationId", input.Context.OrganizationId.Value }, { "branchId", input.Context.BranchId.Value },
                { "divisionId", input.Context.DivisionId.HasValue ? new BsonInt64(input.Context.DivisionId.Value.Value) : BsonNull.Value },
                { "input", OrderDraftBson.Input(input) }, { "quote", B.Quote(quote) }, { "terms", B.Terms(admitted.Terms) },
                { "allocations", new BsonArray(admitted.Stock.Select(B.Allocation)) }, { "suborderIds", new BsonArray(suborders.Select(item => item["_id"])) },
                { "initialSpendingState", state.ToString() }, { "orderedAt", now.UtcDateTime }, { "review", review }, { "bodyHash", hash }, { "requestKey", metadata.IdempotencyKey },
                { "actorGrantVersion", actor.GrantVersion }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId } };
            row["snapshotHash"] = B.SnapshotHash(row);
            await Rows(B.Orders).InsertOneAsync(tx, row, cancellationToken: token);
            await Rows(B.Suborders).InsertManyAsync(tx, suborders, cancellationToken: token);
            foreach (var allocation in admitted.Stock) {
                var reservation = new BsonDocument { { "_id", (await ids.NextAsync(B.Reservations, token)).Value }, { "schemaVersion", 1 }, { "kind", "ordering.reservation" },
                    { "orderId", id }, { "suborderId", groupBySku[allocation.SkuId] }, { "allocation", B.Allocation(allocation) },
                    { "remaining", allocation.Change.Packages }, { "dispatched", 0L }, { "released", 0L }, { "revision", 1L }, { "fence", 0L } };
                await Rows(B.Reservations).InsertOneAsync(tx, reservation, cancellationToken: token);
            }
            var stock = new InventoryLedger(database, clock, [new OrderReservationSourceReader(database, async fenceToken => {
                await admissionReader!.CompleteAsync(tx, actor, admitted, fenceToken);
                await authority.FenceAsync(actor, current, tx, [input.Context], fenceToken);
            })]);
            foreach (var pool in admitted.Stock.Select(item => item.Pool.Id).Distinct().Order())
                await stock.ApplyAsync(tx, actor, new("ordering.order", id, "reserve", pool), 1, new(metadata.Reason, metadata.CorrelationId), token);
            await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: token);
            await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
                { "sourceKey", "ordering.order.submitted:" + id }, { "kind", "ordering.order.submitted" }, { "payload", B.Result(row) },
                { "createdAt", now.UtcDateTime }, { "dueAt", now.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            await RecordStockSourceTransitionAsync(tx, "submission", id, token);
            return B.Result(row);
        }, ct, async (snapshot, tx, token) => {
            var receipt = await ReceiptAsync(actor, metadata.IdempotencyKey, tx, token) ?? throw Invalid();
            var row = await GraphAsync(tx, actor, receipt["result"]["orderId"].AsInt64, token);
            if (OrderDraftBson.Context(row["input"]["context"].AsBsonDocument) != input.Context) throw Invalid();
            await TouchAsync(tx, row, token);
            if (admitted is not null) {
                await admissionReader!.CompleteAsync(tx, actor, admitted, token);
                // A changed price/package/entitlement after stock work aborts every effect, including the receipt.
                var quote = await QuoteAsync(actor, snapshot, tx, input, token);
                if (B.Review(input, quote, admitted.Terms) != review) throw new PersistenceConflictException("order_review_changed", "Review the current terms");
            }
            await authority.FenceAsync(actor, snapshot, tx, [input.Context], token);
        });
    }
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor, string key, CancellationToken ct = default) {
        Metadata(new(key, "Recover original order", "order-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, Permissions.OrderSubmit, clock.UtcNow));
            var receipt = await ReceiptAsync(actor, key, tx, token);
            if (receipt is null) { await authority.FenceAsync(actor, snapshot, tx, [], token); return null; }
            var row = await GraphAsync(tx, actor, receipt["result"]["orderId"].AsInt64, token);
            if (row["requestKey"] != key || receipt["bodyHash"] != row["bodyHash"] || !CatalogSchemas.SemanticEquals(receipt["result"], B.Result(row))) throw Invalid();
            await TouchAsync(tx, row, token); await authority.FenceAsync(actor, snapshot, tx, [OrderDraftBson.Context(row["input"]["context"].AsBsonDocument)], token);
            return B.Result(row);
        }, ct);
    }
    public async Task<SubmittedOrderView> ReadOwnAsync(ActorContext actor, long id, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var query = ScopePolicy.Compile(actor, snapshot, Permissions.OrderRead, clock.UtcNow); AuthorizationStore.Require(query);
            if (!await Rows(B.Orders).Find(tx, ScopeQuery.Create(query) & F.Eq("_id", id) & F.Eq("createdBy", actor.UserId)).AnyAsync(token)) throw Invalid();
            var row = await GraphAsync(tx, actor, id, token); await TouchAsync(tx, row, token);
            var progress = await DecisionGraphAsync(tx, row, token);
            await authority.FenceAsync(actor, snapshot, tx, [OrderDraftBson.Context(row["input"]["context"].AsBsonDocument)], token, Permissions.OrderRead); return View(row, progress);
        }, ct);
    }
    public async Task<(ImmutableArray<SubmittedOrderView> Items, bool HasMore)> ListOwnAsync(ActorContext actor, OrderBuyingContext? context = null, long? beforeId = null, CancellationToken ct = default) {
        context?.Validate(); if (beforeId is <= 0) throw OrderAdmissionPolicy.Invalid("invalid_order_page"); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var query = ScopePolicy.Compile(actor, snapshot, Permissions.OrderRead, clock.UtcNow); AuthorizationStore.Require(query);
            var filter = ScopeQuery.Create(query) & F.Eq("createdBy", actor.UserId);
            if (context is not null) {
                authority.Require(actor, snapshot, context, Permissions.OrderRead);
                filter &= F.Eq("buyingOrganizationId", context.OrganizationId.Value) & F.Eq("branchId", context.BranchId.Value) &
                    F.Eq<BsonValue>("divisionId", context.DivisionId.HasValue ? new BsonInt64(context.DivisionId.Value.Value) : BsonNull.Value);
            }
            if (beforeId.HasValue) filter &= F.Lt("_id", beforeId.Value);
            var rows = await Rows(B.Orders).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var result = ImmutableArray.CreateBuilder<SubmittedOrderView>();
            foreach (var row in rows.Take(25)) { var valid = await GraphAsync(tx, actor, row["_id"].AsInt64, token); await TouchAsync(tx, valid, token); result.Add(View(valid, await DecisionGraphAsync(tx, valid, token))); }
            // The extra row influences HasMore, so its authority must be fenced too.
            var contexts = rows.Select(row => OrderDraftBson.Context(row["input"]["context"].AsBsonDocument)).Concat(context is null ? [] : new[] { context }).Distinct().ToArray();
            await authority.FenceAsync(actor, snapshot, tx, contexts, token, Permissions.OrderRead); return (result.ToImmutable(), rows.Count > 25);
        }, ct);
    }
    private Task<BsonDocument?> ReceiptAsync(ActorContext actor, string key, IClientSessionHandle tx, CancellationToken ct) =>
        Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", B.Operation) & F.Eq("key", key)).SingleOrDefaultAsync(ct)!;
    private async Task<BsonDocument> GraphAsync(IClientSessionHandle tx, ActorContext? actor, long id, CancellationToken ct, bool requireOwner = true) =>
        (await GraphWithProgressAsync(tx, actor, id, ct, requireOwner)).Order;
    private async Task<(BsonDocument Order, OrderApprovalProgress Progress)> GraphWithProgressAsync(IClientSessionHandle tx,
        ActorContext? actor, long id, CancellationToken ct, bool requireOwner = true) {
        var filter = F.Eq("_id", id) & OrderSubmissionSchemas.Validator(B.Orders);
        if (requireOwner) filter &= F.Eq("createdBy", (actor ?? throw Invalid()).UserId);
        var row = await Rows(B.Orders).Find(tx, filter).SingleOrDefaultAsync(ct) ?? throw Invalid();
        var input = OrderDraftBson.Input(row["input"].AsBsonDocument); var quote = B.Quote(row["quote"].AsBsonDocument);
        _ = OrderFulfillmentPolicy.Split(quote);
        if (B.SnapshotHash(row) != row["snapshotHash"].AsString || AuthorizationStore.CommandHash(Body(input, row["review"].AsString), row["reason"].AsString) != row["bodyHash"].AsString ||
            row["buyingOrganizationId"] != input.Context.OrganizationId.Value || row["branchId"] != input.Context.BranchId.Value || row["divisionId"] != row["input"]["context"]["divisionId"]) throw Invalid();
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", row["createdBy"]) & F.Eq("operation", B.Operation) & F.Eq("key", row["requestKey"])).SingleOrDefaultAsync(ct);
        if (audit is null || !CatalogSchemas.SemanticEquals(audit, B.Audit(row)) || receipt is null || receipt.GetValue("bodyHash", BsonNull.Value) != row["bodyHash"] ||
            !receipt.TryGetValue("result", out var result) || !CatalogSchemas.SemanticEquals(result, B.Result(row))) throw Invalid();
        await ChildrenAsync(tx, row, quote, ct);
        return (row, await DecisionGraphAsync(tx, row, ct));
    }
    private async Task ChildrenAsync(IClientSessionHandle tx, BsonDocument order, OrderCatalogQuote quote, CancellationToken ct) {
        var id = order["_id"].AsInt64; var groups = OrderFulfillmentPolicy.Split(quote);
        foreach (var name in new[] { B.Suborders, B.Reservations })
            if (await Rows(name).Find(tx, F.Eq("orderId", id) & new BsonDocument("$nor", new BsonArray { OrderSubmissionSchemas.Validator(name) })).AnyAsync(ct)) throw Invalid();
        var suborders = await Rows(B.Suborders).Find(tx, F.Eq("orderId", id) & OrderSubmissionSchemas.Validator(B.Suborders)).Limit(101).ToListAsync(ct);
        if (suborders.Count != groups.Length || order["suborderIds"].AsBsonArray.Count != groups.Length ||
            suborders.Any(row => !order["suborderIds"].AsBsonArray.Contains(row["_id"]) || row["revision"] != 1L ||
                row["status"] != (order["initialSpendingState"] == "Pending" ? "awaitingSpending" : "awaitingAcceptance"))) throw Invalid();
        foreach (var group in groups) {
            var suborder = suborders.SingleOrDefault(row => CatalogRepository.Owner(row["owner"].AsBsonDocument) == group.Owner) ?? throw Invalid();
            var resource = ReviewResource(order, suborder);
            if (resource.FulfillmentOrganizationId != group.Owner.OrganizationId || resource.ShopId != group.Owner.ShopId ||
                resource.WarehouseId != group.Owner.WarehouseId || resource.ChannelId != group.Owner.ChannelId ||
                suborder["buyingOrganizationId"] != order["buyingOrganizationId"] || suborder["branchId"] != order["branchId"] ||
                suborder["divisionId"] != order["divisionId"] || suborder["createdBy"] != order["createdBy"]) throw Invalid();
            if (suborder["catalogSubtotal"] != new BsonDecimal128(group.CatalogSubtotal.Value) ||
                !suborder["skuIds"].AsBsonArray.SequenceEqual(group.Lines.Select(line => new BsonInt64(line.Definition.SkuId.Value)))) throw Invalid();
        }
        var allocations = order["allocations"].AsBsonArray.Select(item => item.AsBsonDocument).ToArray();
        var reservations = await Rows(B.Reservations).Find(tx, F.Eq("orderId", id) & OrderSubmissionSchemas.Validator(B.Reservations)).Limit(101).ToListAsync(ct);
        if (reservations.Count != allocations.Length) throw Invalid();
        foreach (var allocation in allocations) {
            var reservation = reservations.SingleOrDefault(row => CatalogSchemas.SemanticEquals(row["allocation"], allocation)) ?? throw Invalid();
            var suborder = suborders.Single(row => row["skuIds"].AsBsonArray.Contains(allocation["skuId"]));
            if (reservation["suborderId"] != suborder["_id"]) throw Invalid();
        }
        foreach (var pool in allocations.GroupBy(item => item["poolId"].AsInt64)) {
            var source = new InventorySource("ordering.order", id, "reserve", pool.Key);
            var movement = await Rows("movements").Find(tx, F.Eq("sourceEvent", source.EventKey) & InventoryMovementSchemas.Validator()).SingleOrDefaultAsync(ct) ?? throw Invalid();
            if (movement["actorId"] != order["createdBy"] || movement["actorGrantVersion"] != order["actorGrantVersion"] ||
                movement["sourceRevision"] != 1L || movement["physicalPoolId"] != pool.Key || movement["poolRevision"] != pool.First()["poolRevision"] ||
                movement["source"]["namespace"] != source.Namespace || movement["source"]["documentId"] != id || movement["source"]["eventKind"] != "reserve" || movement["source"]["occurrence"] != pool.Key ||
                movement["legs"].AsBsonArray.Count != pool.Count()) throw Invalid();
            foreach (var allocation in pool) {
                var leg = movement["legs"].AsBsonArray.SingleOrDefault(item => item["balanceId"] == allocation["balanceId"]) ?? throw Invalid();
                var before = InventoryBson.ReadBalance(leg["before"].AsBsonDocument); var after = InventoryBson.ReadBalance(leg["after"].AsBsonDocument);
                var fact = B.Allocation(allocation);
                if (leg["action"] != "Reserve" || leg["packages"] != allocation["packages"] || before.Id != fact.Change.Id || before.PhysicalPoolId != fact.Pool.Id ||
                    before.Balance.Key != fact.Change.Key || before.Balance.Revision != fact.Change.ExpectedRevision || after != before with {
                        Balance = UnitToko.Modules.Inventory.StockBalancePolicy.Reserve(before.Balance, fact.Change.ExpectedRevision, fact.Change.Packages) }) throw Invalid();
            }
        }
    }
    private async Task TouchAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        if (row["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(B.Orders).UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw Invalid();
    }
    private static SubmittedOrderView View(BsonDocument row, OrderApprovalProgress progress) => new(new(row["_id"].AsInt64), row["revision"].AsInt64,
        [.. row["suborderIds"].AsBsonArray.Select(value => new LongId(value.AsInt64))], OrderDraftBson.Time(row["orderedAt"]), row["initialSpendingState"].AsString,
        OrderDraftBson.Input(row["input"].AsBsonDocument), B.Quote(row["quote"].AsBsonDocument), new(Decimal128.ToDecimal(row["terms"]["tax"].AsDecimal128)),
        new(Decimal128.ToDecimal(row["terms"]["charges"].AsDecimal128)), new(Decimal128.ToDecimal(row["terms"]["total"].AsDecimal128)), progress, row["terms"]["buyer"]["name"].AsString);
    private static AuthorizationDeniedException Invalid() => new("order_unavailable");
}
