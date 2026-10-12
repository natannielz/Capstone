using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;
using S = UnitToko.Platform.Ordering.OrderSubstitutionBson;

namespace UnitToko.Platform.Ordering;

public sealed record OrderSubstitutionTargetLine(OrderQuotedLine Catalog, int RemainingPackages);
public sealed record OrderSubstitutionTarget(LongId OrderId, LongId SuborderId, long Revision, long DispatchSequence, string BuyerName,
    ImmutableArray<OrderSubstitutionTargetLine> Lines, bool CanPropose);
public sealed record OrderSubstitutionHistoryItem(LongId ProposalId, LongId OrderId, LongId SuborderId, string BuyerName,
    OrderSubstitutionInput Input, OrderQuotedLine Original, OrderQuotedLine Replacement, string Status, LongId? DecisionId, DateTimeOffset ProposedAt, DateTimeOffset? DecidedAt);

public sealed partial class OrderSubmissionService
{
    public async Task<(ImmutableArray<OrderSubstitutionTarget> Items, bool HasMore)> ListSubstitutionTargetsAsync(ActorContext actor,
        long? beforeId = null, CancellationToken ct = default) {
        if (beforeId is <= 0) throw S.Unavailable(); await SubstitutionStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var capturedAt = clock.UtcNow;
            var filter = ScopeQuery.Create(SubstitutionQuery(actor, snapshot, Permissions.SubstitutionPropose));
            if (beforeId.HasValue) filter &= F.Lt("_id", beforeId.Value);
            var rows = await Rows(B.Suborders).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var result = ImmutableArray.CreateBuilder<OrderSubstitutionTarget>();
            foreach (var row in rows) {
                var context = await SubstitutionContextAsync(actor, snapshot, tx, row["orderId"].AsInt64, row["_id"].AsInt64, Permissions.SubstitutionPropose, token);
                var owner = CatalogRepository.Owner(row["owner"].AsBsonDocument);
                var lines = context.Catalog.Lines.Where(line => line.FulfillmentOwner == owner).Select(line => new OrderSubstitutionTargetLine(line,
                    checked((int)context.Reservations.Where(value => OrderLineBson.Identity(value["allocation"]) == OrderLineIdentity.Of(line)).Sum(value => value["remaining"].AsInt64)))).ToImmutableArray();
                await FenceSubstitutionAsync(context, token);
                result.Add(new(new(row["orderId"].AsInt64), new(row["_id"].AsInt64), context.Progress.Revision, context.Progress.DispatchSequence,
                    context.Order["terms"]["buyer"]["name"].AsString, lines, !context.Progress.Cancelled &&
                    OrderSpendingPolicy.CanAcceptAtStore(context.Progress.SpendingState) && !context.Progress.RejectedSuborders.Contains(new(row["_id"].AsInt64)) &&
                    context.Progress.Revision < 103 && lines.Any(line => line.RemainingPackages > 0)));
            }
            await FenceSubstitutionQueryAsync(actor, snapshot, tx, Permissions.SubstitutionPropose, capturedAt, token);
            return (result.Take(25).ToImmutableArray(), rows.Count > 25);
        }, ct);
    }
    public async Task<(OrderQuotedLine[] Items, bool HasMore)> SubstitutionPackagesAsync(ActorContext actor, long orderId, long suborderId,
        string? search = null, long? afterId = null, CancellationToken ct = default) {
        await SubstitutionStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var context = await SubstitutionContextAsync(actor, snapshot, tx, orderId, suborderId, Permissions.SubstitutionPropose, token);
            return await new CatalogSubstitutionReader(database, clock).QueryAsync(actor, snapshot, tx,
                CatalogRepository.Owner(context.Suborder["owner"].AsBsonDocument),
                search, afterId, fence => FenceSubstitutionAsync(context, fence), token);
        }, ct);
    }
    public async Task<(ImmutableArray<OrderSubstitutionHistoryItem> Items, bool HasMore)> ListSubstitutionsAsync(ActorContext actor,
        bool forBuyer, long? orderId = null, long? beforeId = null, CancellationToken ct = default) {
        if (orderId is <= 0 || beforeId is <= 0) throw S.Unavailable(); await SubstitutionStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var capturedAt = clock.UtcNow;
            var permission = forBuyer ? Permissions.SubstitutionAccept : Permissions.SubstitutionPropose;
            var filter = ScopeQuery.Create(SubstitutionQuery(actor, snapshot, permission));
            if (orderId.HasValue) filter &= F.Eq("orderId", orderId.Value);
            if (beforeId.HasValue) filter &= F.Lt("_id", beforeId.Value);
            var rows = await Rows(S.Proposals).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var result = ImmutableArray.CreateBuilder<OrderSubstitutionHistoryItem>(); var records = new OrderSubstitutionRecords(database, clock);
            foreach (var selected in rows) {
                var context = await SubstitutionContextAsync(actor, snapshot, tx, selected["orderId"].AsInt64, selected["suborderId"].AsInt64, permission, token);
                var proposal = await records.ReadAsync(tx, S.Proposals, selected["_id"].AsInt64, context.Order, token);
                var input = S.Input(proposal["input"].AsBsonDocument);
                var decisions = await Rows(S.Decisions).Find(tx, F.Eq("proposalId", proposal["_id"])).Limit(2).ToListAsync(token);
                if (decisions.Count > 1) throw S.Unavailable();
                var decision = decisions.Count == 0 ? null : await records.ReadAsync(tx, S.Decisions, decisions[0]["_id"].AsInt64, context.Order, token);
                var status = decision is not null ? decision["decision"] == "Accept" ? "accepted" : "declined" :
                    input.ExpectedOrderRevision != context.Progress.Revision || input.ExpectedDispatchSequence != context.Progress.DispatchSequence || context.Progress.Cancelled ? "stale" : "pending";
                result.Add(new(new(proposal["_id"].AsInt64), input.OrderId, input.SuborderId, context.Order["terms"]["buyer"]["name"].AsString, input,
                    B.Quote(proposal["catalogBefore"].AsBsonDocument).Lines.Single(line => line.Definition.SkuId == input.Selection.OriginalSkuId && line.LineRevision == input.Selection.OriginalLineRevision),
                    B.Quote(proposal["replacement"].AsBsonDocument).Lines.Single(), status, decision is null ? null : new(decision["_id"].AsInt64),
                    OrderDraftBson.Time(proposal["recordedAt"]), decision is null ? null : OrderDraftBson.Time(decision["recordedAt"])));
                await FenceSubstitutionAsync(context, token);
            }
            await FenceSubstitutionQueryAsync(actor, snapshot, tx, permission, capturedAt, token);
            return (result.Take(25).ToImmutableArray(), rows.Count > 25);
        }, ct);
    }
    private async Task FenceSubstitutionQueryAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx,
        string permission, DateTimeOffset capturedAt, CancellationToken ct) {
        var query = SubstitutionQuery(actor, snapshot, permission);
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
            query.Clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            snapshot.Grants.Where(grant => query.Clauses.Any(clause => clause.GrantId == grant.Id)),
            snapshot.Memberships.Where(member => query.Clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        _ = SubstitutionQuery(actor, snapshot, permission);
        if (clock.UtcNow < capturedAt) throw S.Unavailable(); ct.ThrowIfCancellationRequested();
    }
}
