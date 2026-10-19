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
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;

namespace UnitToko.Platform.Ordering;

public sealed record OrderReviewView(long OrderId, long? SuborderId, OrderReviewStage Stage, long Revision, string Review,
    OrderBuyingContext Context, string BuyerName, OrderDeliveryRequest Delivery, OrderCatalogQuote Catalog, Money? Tax, Money? Charges, Money? Total,
    OrderSpendingState SpendingState, string? StoreState, bool CanDecide, bool Cancelled, DateTimeOffset OrderedAt);

/// <summary>Immutable decisions over the submitted order. Spending, store acceptance and cancellation
/// have distinct scopes and receipts. Rejection/cancellation release reservations through Inventory;
/// accepted commercial facts and the original submission remain immutable.</summary>
public sealed partial class OrderSubmissionService
{
    private const string Decisions = "orderDecisions";
    private static string ReviewPermission(OrderReviewStage stage) => stage switch {
        OrderReviewStage.Spending => Permissions.OrderSpendApprove, OrderReviewStage.Store => Permissions.OrderAccept,
        OrderReviewStage.Cancellation => Permissions.OrderCancel,
        _ => throw OrderAdmissionPolicy.Invalid("invalid_order_review_stage") };
    internal static string DecisionOperation(OrderReviewStage stage, OrderReviewAction action) => (stage, action) switch {
        (OrderReviewStage.Spending, OrderReviewAction.Approve) => "ordering.order.spending.approve",
        (OrderReviewStage.Spending, OrderReviewAction.Reject) => "ordering.order.spending.reject",
        (OrderReviewStage.Store, OrderReviewAction.Approve) => "ordering.order.store.accept",
        (OrderReviewStage.Store, OrderReviewAction.Reject) => "ordering.order.store.reject",
        (OrderReviewStage.Cancellation, OrderReviewAction.CancelRemainder) => "ordering.order.cancel",
        _ => throw OrderAdmissionPolicy.Invalid("invalid_order_decision") };
    private static object DecisionBody(long orderId, long? suborderId, OrderReviewStage stage, long expectedRevision, string review, OrderReviewAction action) => new { orderId, suborderId, stage, expectedRevision, review, action };
    private static string DecisionReview(BsonDocument order, long? suborderId, OrderReviewStage stage, long revision, long dispatchSequence = 0) {
        var accepted = new BsonDocument {
        { "orderSnapshot", order["snapshotHash"] }, { "suborderId", suborderId.HasValue ? new BsonInt64(suborderId.Value) : BsonNull.Value },
        { "stage", stage.ToString() }, { "revision", revision } };
        // Preserve pre-dispatch review hashes. Any actual dispatch requires a fresh review of the remainder.
        if (dispatchSequence > 0) accepted.Add("dispatchSequence", dispatchSequence);
        return CatalogRepository.Digest(accepted);
    }
    private static ResourceScope ReviewResource(BsonDocument order, BsonDocument? suborder) => new(
        BuyingOrganizationId: order["buyingOrganizationId"].AsInt64, BranchId: order["branchId"].AsInt64,
        DivisionId: order["divisionId"].IsBsonNull ? null : order["divisionId"].AsInt64,
        FulfillmentOrganizationId: suborder?["fulfillmentOrganizationId"].AsInt64, ShopId: suborder?["shopId"].AsInt64,
        WarehouseId: suborder is null || suborder["warehouseId"].IsBsonNull ? null : suborder["warehouseId"].AsInt64,
        ChannelId: suborder?["channelId"].AsInt64, CreatedBy: order["createdBy"].AsInt64);
    private static BsonDocument DecisionResult(BsonDocument row) => new() { { "decisionId", row["_id"] }, { "orderId", row["orderId"] },
        { "suborderId", row["suborderId"] }, { "revision", row["revision"] }, { "stage", row["stage"] },
        { "action", row["action"] }, { "state", row["action"] == "Reject" ? "rejected" : row["action"] == "CancelRemainder" ? "cancelled" : row["stage"] == "Spending" ? "approved" : "accepted" }, { "recordedAt", row["decidedAt"] } };
    private static BsonDocument DecisionAudit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 },
        { "kind", "ordering.order.decided" }, { "actorId", row["decidedBy"] }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "operation", DecisionOperation(Enum.Parse<OrderReviewStage>(row["stage"].AsString), Enum.Parse<OrderReviewAction>(row["action"].AsString)) }, { "requestKey", row["requestKey"] },
        { "snapshotHash", CatalogRepository.Digest(row) }, { "bodyHash", row["bodyHash"] }, { "reason", row["reason"] },
        { "correlationId", row["correlationId"] }, { "createdAt", row["decidedAt"] }, { "result", DecisionResult(row) } };

    private async Task<OrderApprovalProgress> DecisionGraphAsync(IClientSessionHandle tx, BsonDocument order, CancellationToken ct) {
        stockSourceHistory?.Require(database, tx);
        stockSourceLiveRead?.Require(database, clock, tx);
        var id = order["_id"].AsInt64;
        var number = MongoUnitOfWork.OwnsOperation(tx) ? tx.WrappedCoreSession.CurrentTransaction.TransactionNumber : (long?)null;
        var members = number is null ? null : WithoutFences(await GraphMembersAsync(tx, id, ct));
        var orderKey = WithoutFence(order);
        if (number is { } attempt && members is not null && verifiedGraphs.TryGetValue(tx, out var verified)) lock (verified)
            if (verified.TryGetValue(id, out var hit) && hit.Number == attempt && ReferenceEquals(hit.History, stockSourceHistory) &&
                ReferenceEquals(hit.LiveRead, stockSourceLiveRead) && hit.Order == orderKey && hit.Members == members) return hit.Progress;
        var progress = await DecisionGraphCoreAsync(tx, order, ct);
        if (number is { } completed && members is not null && WithoutFences(await GraphMembersAsync(tx, id, ct)) == members) {
            var table = verifiedGraphs.GetValue(tx, _ => []);
            lock (table) table[id] = new(completed, stockSourceHistory, stockSourceLiveRead, orderKey, members, progress);
        }
        return progress;
    }
    // Freshness fences advance on every fenced read and carry no business content.
    private static BsonDocument WithoutFence(BsonDocument row) { var copy = row.DeepClone().AsBsonDocument; copy.Remove("fence"); return copy; }
    private static BsonArray WithoutFences(BsonArray members) =>
        new(members.Select(group => new BsonArray(group.AsBsonArray.Select(row => (BsonValue)WithoutFence(row.AsBsonDocument)))));
    // The replay's carried state, explicit so a verified order-progress checkpoint (OrderProgressSeals) can resume it.
    private sealed class ReplayState {
        internal OrderSpendingState State; internal long Revision = 1; internal long SpendingRequestedBy;
        internal List<LongId> Accepted = [], Rejected = []; internal bool Cancelled; internal DateTimeOffset LastAt;
        internal OrderCatalogQuote Catalog = null!; internal BsonDocument Terms = null!; internal long DispatchSequence;
        internal Dictionary<long, BsonDocument> Reservations = [];
        internal static ReplayState Fresh(BsonDocument order, IEnumerable<BsonDocument> actual) => new() {
            State = Enum.Parse<OrderSpendingState>(order["initialSpendingState"].AsString), SpendingRequestedBy = order["createdBy"].AsInt64,
            LastAt = OrderDraftBson.Time(order["orderedAt"]), Catalog = B.Quote(order["quote"].AsBsonDocument), Terms = order["terms"].DeepClone().AsBsonDocument,
            Reservations = actual.Where(row => order["allocations"].AsBsonArray.Contains(row["allocation"])).ToDictionary(row => row["_id"].AsInt64, row => {
                var expected = row.DeepClone().AsBsonDocument; expected["revision"] = 1L; expected["remaining"] = row["allocation"]["packages"];
                expected["released"] = 0L; expected["dispatched"] = 0L; return expected; }) };
        internal BsonDocument Facts() => new() {
            { "factsVersion", OrderProgressSealSchemas.FactsVersion }, { "revision", Revision }, { "dispatchSequence", DispatchSequence },
            { "spendingState", State.ToString() }, { "spendingRequestedBy", SpendingRequestedBy },
            { "accepted", new BsonArray(Accepted.Select(id => id.Value)) }, { "rejected", new BsonArray(Rejected.Select(id => id.Value)) },
            { "cancelled", Cancelled }, { "lastAt", LastAt.UtcDateTime }, { "catalog", B.Quote(Catalog) }, { "terms", Terms.DeepClone() },
            { "reservations", new BsonArray(Reservations.OrderBy(pair => pair.Key).Select(pair => pair.Value.DeepClone())) } };
        internal static ReplayState FromFacts(BsonDocument facts) => new() {
            Revision = facts["revision"].AsInt64, DispatchSequence = facts["dispatchSequence"].AsInt64,
            State = Enum.Parse<OrderSpendingState>(facts["spendingState"].AsString), SpendingRequestedBy = facts["spendingRequestedBy"].AsInt64,
            Accepted = [.. facts["accepted"].AsBsonArray.Select(id => new LongId(id.AsInt64))], Rejected = [.. facts["rejected"].AsBsonArray.Select(id => new LongId(id.AsInt64))],
            Cancelled = facts["cancelled"].AsBoolean, LastAt = new DateTimeOffset(facts["lastAt"].ToUniversalTime(), TimeSpan.Zero),
            Catalog = B.Quote(facts["catalog"].AsBsonDocument), Terms = facts["terms"].DeepClone().AsBsonDocument,
            Reservations = facts["reservations"].AsBsonArray.ToDictionary(row => row["_id"].AsInt64, row => row.DeepClone().AsBsonDocument) };
    }
    private async Task<OrderApprovalProgress> DecisionGraphCoreAsync(IClientSessionHandle tx, BsonDocument order, CancellationToken ct) {
        stockSourceHistory?.Require(database, tx);
        stockSourceLiveRead?.Require(database, clock, tx);
        if (await Rows(Decisions).Find(tx, F.Eq("orderId", order["_id"]) & new BsonDocument("$nor", new BsonArray { OrderLineageSchemas.OrderValidator(Decisions) })).AnyAsync(ct)) throw Invalid();
        var all = await Rows(Decisions).Find(tx, F.Eq("orderId", order["_id"])).Sort(new BsonDocument("revision", 1)).Limit(103).ToListAsync(ct);
        var substitutions = await Rows(OrderSubstitutionBson.Decisions).Find(tx, F.Eq("orderId", order["_id"])).Limit(103).ToListAsync(ct);
        all.AddRange(substitutions); all = all.OrderBy(row => row["revision"].AsInt64).ToList();
        if (all.Count > 102) throw Invalid();
        var actual = await Rows(B.Reservations).Find(tx, F.Eq("orderId", order["_id"])).Limit(OrderReservationHistoryLimits.MaximumRetained + 1).ToListAsync(ct);
        OrderReservationHistoryLimits.Require(actual);
        // A verified checkpoint binds every decision/substitution at or below its revision and every shipment at or below
        // its dispatch sequence; only newer rows are replayed from its facts. Without one, the full history is replayed.
        var checkpoint = await OrderProgressSeals.VerifyAsync(database, tx, order, ct);
        var s = checkpoint is null ? ReplayState.Fresh(order, actual) : ReplayState.FromFacts(checkpoint);
        if (checkpoint is null) Interlocked.Increment(ref OrderProgressSeals.FullReplays); else Interlocked.Increment(ref OrderProgressSeals.IncrementalReplays);
        var (sealedRevision, sealedSequence) = (s.Revision, s.DispatchSequence);
        var rows = checkpoint is null ? all : all.Where(row => row["revision"].AsInt64 > s.Revision).ToList();
        var reservations = s.Reservations;
        await using var shipments = new ShipmentDispatchRecords(database, clock, stockSourceHistory, stockSourceLiveRead)
            .ReadOrderAsync(tx, order["_id"].AsInt64, ct, checkpoint is null ? 0 : s.DispatchSequence).GetAsyncEnumerator(ct);
        var hasShipment = await shipments.MoveNextAsync();
        async Task ApplyDispatches() {
            while (hasShipment && shipments.Current["decisionRevision"] == s.Revision) {
                var shipment = shipments.Current; var subId = shipment["suborderId"].AsInt64;
                if (s.Cancelled || !OrderSpendingPolicy.CanAcceptAtStore(s.State) || !s.Accepted.Contains(new(subId)) || s.Rejected.Contains(new(subId)) ||
                    shipment["orderSequence"] != checked(s.DispatchSequence + 1) || OrderDraftBson.Time(shipment["recordedAt"]) < s.LastAt) throw Invalid();
                await VerifyDispatchHistoryAsync(tx, order, shipment, reservations, s.Catalog, ct);
                s.DispatchSequence++; s.LastAt = OrderDraftBson.Time(shipment["recordedAt"]); hasShipment = await shipments.MoveNextAsync();
            }
        }
        foreach (var row in rows) {
            await ApplyDispatches();
            if (row["kind"] == "ordering.substitution.decision") {
                var change = await VerifySubstitutionHistoryAsync(tx, order, row, reservations, actual, s.Catalog, s.Terms,
                    s.State, s.Cancelled, [.. s.Rejected], s.Revision, s.DispatchSequence, s.LastAt, ct);
                s.Catalog = change.Catalog; s.Terms = change.Terms; s.State = change.Spending;
                if (row["decision"] == "Accept") { s.Accepted.Clear(); s.SpendingRequestedBy = row["actorId"].AsInt64; }
                s.LastAt = OrderDraftBson.Time(row["recordedAt"]); s.Revision++; continue;
            }
            var stage = Enum.Parse<OrderReviewStage>(row["stage"].AsString); var action = Enum.Parse<OrderReviewAction>(row["action"].AsString);
            var operation = DecisionOperation(stage, action); var subId = row["suborderId"].IsBsonNull ? (long?)null : row["suborderId"].AsInt64;
            if (s.Cancelled || row["expectedRevision"] != s.Revision || row["revision"] != s.Revision + 1 || row["orderSnapshot"] != order["snapshotHash"] ||
                row["review"] != DecisionReview(order, subId, stage, s.Revision, s.DispatchSequence) || OrderDraftBson.Time(row["decidedAt"]) < s.LastAt ||
                row["bodyHash"] != AuthorizationStore.CommandHash(DecisionBody(order["_id"].AsInt64, subId, stage, s.Revision, row["review"].AsString, action), row["reason"].AsString)) throw Invalid();
            if (stage == OrderReviewStage.Spending) {
                if (subId.HasValue || s.State != OrderSpendingState.Pending || row["decidedBy"] == s.SpendingRequestedBy) throw Invalid();
                s.State = action == OrderReviewAction.Approve ? OrderSpendingState.Approved : OrderSpendingState.Rejected;
            } else if (stage == OrderReviewStage.Store) {
                if (!subId.HasValue || !order["suborderIds"].AsBsonArray.Contains(new BsonInt64(subId.Value)) ||
                    !OrderSpendingPolicy.CanAcceptAtStore(s.State) || s.Accepted.Contains(new(subId.Value)) || s.Rejected.Contains(new(subId.Value))) throw Invalid();
                if (action == OrderReviewAction.Approve) s.Accepted.Add(new(subId.Value)); else s.Rejected.Add(new(subId.Value));
            } else {
                if (subId.HasValue || s.State == OrderSpendingState.Rejected || reservations.Values.All(value => value["remaining"] == 0L)) throw Invalid();
                s.Cancelled = true;
            }
            await VerifyReleaseHistoryAsync(tx, row, reservations, action, subId, ct);
            var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"])).SingleOrDefaultAsync(ct);
            var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", row["decidedBy"]) & F.Eq("operation", operation) & F.Eq("key", row["requestKey"])).SingleOrDefaultAsync(ct);
            if (audit is null || !CatalogSchemas.SemanticEquals(audit, DecisionAudit(row)) || receipt is null || receipt["bodyHash"] != row["bodyHash"] ||
                !CatalogSchemas.SemanticEquals(receipt["result"], DecisionResult(row))) throw Invalid();
            if (stage == OrderReviewStage.Spending && PersonApprovalReceipt.Read(receipt, Permissions.OrderSpendApprove) != new ApprovalRequest(1, 1, RequestedBy: s.SpendingRequestedBy)) throw Invalid();
            if (stage != OrderReviewStage.Spending && receipt.Contains(PersonApprovalReceipt.Field)) throw Invalid();
            s.LastAt = OrderDraftBson.Time(row["decidedAt"]); s.Revision++;
        }
        await ApplyDispatches(); if (hasShipment) throw Invalid();
        if (reservations.Count != actual.Count || actual.Any(row => !reservations.ContainsKey(row["_id"].AsInt64))) throw Invalid();
        foreach (var row in actual) foreach (var field in new[] { "revision", "remaining", "released", "dispatched" })
            if (row[field] != reservations[row["_id"].AsInt64][field]) throw Invalid();
        stockSourceHistory?.Require(database, tx);
        if (checkpoint is null || s.Revision != sealedRevision || s.DispatchSequence != sealedSequence)
            await OrderProgressSeals.SealAsync(database, clock, tx, order, s.Facts(), ct);
        return new(s.Revision, s.State, [.. s.Accepted], [.. s.Rejected], s.Cancelled) {
            DispatchSequence = s.DispatchSequence, HasRemainingReservation = reservations.Values.Any(row => row["remaining"].AsInt64 > 0) };
    }
    private sealed record ReviewContext(BsonDocument Order, BsonDocument? Suborder, ResourceScope Resource, OrderApprovalProgress State,
        OrderCatalogQuote Catalog, BsonDocument Terms, long SpendingRequestedBy);
    private AuthorizationQuery ReviewQuery(ActorContext actor, AuthorizationSnapshot snapshot, OrderReviewStage stage) {
        var query = ScopePolicy.Compile(actor, snapshot, ReviewPermission(stage), clock.UtcNow); AuthorizationStore.Require(query); return query;
    }
    private async Task<ReviewContext> ReviewContextAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx,
        long id, long? suborderId, OrderReviewStage stage, CancellationToken ct) {
        var query = ReviewQuery(actor, snapshot, stage);
        if (id <= 0 || stage != OrderReviewStage.Store && suborderId.HasValue || stage == OrderReviewStage.Store && suborderId is not > 0) throw Invalid();
        BsonDocument? sub = null;
        if (stage == OrderReviewStage.Store) sub = await Rows(B.Suborders).Find(tx, ScopeQuery.Create(query) & F.Eq("_id", suborderId!.Value) & F.Eq("orderId", id)).SingleOrDefaultAsync(ct) ?? throw Invalid();
        else if (!await Rows(B.Orders).Find(tx, ScopeQuery.Create(query) & F.Eq("_id", id)).AnyAsync(ct)) throw Invalid();
        var order = await GraphAsync(tx, actor, id, ct, requireOwner: false);
        var resource = ReviewResource(order, sub);
        if (!query.Matches(resource) || !ScopePolicy.IsResourceActive(resource, snapshot.Directory)) throw Invalid();
        var state = await DecisionGraphAsync(tx, order, ct);
        var commercial = await CurrentSubstitutionCommercialAsync(tx, order, state.Revision, ct);
        return new(order, sub, resource, state, B.Quote(commercial.Catalog), commercial.Terms,
            commercial.Amendment?["actorId"].AsInt64 ?? order["createdBy"].AsInt64);
    }
    private static OrderReviewView ReviewView(ReviewContext context, OrderReviewStage stage) {
        var order = context.Order; var subId = context.Suborder?["_id"].AsInt64; var quote = context.Catalog;
        if (context.Suborder is { } sub) {
            var lines = quote.Lines.Where(line => line.FulfillmentOwner == CatalogRepository.Owner(sub["owner"].AsBsonDocument)).ToImmutableArray();
            quote = new(lines, quote.Currency, lines.Aggregate(new Money(0), (total, line) => Money.Add(total, line.CatalogSubtotal)));
        }
        var storeState = subId.HasValue ? context.State.RejectedSuborders.Contains(new(subId.Value)) ? "rejected" : context.State.Cancelled ? "cancelled" :
            context.State.SpendingState == OrderSpendingState.Rejected ? "spendingRejected" : context.State.AcceptedSuborders.Contains(new(subId.Value)) ? "accepted" :
            OrderSpendingPolicy.CanAcceptAtStore(context.State.SpendingState) ? "awaitingAcceptance" : "awaitingSpending" : null;
        return new(order["_id"].AsInt64, subId, stage, context.State.Revision, DecisionReview(order, subId, stage, context.State.Revision, context.State.DispatchSequence),
            OrderDraftBson.Context(order["input"]["context"].AsBsonDocument), order["terms"]["buyer"]["name"].AsString, OrderDraftBson.Input(order["input"].AsBsonDocument).Delivery!, quote,
            subId.HasValue ? null : new Money(Decimal128.ToDecimal(context.Terms["tax"].AsDecimal128)),
            subId.HasValue ? null : new Money(Decimal128.ToDecimal(context.Terms["charges"].AsDecimal128)),
            subId.HasValue ? null : new Money(Decimal128.ToDecimal(context.Terms["total"].AsDecimal128)), context.State.SpendingState, storeState,
            !context.State.Cancelled && (stage == OrderReviewStage.Spending ? context.State.SpendingState == OrderSpendingState.Pending :
                stage == OrderReviewStage.Store ? storeState == "awaitingAcceptance" : context.State.SpendingState != OrderSpendingState.Rejected &&
                context.State.HasRemainingReservation), context.State.Cancelled, OrderDraftBson.Time(order["orderedAt"]));
    }
    private async Task CompleteReviewAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx,
        OrderReviewStage stage, IReadOnlyCollection<ReviewContext> contexts, CancellationToken ct, bool touchOrder = true) {
        var query = ReviewQuery(actor, snapshot, stage);
        if (contexts.Any(context => !query.Matches(context.Resource) || !ScopePolicy.IsResourceActive(context.Resource, snapshot.Directory))) throw Invalid();
        var clauses = query.Clauses.Where(clause => contexts.Count == 0 || contexts.Any(context => clause.Matches(context.Resource))).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(contexts.SelectMany(context => ScopePolicy.RequiredNodes(context.Resource, snapshot.Directory))).Distinct(), [],
            snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        if (touchOrder) foreach (var context in contexts.DistinctBy(value => value.Order["_id"].AsInt64)) await TouchAsync(tx, context.Order, ct);
        if (actor.Session is { } reference) {
            var live = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(reference, actor.UserId, actor.ProviderIssuer,
                actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).Project(new BsonDocument { { "idleExpiresAt", 1 }, { "absoluteExpiresAt", 1 } }).SingleOrDefaultAsync(ct);
            var now = clock.UtcNow.ToUnixTimeMilliseconds();
            if (live is null || !live.TryGetValue("idleExpiresAt", out var idle) || !idle.IsBsonDateTime ||
                !live.TryGetValue("absoluteExpiresAt", out var absolute) || !absolute.IsBsonDateTime ||
                idle.AsBsonDateTime.MillisecondsSinceEpoch <= now || absolute.AsBsonDateTime.MillisecondsSinceEpoch <= now)
                throw new AuthorizationDeniedException("stale_or_inactive_session");
        }
        query = ReviewQuery(actor, snapshot, stage);
        if (contexts.Any(context => !query.Matches(context.Resource))) throw Invalid();
        ct.ThrowIfCancellationRequested();
    }
    public async Task<OrderReviewView> ReviewOrderAsync(ActorContext actor, long id, long? suborderId, OrderReviewStage stage, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var context = await ReviewContextAsync(actor, snapshot, tx, id, suborderId, stage, token);
            await CompleteReviewAsync(actor, snapshot, tx, stage, [context], token); return ReviewView(context, stage);
        }, ct);
    }
    public async Task<(ImmutableArray<OrderReviewView> Items, bool HasMore)> ReviewListAsync(ActorContext actor, OrderReviewStage stage, long? beforeId = null, CancellationToken ct = default) {
        if (beforeId is <= 0) throw Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var query = ReviewQuery(actor, snapshot, stage); var filter = ScopeQuery.Create(query);
            if (beforeId.HasValue) filter &= F.Lt("_id", beforeId.Value);
            var rows = await Rows(stage != OrderReviewStage.Store ? B.Orders : B.Suborders).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var contexts = new List<ReviewContext>();
            foreach (var row in rows.Take(25)) contexts.Add(await ReviewContextAsync(actor, snapshot, tx,
                stage != OrderReviewStage.Store ? row["_id"].AsInt64 : row["orderId"].AsInt64, stage != OrderReviewStage.Store ? null : row["_id"].AsInt64, stage, token));
            await CompleteReviewAsync(actor, snapshot, tx, stage, contexts, token);
            return (contexts.Select(context => ReviewView(context, stage)).ToImmutableArray(), rows.Count > 25);
        }, ct);
    }
    public Task<BsonDocument> DecideOrderAsync(ActorContext actor, long orderId, long? suborderId, OrderReviewStage stage, long expectedRevision,
        string review, AuthorizationCommandMetadata metadata, CancellationToken ct = default, OrderReviewAction action = OrderReviewAction.Approve) => DecideOrderCoreAsync(actor, orderId, suborderId, stage, expectedRevision, review, metadata, false, action, ct);
    private async Task<BsonDocument> DecideOrderCoreAsync(ActorContext actor, long orderId, long? suborderId, OrderReviewStage stage, long expectedRevision,
        string review, AuthorizationCommandMetadata metadata, bool receiptOnly, OrderReviewAction action, CancellationToken ct) {
        Metadata(metadata); var permission = ReviewPermission(stage);
        if (expectedRevision is < 1 or > 102 || review is null || review.Length != 64 || review.Any(c => !char.IsAsciiHexDigitLower(c))) throw Invalid();
        await StorageAsync(ct); var operation = DecisionOperation(stage, action); var body = DecisionBody(orderId, suborderId, stage, expectedRevision, review, action);
        var id = receiptOnly ? 0 : (await ids.NextAsync(Decisions, ct)).Value; var auditId = receiptOnly ? 0 : (await ids.NextAsync("auditEvents", ct)).Value;
        ReviewContext? context = null; AuthorizationSnapshot current = null!;
        return await authorization.ExecuteCommandAsync(actor, operation, metadata, body, async (snapshot, tx, token) => {
            context = await ReviewContextAsync(actor, snapshot, tx, orderId, suborderId, stage, token);
            current = snapshot;
            var previous = await Rows(Decisions).Find(tx, F.Eq("decidedBy", actor.UserId) & F.Eq("stage", stage.ToString()) & F.Eq("action", action.ToString()) & F.Eq("requestKey", metadata.IdempotencyKey)).Limit(2).ToListAsync(token);
            if (previous.Count > 1) throw Invalid();
            if (previous.Count == 1) _ = await GraphAsync(tx, actor, previous[0]["orderId"].AsInt64, token, requireOwner: false);
            var evidence = new ApprovalRequest(1, 1, RequestedBy: context.SpendingRequestedBy);
            return new(ScopePolicy.Authorize(actor, snapshot, permission, context.Resource, clock.UtcNow, evidence),
                ScopePolicy.RequiredNodes(context.Resource, snapshot.Directory), permission, new(context.Resource, evidence));
        }, async (tx, token) => {
            var selected = context ?? throw Invalid(); var view = ReviewView(selected, stage);
            if (view.Revision != expectedRevision || view.Review != review || !view.CanDecide) throw new PersistenceConflictException("order_decision_changed", "Reload and review the current order decision");
            var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
            if (now < view.OrderedAt) throw Invalid();
            var releases = action == OrderReviewAction.Approve ? new BsonArray() : await PrepareReleasesAsync(tx, selected.Order, suborderId, token);
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "kind", "ordering.order.decision" }, { "orderId", orderId },
                { "suborderId", suborderId.HasValue ? new BsonInt64(suborderId.Value) : BsonNull.Value }, { "stage", stage.ToString() }, { "action", action.ToString() }, { "releases", releases },
                { "revision", expectedRevision + 1 }, { "expectedRevision", expectedRevision }, { "review", review }, { "orderSnapshot", selected.Order["snapshotHash"] },
                { "decidedBy", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "decidedAt", now.UtcDateTime }, { "auditId", auditId },
                { "requestKey", metadata.IdempotencyKey }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason) } };
            await Rows(Decisions).InsertOneAsync(tx, row, cancellationToken: token);
            await ReleaseOrderStockAsync(tx, actor, row, metadata, token, fenceToken => CompleteReviewAsync(actor, current, tx, stage, [selected], fenceToken, touchOrder: false));
            await Rows("auditEvents").InsertOneAsync(tx, DecisionAudit(row), cancellationToken: token);
            await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
                { "sourceKey", "ordering.order.decision:" + id }, { "kind", "ordering.order.decided" }, { "payload", DecisionResult(row) },
                { "createdAt", now.UtcDateTime }, { "dueAt", now.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            await RecordStockSourceTransitionAsync(tx, "decision", id, token);
            return DecisionResult(row);
        }, ct, async (snapshot, tx, token) => {
            var current = await ReviewContextAsync(actor, snapshot, tx, orderId, suborderId, stage, token);
            var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", metadata.IdempotencyKey)).SingleAsync(token);
            var recorded = await Rows(Decisions).Find(tx, F.Eq("_id", receipt["result"]["decisionId"])).SingleOrDefaultAsync(token) ?? throw Invalid();
            if (recorded["decidedBy"] != actor.UserId || recorded["requestKey"] != metadata.IdempotencyKey || recorded["orderId"] != orderId ||
                recorded["stage"] != stage.ToString() || recorded["action"] != action.ToString() || recorded["bodyHash"] != AuthorizationStore.CommandHash(body, metadata.Reason) ||
                !CatalogSchemas.SemanticEquals(receipt["result"], DecisionResult(recorded))) throw Invalid();
            await CompleteReviewAsync(actor, snapshot, tx, stage, [current], token);
        }, requireReceipt: receiptOnly);
    }
    public async Task<BsonDocument?> RecoverOrderDecisionAsync(ActorContext actor, OrderReviewStage stage, string key, CancellationToken ct = default, OrderReviewAction action = OrderReviewAction.Approve) {
        _ = DecisionOperation(stage, action);
        Metadata(new(key, "Recover original decision", "order-decision-recovery")); await StorageAsync(ct);
        var selected = await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            _ = ReviewQuery(actor, snapshot, stage);
            var row = await Rows(Decisions).Find(tx, F.Eq("decidedBy", actor.UserId) & F.Eq("stage", stage.ToString()) & F.Eq("action", action.ToString()) & F.Eq("requestKey", key)).SingleOrDefaultAsync(token);
            if (row is null) await CompleteReviewAsync(actor, snapshot, tx, stage, [], token);
            return row;
        }, ct);
        if (selected is null) return null;
        // Receipt-only replay cannot recreate a missing decision, reserve stock or execute a new approval.
        return await DecideOrderCoreAsync(actor, selected["orderId"].AsInt64, selected["suborderId"].IsBsonNull ? null : selected["suborderId"].AsInt64,
            stage, selected["expectedRevision"].AsInt64, selected["review"].AsString,
            new(key, selected["reason"].AsString, "order-decision-recovery"), true, action, ct);
    }
}
