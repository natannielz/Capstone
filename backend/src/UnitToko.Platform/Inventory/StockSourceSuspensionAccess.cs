using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;
namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    public async Task<AuthorizedPage> ListSuspensionsAsync(ActorContext actor, long poolId, int offset, int limit, CancellationToken ct = default)
    {
        if (poolId <= 0) throw StockSourceSuspensionBson.Invalid();
        AuthorizationStore.ValidatePage(offset, limit); await SuspensionStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            var context = await SuspensionContextAsync(actor, snapshot, tx, poolId, Permissions.StockSourceRead, token);
            var filter = F.Eq("poolId", poolId);
            var total = await Rows(StockSourceSuspensionSchemas.Collection).CountDocumentsAsync(tx, filter, cancellationToken: token);
            var items = await Rows(StockSourceSuspensionSchemas.Collection).Find(tx, filter)
                .Sort(new BsonDocument("suspensionRevision", -1)).Skip(offset).Limit(limit).ToListAsync(token);
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceRead, context.Resources, tx, token);
            return new AuthorizedPage(total, items);
        }, ct);
    }
    public async Task<BsonDocument?> ReadSuspensionDetailAsync(ActorContext actor, long poolId, long suspensionId, CancellationToken ct = default)
    {
        if (poolId <= 0 || suspensionId <= 0) throw StockSourceSuspensionBson.Invalid();
        await SuspensionStorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (snapshot, tx, token) =>
        {
            // All current and historical channel scopes are required before loading private history.
            var context = await SuspensionContextAsync(actor, snapshot, tx, poolId, Permissions.StockSourceRead, token);
            var row = await Rows(StockSourceSuspensionSchemas.Collection).Find(tx, F.Eq("poolId", poolId) & F.Eq("_id", suspensionId)).SingleOrDefaultAsync(token);
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceRead, context.Resources, tx, token);
            return row;
        }, ct);
    }
}