using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;
using S = UnitToko.Platform.Ordering.OrderSubstitutionBson;

namespace UnitToko.Platform.Ordering;

public sealed partial class OrderSubmissionService
{
    private async Task<(OrderCatalogQuote Catalog, BsonDocument Terms, OrderSpendingState Spending)> VerifySubstitutionHistoryAsync(
        IClientSessionHandle tx, BsonDocument order, BsonDocument supplied, Dictionary<long, BsonDocument> expected,
        IReadOnlyCollection<BsonDocument> actual, OrderCatalogQuote catalogBefore, BsonDocument termsBefore, OrderSpendingState spending,
        bool cancelled, ImmutableArray<LongId> rejected, long revision, long dispatchSequence, DateTimeOffset lastAt, CancellationToken ct) {
        var records = new OrderSubstitutionRecords(database, clock);
        var row = await records.ReadAsync(tx, S.Decisions, supplied["_id"].AsInt64, order, ct);
        if (row != supplied) throw S.Unavailable();
        var proposal = await records.ReadAsync(tx, S.Proposals, row["proposalId"].AsInt64, order, ct);
        var input = S.Input(proposal["input"].AsBsonDocument); var decision = Enum.Parse<OrderSubstitutionDecision>(row["decision"].AsString);
        if (cancelled || !OrderSpendingPolicy.CanAcceptAtStore(spending) || rejected.Contains(input.SuborderId) ||
            row["expectedRevision"] != revision || row["revision"] != revision + 1 || row["dispatchSequence"] != dispatchSequence ||
            input.ExpectedOrderRevision != revision || input.ExpectedDispatchSequence != dispatchSequence ||
            row["suborderId"] != input.SuborderId.Value || OrderDraftBson.Time(row["recordedAt"]) < lastAt ||
            proposal["catalogBefore"] != B.Quote(catalogBefore)) throw S.Unavailable();
        var selected = expected.Values.Where(value => value["suborderId"] == input.SuborderId.Value).ToArray();
        var original = catalogBefore.Lines.SingleOrDefault(line => line.Definition.SkuId == input.Selection.OriginalSkuId && line.LineRevision == input.Selection.OriginalLineRevision) ?? throw S.Unavailable();
        var sub = await Rows(B.Suborders).Find(tx, F.Eq("_id", input.SuborderId.Value) & F.Eq("orderId", order["_id"])).SingleAsync(ct);
        if (original.FulfillmentOwner != CatalogRepository.Owner(sub["owner"].AsBsonDocument)) throw S.Unavailable();
        var remaining = checked((int)selected.Where(value => value["allocation"]["skuId"] == input.Selection.OriginalSkuId.Value && OrderLineBson.Revision(value["allocation"]) == input.Selection.OriginalLineRevision).Sum(value => value["remaining"].AsInt64));
        var dispatched = checked((int)selected.Where(value => value["allocation"]["skuId"] == input.Selection.OriginalSkuId.Value && OrderLineBson.Revision(value["allocation"]) == input.Selection.OriginalLineRevision).Sum(value => value["dispatched"].AsInt64));
        var progress = new OrderLineProgress(original.Packages, remaining, dispatched, checked(original.Packages - remaining - dispatched));
        _ = OrderLinePolicy.Unallocated(progress);
        var reservationReview = FulfillmentReview(order, input.SuborderId.Value, revision, selected);
        var replacementQuote = B.Quote(proposal["replacement"].AsBsonDocument);
        if (replacementQuote.Lines.Length != 1 || input.Selection.OriginalPackages > remaining || proposal["originalRemaining"] != remaining ||
            proposal["reservationReview"] != reservationReview || proposal["review"] != CatalogRepository.Digest(new BsonDocument {
                { "input", S.Input(input) }, { "orderSnapshot", order["snapshotHash"] }, { "catalogBefore", B.Quote(catalogBefore) },
                { "replacement", proposal["replacement"] }, { "reservationReview", reservationReview } })) throw S.Unavailable();
        var replacement = replacementQuote.Lines.Single();
        // Validating the offered identity also applies to decline; an invalid proposal never
        // becomes valid merely because the decision would have no stock effect.
        var change = OrderSubstitutionPolicy.Replace(catalogBefore, progress, input.Selection, replacement, revision + 1);
        var accepted = decision == OrderSubstitutionDecision.Accept;
        var nextCatalog = accepted ? change.Catalog : catalogBefore;
        var nextTerms = accepted ? row["terms"].AsBsonDocument : termsBefore;
        var nextSpending = spending;
        if (accepted) {
            var rule = nextTerms["spending"];
            nextSpending = OrderSpendingPolicy.Initial(new(Enum.Parse<OrderSpendingMode>(rule["mode"].AsString),
                rule["threshold"].IsBsonNull ? null : new Money(Decimal128.ToDecimal(rule["threshold"].AsDecimal128))), new(Decimal128.ToDecimal(nextTerms["total"].AsDecimal128)));
            OrderSubstitutionPolicy.RequireApprovalCapacity(revision, order["suborderIds"].AsBsonArray.Count, nextSpending);
            if (Money.Add(Money.Add(nextCatalog.CatalogSubtotal, new(Decimal128.ToDecimal(nextTerms["tax"].AsDecimal128))),
                new(Decimal128.ToDecimal(nextTerms["charges"].AsDecimal128))) != new Money(Decimal128.ToDecimal(nextTerms["total"].AsDecimal128))) throw S.Unavailable();
        }
        if (row["catalog"] != B.Quote(nextCatalog) || row["terms"] != nextTerms || row["spendingState"] != nextSpending.ToString() ||
            row["review"] != SubstitutionDecisionBinding(order, proposal, revision, dispatchSequence, decision, nextCatalog, nextTerms)) throw S.Unavailable();
        var releases = row["releases"].AsBsonArray; var allocations = row["allocations"].AsBsonArray; var ids = row["reservationIds"].AsBsonArray;
        if (!accepted) {
            if (releases.Count != 0 || allocations.Count != 0 || ids.Count != 0) throw S.Unavailable();
            return (nextCatalog, nextTerms.DeepClone().AsBsonDocument, nextSpending);
        }
        if (releases.Count == 0 || allocations.Count == 0 || allocations.Count != ids.Count || ids.Distinct().Count() != ids.Count ||
            releases.Select(value => value["reservationId"]).Distinct().Count() != releases.Count ||
            releases.Sum(value => value["allocation"]["packages"].AsInt64) != input.Selection.OriginalPackages ||
            allocations.Sum(value => value["packages"].AsInt64) != replacement.Packages ||
            allocations.Any(value => OrderLineBson.Revision(value) != revision + 1)) throw S.Unavailable();
        foreach (var value in releases) {
            var release = value.AsBsonDocument;
            if (!expected.TryGetValue(release["reservationId"].AsInt64, out var reservation) || reservation["suborderId"] != input.SuborderId.Value ||
                reservation["allocation"]["skuId"] != input.Selection.OriginalSkuId.Value ||
                OrderLineBson.Revision(reservation["allocation"]) != input.Selection.OriginalLineRevision) throw S.Unavailable();
            OrderSubstitutionReservationFacts.RequireRelease(reservation, release, false);
            reservation["remaining"] = checked(reservation["remaining"].AsInt64 - release["allocation"]["packages"].AsInt64);
            reservation["released"] = checked(reservation["released"].AsInt64 + release["allocation"]["packages"].AsInt64);
            reservation["revision"] = checked(reservation["revision"].AsInt64 + 1);
        }
        for (var index = 0; index < allocations.Count; index++) {
            var allocation = B.Allocation(allocations[index].AsBsonDocument); var owner = replacement.FulfillmentOwner;
            if (allocation.SkuId != replacement.Definition.SkuId.Value || allocation.Change.Key.DefinitionId != replacement.Definition.Id ||
                allocation.Pool.Location.OrganizationId.Value != owner.OrganizationId || allocation.Pool.Location.ShopId.Value != owner.ShopId ||
                allocation.Pool.Location.ChannelId.Value != owner.ChannelId || owner.WarehouseId is { } warehouse && allocation.Pool.Location.WarehouseId.Value != warehouse ||
                expected.ContainsKey(ids[index].AsInt64)) throw S.Unavailable();
            var live = actual.SingleOrDefault(value => value["_id"] == ids[index]) ?? throw S.Unavailable();
            if (live["orderId"] != order["_id"] || live["suborderId"] != input.SuborderId.Value || live["allocation"] != allocations[index]) throw S.Unavailable();
            var initial = live.DeepClone().AsBsonDocument; initial["revision"] = 1L; initial["remaining"] = allocations[index]["packages"];
            initial["released"] = 0L; initial["dispatched"] = 0L; expected.Add(ids[index].AsInt64, initial);
        }
        OrderReservationHistoryLimits.Require(expected.Values);
        foreach (var kind in new[] { "release", "reserve" }) {
            var facts = kind == "release" ? releases.Select(value => value["allocation"].AsBsonDocument) : allocations.Select(value => value.AsBsonDocument);
            foreach (var pool in facts.GroupBy(value => value["poolId"].AsInt64)) {
                var source = new InventorySource("ordering.substitution", row["_id"].AsInt64, kind, pool.Key);
                var movement = await Rows("movements").Find(tx, F.Eq("sourceEvent", source.EventKey) & InventoryMovementSchemas.Validator()).SingleOrDefaultAsync(ct) ?? throw S.Unavailable();
                await RequireSubstitutionMovementAsync(row, movement, kind, pool.ToArray(), ct);
            }
        }
        return (nextCatalog, nextTerms.DeepClone().AsBsonDocument, nextSpending);
    }
    internal static Task RequireSubstitutionMovementAsync(BsonDocument row, BsonDocument movement, string kind, IReadOnlyCollection<BsonDocument> facts, CancellationToken ct) {
        var source = new InventorySource("ordering.substitution", row["_id"].AsInt64, kind, movement["physicalPoolId"].AsInt64);
        if (kind is not ("reserve" or "release") || row["decision"] != "Accept" || movement["sourceEvent"] != source.EventKey ||
            movement["sourceRevision"] != row["revision"] || movement["actorId"] != row["actorId"] || movement["actorGrantVersion"] != row["actorGrantVersion"] ||
            movement["recordedAt"].CompareTo(row["recordedAt"]) < 0 || movement["reason"] != row["reason"] || movement["correlationId"] != row["correlationId"] ||
            movement["source"] != new BsonDocument { { "namespace", source.Namespace }, { "documentId", source.DocumentId }, { "eventKind", kind }, { "occurrence", source.Occurrence } } ||
            facts.Count is < 1 or > 100 || facts.Count != movement["legs"].AsBsonArray.Count || facts.Select(value => value["balanceId"]).Distinct().Count() != facts.Count) throw S.Unavailable();
        foreach (var fact in facts.Select(B.Allocation)) {
            var leg = movement["legs"].AsBsonArray.SingleOrDefault(value => value["balanceId"] == fact.Change.Id) ?? throw S.Unavailable();
            var before = InventoryBson.ReadBalance(leg["before"].AsBsonDocument);
            if (fact.Pool.Id != source.Occurrence || movement["poolRevision"] != fact.Pool.Revision || before.Id != fact.Change.Id || before.PhysicalPoolId != fact.Pool.Id ||
                before.Balance.Key != fact.Change.Key || before.Balance.Revision != fact.Change.ExpectedRevision || leg["packages"] != fact.Change.Packages ||
                leg["action"] != (kind == "reserve" ? "Reserve" : "Release")) throw S.Unavailable();
            var after = before with { Balance = kind == "reserve" ? StockBalancePolicy.Reserve(before.Balance, fact.Change.ExpectedRevision, fact.Change.Packages) :
                StockBalancePolicy.Release(before.Balance, fact.Change.ExpectedRevision, fact.Change.Packages) };
            if (leg["after"] != InventoryBson.Balance(after)) throw S.Unavailable();
        }
        ct.ThrowIfCancellationRequested(); return Task.CompletedTask;
    }
}
