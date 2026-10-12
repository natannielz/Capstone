using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;
using S = UnitToko.Platform.Ordering.OrderSubstitutionBson;

namespace UnitToko.Platform.Ordering;

public sealed record OrderSubstitutionProposalReview(OrderSubstitutionInput Input, string Review, string BuyerName,
    OrderQuotedLine Original, int RemainingPackages, OrderQuotedLine Replacement, DateTimeOffset CalculatedAt);

public sealed partial class OrderSubmissionService
{
    private sealed record SubstitutionContext(ActorContext Actor, AuthorizationSnapshot Authority, IClientSessionHandle Tx, long Transaction,
        DateTimeOffset CapturedAt, BsonDocument Order, BsonDocument Suborder, ResourceScope Resource, OrderApprovalProgress Progress,
        OrderCatalogQuote Catalog, BsonDocument Terms, BsonDocument[] Reservations, string Permission);

    private AuthorizationQuery SubstitutionQuery(ActorContext actor, AuthorizationSnapshot snapshot, string permission) {
        if (permission is not (Permissions.SubstitutionPropose or Permissions.SubstitutionAccept)) throw S.Unavailable();
        var query = ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow); AuthorizationStore.Require(query); return query;
    }
    private async Task SubstitutionStorageAsync(CancellationToken ct) {
        await StorageAsync(ct); await OrderSubstitutionRecords.RequireStorageAsync(database, ct);
    }
    private async Task<SubstitutionContext> SubstitutionContextAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx,
        long orderId, long suborderId, string permission, CancellationToken ct) {
        if (orderId <= 0 || suborderId <= 0) throw S.Unavailable();
        var capturedAt = clock.UtcNow; var transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        var query = SubstitutionQuery(actor, snapshot, permission);
        var sub = await Rows(B.Suborders).Find(tx, ScopeQuery.Create(query) & F.Eq("_id", suborderId) & F.Eq("orderId", orderId)).SingleOrDefaultAsync(ct) ?? throw S.Unavailable();
        var (order, progress) = await GraphWithProgressAsync(tx, actor, orderId, ct, requireOwner: false);
        var resource = ReviewResource(order, sub);
        if (!query.Matches(resource) || !ScopePolicy.IsResourceActive(resource, snapshot.Directory)) throw S.Unavailable();
        var commercial = await CurrentSubstitutionCommercialAsync(tx, order, progress.Revision, ct);
        var rows = await Rows(B.Reservations).Find(tx, F.Eq("orderId", orderId) & F.Eq("suborderId", suborderId)).Sort(new BsonDocument("_id", 1)).Limit(OrderReservationHistoryLimits.MaximumRetained + 1).ToListAsync(ct);
        OrderReservationHistoryLimits.Require(rows);
        var context = new SubstitutionContext(actor, snapshot, tx, transaction, capturedAt, order, sub, resource, progress,
            B.Quote(commercial.Catalog), commercial.Terms, rows.ToArray(), permission);
        SubstitutionCurrent(context); return context;
    }
    private void SubstitutionCurrent(SubstitutionContext context) {
        if (!context.Tx.IsInTransaction || !ReferenceEquals(context.Tx.Client, database.Client) ||
            context.Tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != context.Transaction || clock.UtcNow < context.CapturedAt ||
            !SubstitutionQuery(context.Actor, context.Authority, context.Permission).Matches(context.Resource) ||
            !ScopePolicy.IsResourceActive(context.Resource, context.Authority.Directory)) throw S.Unavailable();
    }
    private async Task FenceSubstitutionAsync(SubstitutionContext context, CancellationToken ct, bool requireUnchanged = true) {
        SubstitutionCurrent(context);
        var query = SubstitutionQuery(context.Actor, context.Authority, context.Permission);
        var clauses = query.Clauses.Where(clause => clause.Matches(context.Resource)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        var current = await Rows(B.Orders).Find(context.Tx, F.Eq("_id", context.Order["_id"])).SingleOrDefaultAsync(ct) ?? throw S.Unavailable();
        if (B.SnapshotHash(current) != B.SnapshotHash(context.Order)) throw S.Unavailable();
        if (requireUnchanged) {
            var progress = await DecisionGraphAsync(context.Tx, current, ct);
            if (progress.Revision != context.Progress.Revision || progress.DispatchSequence != context.Progress.DispatchSequence)
                throw new PersistenceConflictException("order_substitution_changed", "The remaining order changed; reload the replacement review");
            var actual = await Rows(B.Reservations).Find(context.Tx, F.Eq("orderId", current["_id"]) & F.Eq("suborderId", context.Suborder["_id"]))
                .Sort(new BsonDocument("_id", 1)).Limit(OrderReservationHistoryLimits.MaximumRetained + 1).ToListAsync(ct);
            if (actual.Count != context.Reservations.Length || actual.Where((row, i) =>
                !CatalogSchemas.SemanticEquals(new BsonDocument(row.Where(field => field.Name != "fence")),
                    new BsonDocument(context.Reservations[i].Where(field => field.Name != "fence")))).Any()) throw S.Unavailable();
        }
        await TouchAsync(context.Tx, current, ct);
        if (context.Actor.Session is { } session) {
            var live = await Rows("identitySessions").Find(context.Tx, SessionPersistence.LiveFilter(session, context.Actor.UserId,
                context.Actor.ProviderIssuer, context.Actor.ProviderSubject, context.Actor.GrantVersion, clock.UtcNow))
                .Project(new BsonDocument { { "idleExpiresAt", 1 }, { "absoluteExpiresAt", 1 } }).SingleOrDefaultAsync(ct);
            var now = clock.UtcNow.ToUnixTimeMilliseconds();
            if (live is null || live["idleExpiresAt"].AsBsonDateTime.MillisecondsSinceEpoch <= now || live["absoluteExpiresAt"].AsBsonDateTime.MillisecondsSinceEpoch <= now)
                throw new AuthorizationDeniedException("stale_or_inactive_session");
        }
        SubstitutionCurrent(context); ct.ThrowIfCancellationRequested();
    }
    private static OrderLineProgress SubstitutionLine(SubstitutionContext context, OrderSubstitutionInput input) {
        OrderSubstitutionPolicy.Validate(input);
        if (input.OrderId.Value != context.Order["_id"].AsInt64 || input.SuborderId.Value != context.Suborder["_id"].AsInt64 ||
            input.ExpectedOrderRevision != context.Progress.Revision || input.ExpectedDispatchSequence != context.Progress.DispatchSequence ||
            context.Progress.Cancelled || !OrderSpendingPolicy.CanAcceptAtStore(context.Progress.SpendingState) ||
            context.Progress.RejectedSuborders.Contains(input.SuborderId)) throw S.Unavailable();
        var line = context.Catalog.Lines.SingleOrDefault(line => line.Definition.SkuId == input.Selection.OriginalSkuId && line.LineRevision == input.Selection.OriginalLineRevision) ?? throw S.Unavailable();
        if (line.FulfillmentOwner != CatalogRepository.Owner(context.Suborder["owner"].AsBsonDocument)) throw S.Unavailable();
        var selected = context.Reservations.Where(row => row["allocation"]["skuId"] == input.Selection.OriginalSkuId.Value && OrderLineBson.Revision(row["allocation"]) == input.Selection.OriginalLineRevision).ToArray();
        var remaining = checked((int)selected.Sum(row => row["remaining"].AsInt64)); var dispatched = checked((int)selected.Sum(row => row["dispatched"].AsInt64));
        var progress = new OrderLineProgress(line.Packages, remaining, dispatched, checked(line.Packages - remaining - dispatched));
        _ = OrderLinePolicy.Unallocated(progress);
        if (input.Selection.OriginalPackages > remaining) throw S.Unavailable();
        return progress;
    }
    private static string ProposalReview(SubstitutionContext context, OrderSubstitutionInput input, OrderQuotedLine replacement) => CatalogRepository.Digest(new BsonDocument {
        { "input", S.Input(input) }, { "orderSnapshot", context.Order["snapshotHash"] }, { "catalogBefore", B.Quote(context.Catalog) },
        { "replacement", B.Quote(new OrderCatalogQuote([replacement], replacement.Price.Currency, replacement.CatalogSubtotal)) },
        { "reservationReview", FulfillmentReview(context.Order, input.SuborderId.Value, context.Progress.Revision, context.Reservations) } });

    public async Task<OrderSubstitutionProposalReview> PreviewSubstitutionAsync(ActorContext actor, OrderSubstitutionInput input, CancellationToken ct = default) {
        OrderSubstitutionPolicy.Validate(input); await SubstitutionStorageAsync(ct);
        return await authorization.ReadAsync<OrderSubstitutionProposalReview>(actor, async (snapshot, tx, token) => {
            var context = await SubstitutionContextAsync(actor, snapshot, tx, input.OrderId.Value, input.SuborderId.Value, Permissions.SubstitutionPropose, token);
            var line = SubstitutionLine(context, input);
            var replacement = await new CatalogSubstitutionReader(database, clock).ReadAsync(actor, snapshot, tx,
                CatalogRepository.Owner(context.Suborder["owner"].AsBsonDocument), input.Selection.Replacement, fence => FenceSubstitutionAsync(context, fence), token);
            _ = OrderSubstitutionPolicy.Replace(context.Catalog, line, input.Selection, replacement, context.Progress.Revision + 1);
            await FenceSubstitutionAsync(context, token);
            return new(input, ProposalReview(context, input, replacement), context.Order["terms"]["buyer"]["name"].AsString,
                context.Catalog.Lines.Single(line => line.Definition.SkuId == input.Selection.OriginalSkuId && line.LineRevision == input.Selection.OriginalLineRevision), line.Reserved, replacement, clock.UtcNow);
        }, ct);
    }

    public Task<BsonDocument> ProposeSubstitutionAsync(ActorContext actor, OrderSubstitutionInput input, string review,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) => ProposeSubstitutionCoreAsync(actor, input, review, metadata, false, ct);
    private async Task<BsonDocument> ProposeSubstitutionCoreAsync(ActorContext actor, OrderSubstitutionInput input, string review,
        AuthorizationCommandMetadata metadata, bool receiptOnly, CancellationToken ct) {
        OrderSubstitutionPolicy.Validate(input); OrderSubstitutionPolicy.RequireReview(review, review); Metadata(metadata); await SubstitutionStorageAsync(ct);
        var id = receiptOnly ? 0 : (await ids.NextAsync(S.Proposals, ct)).Value; var auditId = receiptOnly ? 0 : (await ids.NextAsync("auditEvents", ct)).Value;
        var body = S.ProposalBody(input, review); SubstitutionContext context = null!;
        return await authorization.ExecuteCommandAsync(actor, S.ProposeOperation, metadata, body, async (snapshot, tx, token) => {
            context = await SubstitutionContextAsync(actor, snapshot, tx, input.OrderId.Value, input.SuborderId.Value, Permissions.SubstitutionPropose, token);
            var previous = await Rows(S.Proposals).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).Limit(2).ToListAsync(token);
            if (previous.Count > 1) throw S.Unavailable();
            if (previous.Count == 1) _ = await new OrderSubstitutionRecords(database, clock).ReadAsync(tx, S.Proposals, previous[0]["_id"].AsInt64, context.Order, token);
            return new(ScopePolicy.Authorize(actor, snapshot, Permissions.SubstitutionPropose, context.Resource, clock.UtcNow),
                ScopePolicy.RequiredNodes(context.Resource, snapshot.Directory), Permissions.SubstitutionPropose, new(context.Resource));
        }, async (tx, token) => {
            var progress = SubstitutionLine(context, input);
            if (await Rows(S.Proposals).CountDocumentsAsync(tx, F.Eq("orderId", input.OrderId.Value), cancellationToken: token) >= OrderReservationHistoryLimits.MaximumProposals) throw S.Unavailable();
            var replacement = await new CatalogSubstitutionReader(database, clock).ReadAsync(actor, context.Authority, tx,
                CatalogRepository.Owner(context.Suborder["owner"].AsBsonDocument), input.Selection.Replacement, fence => FenceSubstitutionAsync(context, fence), token);
            _ = OrderSubstitutionPolicy.Replace(context.Catalog, progress, input.Selection, replacement, context.Progress.Revision + 1);
            OrderSubstitutionPolicy.RequireReview(review, ProposalReview(context, input, replacement));
            var row = SubstitutionHeader(context, input, id, "ordering.substitution.proposal", review, metadata, auditId, body);
            row.Add("catalogBefore", B.Quote(context.Catalog)); row.Add("replacement", B.Quote(new OrderCatalogQuote([replacement], replacement.Price.Currency, replacement.CatalogSubtotal)));
            row.Add("originalRemaining", progress.Reserved); row.Add("reservationReview", FulfillmentReview(context.Order, input.SuborderId.Value, context.Progress.Revision, context.Reservations));
            row.Add("recordHash", S.Hash(row));
            await Rows(S.Proposals).InsertOneAsync(tx, row, cancellationToken: token);
            await PublishSubstitutionAsync(tx, row, true, token); return S.ProposalResult(row);
        }, ct, async (snapshot, tx, token) => {
            var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", S.ProposeOperation) & F.Eq("key", metadata.IdempotencyKey)).SingleAsync(token);
            var row = await new OrderSubstitutionRecords(database, clock).ReadAsync(tx, S.Proposals, receipt["result"]["proposalId"].AsInt64, context.Order, token);
            if (row["bodyHash"] != AuthorizationStore.CommandHash(body, metadata.Reason)) throw S.Unavailable();
            await FenceSubstitutionAsync(context, token);
        }, requireReceipt: receiptOnly, validateCurrent: () => SubstitutionCurrent(context));
    }
    private BsonDocument SubstitutionHeader(SubstitutionContext context, OrderSubstitutionInput input, long id, string kind, string review,
        AuthorizationCommandMetadata metadata, long auditId, object body) {
        SubstitutionCurrent(context);
        var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "kind", kind }, { "orderId", input.OrderId.Value }, { "suborderId", input.SuborderId.Value },
            { "orderSnapshot", context.Order["snapshotHash"] }, { "input", S.Input(input) }, { "actorId", context.Actor.UserId }, { "actorGrantVersion", context.Actor.GrantVersion },
            { "recordedAt", DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds()).UtcDateTime }, { "requestKey", metadata.IdempotencyKey },
            { "review", review }, { "bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason) }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId } };
        foreach (var field in new[] { "createdBy", "buyingOrganizationId", "branchId", "divisionId" }) row.Add(field, context.Order[field].DeepClone());
        foreach (var field in new[] { "fulfillmentOrganizationId", "shopId", "warehouseId", "channelId" }) row.Add(field, context.Suborder[field].DeepClone());
        return row;
    }
    private async Task PublishSubstitutionAsync(IClientSessionHandle tx, BsonDocument row, bool proposal, CancellationToken ct) {
        var operation = proposal ? S.ProposeOperation : S.Operation(Enum.Parse<OrderSubstitutionDecision>(row["decision"].AsString));
        await Rows("auditEvents").InsertOneAsync(tx, S.Audit(row, proposal), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
            { "sourceKey", operation + ":" + row["_id"].AsInt64 }, { "kind", operation }, { "payload", proposal ? S.ProposalResult(row) : S.DecisionResult(row) },
            { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
    public async Task<BsonDocument?> RecoverSubstitutionProposalAsync(ActorContext actor, string key, CancellationToken ct = default) {
        Metadata(new(key, "Recover original substitution", "substitution-recovery")); await SubstitutionStorageAsync(ct);
        var row = await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            _ = SubstitutionQuery(actor, snapshot, Permissions.SubstitutionPropose);
            var row = await Rows(S.Proposals).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", key)).SingleOrDefaultAsync(token);
            if (row is null) {
                if (await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", S.ProposeOperation) & F.Eq("key", key)).AnyAsync(token)) throw S.Unavailable();
                await authorization.FenceAsync(actor, [], tx, token);
            }
            return row;
        }, ct);
        return row is null ? null : await ProposeSubstitutionCoreAsync(actor, S.Input(row["input"].AsBsonDocument), row["review"].AsString,
            new(key, row["reason"].AsString, row["correlationId"].AsString), true, ct);
    }
}
