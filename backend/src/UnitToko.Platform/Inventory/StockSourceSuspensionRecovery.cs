using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    /// <summary>Looks up the original actor's receipt. It cannot create a missing stop or borrow read permission.</summary>
    public async Task<BsonDocument?> RecoverSuspensionAsync(ActorContext actor, long poolId, string key, string correlationId, CancellationToken ct = default)
    {
        if (poolId <= 0 || !StockSourceSetupPolicy.Text(key, 200) || !StockSourceSetupPolicy.Text(correlationId, 128))
            throw StockSourceSuspensionBson.Invalid();
        await SuspensionStorageAsync(ct);
        var recovered = await authorization.ReadAsync<BsonDocument?>(actor, async (snapshot, tx, token) =>
        {
            var context = await SuspensionContextAsync(actor, snapshot, tx, poolId, Permissions.StockSourceSuspend, token);
            var receipt = await Rows("idempotencyRecords").Find(tx,
                F.Eq("actorId", actor.UserId) & F.Eq("operation", StockSourceSuspensionBson.Operation) & F.Eq("key", key)).SingleOrDefaultAsync(token);
            BsonDocument? row = null;
            if (receipt is not null)
            {
                long id = 0;
                StockSourceSuspensionBson.Guard(() =>
                {
                    if (!receipt["result"].IsBsonDocument || !StockSourceSuspensionBson.Positive(receipt["result"].AsBsonDocument, "suspensionId") ||
                        !StockSourceSuspensionBson.Positive(receipt["result"].AsBsonDocument, "poolId")) throw StockSourceSuspensionBson.Invalid();
                    if (receipt["result"]["poolId"].AsInt64 != poolId) throw new AuthorizationDeniedException("scope_denied");
                    id = receipt["result"]["suspensionId"].AsInt64;
                });
                row = await Rows(StockSourceSuspensionSchemas.Collection).Find(tx, F.Eq("_id", id) & F.Eq("poolId", poolId)).SingleOrDefaultAsync(token)
                    ?? throw StockSourceSuspensionBson.Invalid();
                StockSourceSuspensionBson.Require(row);
                if (row["suspendedBy"].AsInt64 != actor.UserId) throw StockSourceSuspensionBson.Invalid();
                RequireSuspensionReceipt(receipt, row);
            }
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceSuspend, context.Resources, tx, token);
            return row;
        }, ct);
        if (recovered is null) return null;
        var replay = await SuspendCoreAsync(actor, poolId, StockSourceSuspensionBson.Expectation(recovered),
            new(key, recovered["reason"].AsString, correlationId), true, ct);
        if (!replay.Equals(StockSourceSuspensionBson.Result(recovered))) throw StockSourceSuspensionBson.Invalid();
        return replay;
    }
}
