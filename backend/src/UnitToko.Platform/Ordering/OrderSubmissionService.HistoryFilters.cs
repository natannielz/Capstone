using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;
namespace UnitToko.Platform.Ordering;
public sealed record FilteredOrderPage(ImmutableArray<SubmittedOrderView> Items, bool HasMore, string? NextCursor);
public sealed partial class OrderSubmissionService
{
    public Task<ImmutableArray<OrderBuyingOption>> HistoryContextsAsync(ActorContext actor, CancellationToken ct = default) =>
        authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var query = ScopePolicy.Compile(actor, snapshot, Permissions.OrderRead, clock.UtcNow); AuthorizationStore.Require(query);
            var contexts = new List<OrderBuyingContext>();
            foreach (var branch in snapshot.Directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Branch && node.ParentId is > 0).OrderBy(node => node.Id)) {
                var options = new[] { new OrderBuyingContext(new(branch.ParentId!.Value), new(branch.Id)) }
                    .Concat(snapshot.Directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Division && node.ParentId == branch.Id).OrderBy(node => node.Id)
                        .Select(node => new OrderBuyingContext(new(branch.ParentId.Value), new(branch.Id), new(node.Id))));
                foreach (var option in options) if (query.Matches(option.Resource(actor.UserId))) contexts.Add(option);
            }
            if (contexts.Count > 256) throw new AuthorizationDeniedException("order_context_limit");
            var ids = contexts.SelectMany(context => new long?[] { context.OrganizationId.Value, context.BranchId.Value, context.DivisionId?.Value }).OfType<long>().Distinct();
            var profiles = await database.GetCollection<BsonDocument>("organizationProfiles").Find(tx,
                Builders<BsonDocument>.Filter.In("_id", ids)).ToListAsync(token);
            string? Name(long? id) {
                var profile = profiles.SingleOrDefault(row => row["_id"].AsInt64 == id);
                return profile is not null && profile.TryGetValue("name", out var name) && name.IsString ? name.AsString : null;
            }
            var result = contexts.Select(context => new OrderBuyingOption(context, Name(context.OrganizationId.Value), Name(context.BranchId.Value), Name(context.DivisionId?.Value))).ToImmutableArray();
            await authority.FenceAsync(actor, snapshot, tx, contexts, token, Permissions.OrderRead);
            return result;
        }, ct);

    public async Task<FilteredOrderPage> ListOwnFilteredAsync(ActorContext actor, OrderHistoryFilter filterBy, string? cursor = null, CancellationToken ct = default) {
        if (filterBy is null) throw OrderHistoryFilter.Invalid(); filterBy.Validate(); var boundary = filterBy.ParseCursor(cursor); var context = filterBy.Context; await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var query = ScopePolicy.Compile(actor, snapshot, Permissions.OrderRead, clock.UtcNow); AuthorizationStore.Require(query);
            var filter = ScopeQuery.Create(query) & F.Eq("createdBy", actor.UserId);
            if (context is not null) {
                authority.Require(actor, snapshot, context, Permissions.OrderRead);
                filter &= F.Eq("buyingOrganizationId", context.OrganizationId.Value) & F.Eq("branchId", context.BranchId.Value) &
                    F.Eq<BsonValue>("divisionId", context.DivisionId.HasValue ? new BsonInt64(context.DivisionId.Value.Value) : BsonNull.Value);
            }
            if (boundary is { } boundaryId) {
                if (!await Rows(B.Orders).Find(tx, filter & F.Eq("_id", boundaryId)).AnyAsync(token)) throw OrderHistoryFilter.Invalid();
                var owner = await GraphAsync(tx, actor, boundaryId, token); await TouchAsync(tx, owner, token);
                await authority.FenceAsync(actor, snapshot, tx, [OrderDraftBson.Context(owner["input"]["context"].AsBsonDocument)], token, Permissions.OrderRead);
                filter &= filterBy.Sort == "newest" ? F.Lt("_id", boundaryId) : F.Gt("_id", boundaryId);
            }
            var rows = await Rows(B.Orders).Find(tx, filter).Sort(new BsonDocument("_id", filterBy.Sort == "newest" ? -1 : 1)).Limit(26).ToListAsync(token);
            var result = ImmutableArray.CreateBuilder<SubmittedOrderView>();
            foreach (var row in rows.Take(25)) { var valid = await GraphAsync(tx, actor, row["_id"].AsInt64, token); await TouchAsync(tx, valid, token); result.Add(View(valid, await DecisionGraphAsync(tx, valid, token))); }
            // The extra row influences HasMore, so its authority must be fenced too.
            var contexts = rows.Select(row => OrderDraftBson.Context(row["input"]["context"].AsBsonDocument)).Concat(context is null ? [] : new[] { context }).Distinct().ToArray();
            await authority.FenceAsync(actor, snapshot, tx, contexts, token, Permissions.OrderRead); return new FilteredOrderPage(result.ToImmutable(), rows.Count > 25, rows.Count > 25 ? filterBy.Cursor(rows[24]["_id"].AsInt64) : null);
        }, ct);
    }
}
