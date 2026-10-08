using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Catalog;
using R = UnitToko.Platform.Inventory.ReplenishmentRecords;

namespace UnitToko.Platform.Inventory;

public sealed partial class ReplenishmentService
{
    public async Task<(ImmutableArray<ReplenishmentEvaluation> Items, long? NextBefore)> HistoryAsync(ActorContext actor,
        GrantScope location, long? before, int limit, CancellationToken ct = default)
    {
        if (before is <= 0 || limit is < 1 or > 10) throw R.Invalid();
        await R.RequireStorageAsync(database, ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            _ = Require(actor, authority, location);
            var query = F.Eq("input.location", CatalogRepository.Owner(location));
            if (before is { } cursor) query &= F.Lt("_id", cursor);
            var rows = await Rows(R.Collection).Find(tx, query).Sort(new BsonDocument("_id", -1)).Limit(limit + 1).ToListAsync(token);
            var results = ImmutableArray.CreateBuilder<ReplenishmentEvaluation>();
            foreach (var row in rows.Take(limit)) results.Add(await GraphAsync(tx, row, token));
            await FenceAsync(actor, authority, tx, location, token);
            return (results.ToImmutable(), rows.Count > limit ? (long?)results[^1].Id : null);
        }, ct);
    }
}
