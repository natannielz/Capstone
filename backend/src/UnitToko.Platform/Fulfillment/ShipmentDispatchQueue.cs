using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Ordering;
using B = UnitToko.Platform.Fulfillment.ShipmentPreparationBson;

namespace UnitToko.Platform.Fulfillment;

public sealed record ShipmentDispatchQueueItem(LongId PreparationId, long PreparationRevision, FulfillmentOrderView Source,
    ImmutableArray<ShipmentPackageSelection> Lines, bool CanDispatch);

public sealed partial class ShipmentPreparationService
{
    internal async Task<(ImmutableArray<ShipmentDispatchQueueItem> Items, bool HasMore)> ListForDispatchAsync(ActorContext actor, long? beforeId, CancellationToken ct) {
        if (beforeId is <= 0) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var query = ScopePolicy.Compile(actor, snapshot, Permissions.ShipmentDispatch, clock.UtcNow); AuthorizationStore.Require(query);
            var filter = ScopeQuery.Create(query); if (beforeId.HasValue) filter &= F.Lt("_id", beforeId.Value);
            // Promote only the saved scope for the shared scope compiler. Filter before pagination,
            // source reads or profile disclosure; then verify the original head and owner graph.
            var rows = await Rows(B.Heads).Aggregate(tx).AppendStage<BsonDocument>(new BsonDocument("$replaceRoot", new BsonDocument("newRoot",
                new BsonDocument("$mergeObjects", new BsonArray { "$$ROOT", "$scope" })))).Match(filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = ImmutableArray.CreateBuilder<ShipmentDispatchQueueItem>(); var sources = new List<FulfillmentOrderSource>();
            foreach (var row in rows) {
                var head = await HeadAsync(tx, row["suborderId"].AsInt64, token) ?? throw B.Invalid();
                if (head["_id"] != row["_id"]) throw B.Invalid();
                var version = await VersionAsync(tx, head["versionId"].AsInt64, token);
                var source = await orders.ReadForFulfillmentAsync(actor, snapshot, tx, head["suborderId"].AsInt64, Permissions.ShipmentDispatch, token);
                if (!CatalogEqual(head["scope"], B.Scope(source.Resource))) throw B.Invalid();
                await TouchAsync(tx, head, token); sources.Add(source);
                items.Add(new(new(head["_id"].AsInt64), head["revision"].AsInt64, source.View, B.Lines(version["lines"].AsBsonArray),
                    source.View.CanPrepare && source.View.Review == version["source"]["review"].AsString));
            }
            await orders.FenceFulfillmentAsync(actor, snapshot, tx, sources, Permissions.ShipmentDispatch, token);
            return (items.Take(25).ToImmutableArray(), rows.Count > 25);
        }, ct);
    }
}
