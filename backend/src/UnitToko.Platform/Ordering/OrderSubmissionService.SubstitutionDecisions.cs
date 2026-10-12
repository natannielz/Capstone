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
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;
using S = UnitToko.Platform.Ordering.OrderSubstitutionBson;

namespace UnitToko.Platform.Ordering;

public sealed record OrderSubstitutionDecisionReview(LongId ProposalId, LongId OrderId, LongId SuborderId, long Revision,
    OrderSubstitutionDecision Decision, string Review, OrderQuotedLine Original, int OriginalPackages, OrderQuotedLine Replacement,
    OrderCatalogQuote CatalogAfter, Money Tax, Money Charges, Money Total, string SpendingState, DateTimeOffset CalculatedAt);

public sealed partial class OrderSubmissionService
{
    private sealed record SubstitutionDecisionCapture(SubstitutionContext Context, BsonDocument Proposal, OrderSubstitutionDecision Decision,
        OrderCatalogQuote Catalog, BsonDocument Terms, OrderSpendingState Spending, string Review, OrderAdmission? Admission, OrderDraftInput? ReplacementInput,
        OrderSubstitutionReleaseCapture? Release, SubstitutionInvoiceBudget? InvoiceBudget);

    private async Task<(BsonDocument Catalog, BsonDocument Terms, BsonDocument? Amendment)> CurrentSubstitutionCommercialAsync(
        IClientSessionHandle tx, BsonDocument order, long decisionRevision, CancellationToken ct) {
        var row = await Rows(S.Decisions).Find(tx, F.Eq("orderId", order["_id"]) & F.Eq("decision", "Accept") & F.Lte("revision", decisionRevision))
            .Sort(new BsonDocument("revision", -1)).FirstOrDefaultAsync(ct);
        if (row is null) return (order["quote"].DeepClone().AsBsonDocument, order["terms"].DeepClone().AsBsonDocument, null);
        var valid = await new OrderSubstitutionRecords(database, clock).ReadAsync(tx, S.Decisions, row["_id"].AsInt64, order, ct);
        return (valid["catalog"].DeepClone().AsBsonDocument, valid["terms"].DeepClone().AsBsonDocument, valid);
    }
    private static string SubstitutionDecisionBinding(BsonDocument order, BsonDocument proposal, long revision, long dispatchSequence,
        OrderSubstitutionDecision decision, OrderCatalogQuote catalog, BsonDocument terms) => CatalogRepository.Digest(new BsonDocument {
        { "orderSnapshot", order["snapshotHash"] }, { "proposalId", proposal["_id"] }, { "proposalHash", proposal["recordHash"] },
        { "revision", revision }, { "dispatchSequence", dispatchSequence }, { "decision", decision.ToString() }, { "catalog", B.Quote(catalog) }, { "terms", terms } });

    private async Task<SubstitutionDecisionCapture> CaptureSubstitutionDecisionAsync(ActorContext actor, AuthorizationSnapshot snapshot,
        IClientSessionHandle tx, long proposalId, OrderSubstitutionDecision decision, CancellationToken ct) {
        _ = OrderSubstitutionPolicy.WritesReservation(decision);
        var query = SubstitutionQuery(actor, snapshot, Permissions.SubstitutionAccept);
        var selected = await Rows(S.Proposals).Find(tx, ScopeQuery.Create(query) & F.Eq("_id", proposalId)).SingleOrDefaultAsync(ct) ?? throw S.Unavailable();
        var context = await SubstitutionContextAsync(actor, snapshot, tx, selected["orderId"].AsInt64, selected["suborderId"].AsInt64, Permissions.SubstitutionAccept, ct);
        var proposal = await new OrderSubstitutionRecords(database, clock).ReadAsync(tx, S.Proposals, proposalId, context.Order, ct);
        var input = S.Input(proposal["input"].AsBsonDocument); var progress = SubstitutionLine(context, input);
        if (context.Progress.Revision >= 103 || await Rows(S.Decisions).Find(tx, F.Eq("proposalId", proposalId)).AnyAsync(ct) ||
            proposal["catalogBefore"] != B.Quote(context.Catalog) || proposal["originalRemaining"] != progress.Reserved ||
            proposal["reservationReview"] != FulfillmentReview(context.Order, input.SuborderId.Value, context.Progress.Revision, context.Reservations)) throw S.Unavailable();
        var replacement = B.Quote(proposal["replacement"].AsBsonDocument).Lines.Single();
        if (proposal["review"] != ProposalReview(context, input, replacement)) throw S.Unavailable();
        OrderAdmission? admitted = null; OrderDraftInput? replacementInput = null; OrderSubstitutionReleaseCapture? release = null; SubstitutionInvoiceBudget? invoiceBudget = null;
        var after = context.Catalog; var terms = context.Terms.DeepClone().AsBsonDocument; var spending = context.Progress.SpendingState;
        if (decision == OrderSubstitutionDecision.Accept) {
            // The same actual buyer command independently supplies buyer publication, commercial,
            // postal, capacity and finite-stock authority. Staff facts are never that authority.
            replacementInput = OrderDraftBson.Input(context.Order["input"].AsBsonDocument) with { Selections = [input.Selection.Replacement] };
            var current = await QuoteAsync(actor, snapshot, tx, replacementInput, ct);
            if (B.Quote(current) != proposal["replacement"]) throw new PersistenceConflictException("order_substitution_selection_changed", "The proposed package or price changed; ask for a new proposal");
            after = OrderSubstitutionPolicy.Replace(context.Catalog, progress, input.Selection, current.Lines.Single(), context.Progress.Revision + 1).Catalog;
            var approved = admissionReader as ApprovedOrderAdmissionReader ?? throw OrderAdmissionPolicy.Invalid();
            var releaseFacts = await PrepareSubstitutionReleasesAsync(context, input.Selection.OriginalSkuId.Value,
                input.Selection.OriginalLineRevision, input.Selection.OriginalPackages, ct);
            release = new(database, actor, tx, clock, releaseFacts, (written, token) => FenceSubstitutionAsync(context, token, requireUnchanged: !written));
            var replacementLine = current.Lines.Single() with { LineRevision = context.Progress.Revision + 1 };
            admitted = await approved.ReadForSubstitutionAsync(tx, actor, replacementInput, after,
                new([replacementLine], current.Currency, current.CatalogSubtotal), release, ct);
            terms = B.Terms(admitted.Terms); spending = OrderSpendingPolicy.Initial(admitted.Terms.Spending, admitted.Terms.Total);
            invoiceBudget = await RequireSubstitutionInvoiceBudgetAsync(context, terms, ct);
            OrderSubstitutionPolicy.RequireApprovalCapacity(context.Progress.Revision, context.Order["suborderIds"].AsBsonArray.Count, spending);
            var retained = await Rows(B.Reservations).Find(tx, F.Eq("orderId", input.OrderId.Value))
                .Limit(OrderReservationHistoryLimits.MaximumRetained + 1).ToListAsync(ct);
            OrderReservationHistoryLimits.RequireReplacement(retained, releaseFacts, admitted.Stock.Length);
            await approved.CompleteAsync(tx, actor, admitted, ct);
        }
        var review = SubstitutionDecisionBinding(context.Order, proposal, context.Progress.Revision, context.Progress.DispatchSequence, decision, after, terms);
        await FenceSubstitutionAsync(context, ct); return new(context, proposal, decision, after, terms, spending, review, admitted, replacementInput, release, invoiceBudget);
    }
    private static OrderSubstitutionDecisionReview SubstitutionDecisionView(SubstitutionDecisionCapture captured, DateTimeOffset at) {
        var input = S.Input(captured.Proposal["input"].AsBsonDocument);
        return new(new(captured.Proposal["_id"].AsInt64), input.OrderId, input.SuborderId, captured.Context.Progress.Revision, captured.Decision, captured.Review,
            captured.Context.Catalog.Lines.Single(line => line.Definition.SkuId == input.Selection.OriginalSkuId && line.LineRevision == input.Selection.OriginalLineRevision), input.Selection.OriginalPackages,
            B.Quote(captured.Proposal["replacement"].AsBsonDocument).Lines.Single(), captured.Catalog,
            new(Decimal128.ToDecimal(captured.Terms["tax"].AsDecimal128)), new(Decimal128.ToDecimal(captured.Terms["charges"].AsDecimal128)),
            new(Decimal128.ToDecimal(captured.Terms["total"].AsDecimal128)), captured.Spending.ToString(), at);
    }
    public async Task<OrderSubstitutionDecisionReview> PreviewSubstitutionDecisionAsync(ActorContext actor, long proposalId,
        OrderSubstitutionDecision decision, CancellationToken ct = default) {
        await SubstitutionStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
            SubstitutionDecisionView(await CaptureSubstitutionDecisionAsync(actor, snapshot, tx, proposalId, decision, token), clock.UtcNow), ct);
    }
    public Task<BsonDocument> DecideSubstitutionAsync(ActorContext actor, long proposalId, long expectedRevision, OrderSubstitutionDecision decision,
        string review, AuthorizationCommandMetadata metadata, CancellationToken ct = default) =>
        DecideSubstitutionCoreAsync(actor, proposalId, expectedRevision, decision, review, metadata, false, ct);

    private async Task<BsonDocument> DecideSubstitutionCoreAsync(ActorContext actor, long proposalId, long expectedRevision, OrderSubstitutionDecision decision,
        string review, AuthorizationCommandMetadata metadata, bool receiptOnly, CancellationToken ct) {
        if (proposalId <= 0 || expectedRevision is < 1 or > 102) throw S.Unavailable();
        OrderSubstitutionPolicy.RequireReview(review, review); Metadata(metadata); await SubstitutionStorageAsync(ct);
        var operation = S.Operation(decision); var body = S.DecisionBody(proposalId, expectedRevision, decision, review);
        var id = receiptOnly ? 0 : (await ids.NextAsync(S.Decisions, ct)).Value; var auditId = receiptOnly ? 0 : (await ids.NextAsync("auditEvents", ct)).Value;
        SubstitutionContext context = null!; SubstitutionDecisionCapture? capture = null;
        return await authorization.ExecuteCommandAsync(actor, operation, metadata, body, async (snapshot, tx, token) => {
            capture = null; var query = SubstitutionQuery(actor, snapshot, Permissions.SubstitutionAccept);
            var proposal = await Rows(S.Proposals).Find(tx, ScopeQuery.Create(query) & F.Eq("_id", proposalId)).SingleOrDefaultAsync(token) ?? throw S.Unavailable();
            context = await SubstitutionContextAsync(actor, snapshot, tx, proposal["orderId"].AsInt64, proposal["suborderId"].AsInt64, Permissions.SubstitutionAccept, token);
            _ = await new OrderSubstitutionRecords(database, clock).ReadAsync(tx, S.Proposals, proposalId, context.Order, token);
            var previous = await Rows(S.Decisions).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).Limit(2).ToListAsync(token);
            if (previous.Count > 1) throw S.Unavailable();
            if (previous.Count == 1) _ = await new OrderSubstitutionRecords(database, clock).ReadAsync(tx, S.Decisions, previous[0]["_id"].AsInt64, context.Order, token);
            var evidence = new ApprovalRequest(1, 1, RequestedBy: context.Order["createdBy"].AsInt64, ProposedBy: proposal["actorId"].AsInt64);
            return new(ScopePolicy.Authorize(actor, snapshot, Permissions.SubstitutionAccept, context.Resource, clock.UtcNow, evidence),
                ScopePolicy.RequiredNodes(context.Resource, snapshot.Directory), Permissions.SubstitutionAccept, new(context.Resource, evidence));
        }, async (tx, token) => {
            capture = await CaptureSubstitutionDecisionAsync(actor, context.Authority, tx, proposalId, decision, token); context = capture.Context;
            if (expectedRevision != context.Progress.Revision || capture.Review != review)
                throw new PersistenceConflictException("order_substitution_review_changed", "Review the current replacement before consenting");
            var input = S.Input(capture.Proposal["input"].AsBsonDocument);
            var row = SubstitutionHeader(context, input, id, "ordering.substitution.decision", review, metadata, auditId, body);
            row.Add("proposalId", proposalId); row.Add("proposalHash", capture.Proposal["recordHash"]); row.Add("decision", decision.ToString());
            row.Add("expectedRevision", expectedRevision); row.Add("revision", expectedRevision + 1); row.Add("dispatchSequence", context.Progress.DispatchSequence);
            row.Add("catalog", B.Quote(capture.Catalog)); row.Add("terms", capture.Terms); row.Add("spendingState", capture.Spending.ToString());
            var releases = capture.Release?.Rows ?? new BsonArray();
            row.Add("releases", releases); row.Add("allocations", capture.Admission is { } admission ? new BsonArray(admission.Stock.Select(B.Allocation)) : new BsonArray());
            var reservationIds = new BsonArray();
            foreach (var _ in row["allocations"].AsBsonArray) reservationIds.Add((await ids.NextAsync(B.Reservations, token)).Value);
            row.Add("reservationIds", reservationIds); row.Add("recordHash", S.Hash(row));
            await Rows(S.Decisions).InsertOneAsync(tx, row, cancellationToken: token);
            if (capture.Admission is { } admitted) {
                ((ApprovedOrderAdmissionReader)admissionReader!).BindSubstitution(tx, actor, admitted, id);
                await ApplySubstitutionReservationsAsync(context, row, metadata, token);
            }
            await PublishSubstitutionAsync(tx, row, false, token);
            await RecordStockSourceTransitionAsync(tx, "substitution", id, token); return S.DecisionResult(row);
        }, ct, async (snapshot, tx, token) => {
            var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", metadata.IdempotencyKey)).SingleAsync(token);
            var row = await new OrderSubstitutionRecords(database, clock).ReadAsync(tx, S.Decisions, receipt["result"]["decisionId"].AsInt64, context.Order, token);
            if (row["bodyHash"] != AuthorizationStore.CommandHash(body, metadata.Reason) || row["proposalId"] != proposalId) throw S.Unavailable();
            _ = await GraphWithProgressAsync(tx, actor, context.Order["_id"].AsInt64, token, requireOwner: false);
            if (capture?.Admission is { } admitted) {
                await admissionReader!.CompleteAsync(tx, actor, admitted, token);
                var currentQuote = await QuoteAsync(actor, snapshot, tx, capture.ReplacementInput!, token);
                if (B.Quote(currentQuote) != capture.Proposal["replacement"]) throw S.Unavailable();
                await RequireSubstitutionInvoiceBudgetAsync(context, capture.Terms, token, capture.InvoiceBudget ?? throw S.Unavailable());
            }
            await FenceSubstitutionAsync(context, token, requireUnchanged: false);
        }, requireReceipt: receiptOnly, validateCurrent: () => {
            SubstitutionCurrent(context);
            if (capture?.Admission is { } admitted)
                ((ApprovedOrderAdmissionReader)admissionReader!).RequireSubstitutionCurrent(context.Tx, actor, admitted);
        });
    }
    public async Task<BsonDocument?> RecoverSubstitutionDecisionAsync(ActorContext actor, string key, OrderSubstitutionDecision decision, CancellationToken ct = default) {
        var operation = S.Operation(decision); Metadata(new(key, "Recover original buyer decision", "substitution-recovery")); await SubstitutionStorageAsync(ct);
        var row = await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            _ = SubstitutionQuery(actor, snapshot, Permissions.SubstitutionAccept);
            var row = await Rows(S.Decisions).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", key) & F.Eq("decision", decision.ToString())).SingleOrDefaultAsync(token);
            if (row is null) {
                if (await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", key)).AnyAsync(token)) throw S.Unavailable();
                await authorization.FenceAsync(actor, [], tx, token);
            }
            return row;
        }, ct);
        return row is null ? null : await DecideSubstitutionCoreAsync(actor, row["proposalId"].AsInt64, row["expectedRevision"].AsInt64, decision,
            row["review"].AsString, new(key, row["reason"].AsString, row["correlationId"].AsString), true, ct);
    }
}
