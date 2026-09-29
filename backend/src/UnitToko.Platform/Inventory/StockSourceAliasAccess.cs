using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    public async Task<BsonDocument?> ReadAliasProposalAsync(ActorContext actor, long poolId, StockLocation requestedLocation,
        long proposalId, CancellationToken ct = default)
    {
        requestedLocation.Validate();
        if (poolId <= 0 || proposalId <= 0) throw StockSourceAliasBson.Invalid();
        await AliasStorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (snapshot, tx, token) =>
        {
            var resources = await AliasScopesAsync(actor, snapshot, tx, poolId, requestedLocation, Permissions.StockSourceRead, token);
            var selected = await Rows("stockSourceAliasProposals").Find(tx, AliasFilter(poolId, requestedLocation) & F.Eq("_id", proposalId))
                .Project(new BsonDocument("_id", 1)).SingleOrDefaultAsync(token);
            BsonDocument? view = null;
            if (selected is not null)
            {
                view = await AliasViewAsync(tx, proposalId, token);
                RequireAliasBinding(view["proposal"].AsBsonDocument, await CurrentSourceAsync(tx, poolId, token));
            }
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceRead, resources, tx, token);
            return view;
        }, ct);
    }
}
