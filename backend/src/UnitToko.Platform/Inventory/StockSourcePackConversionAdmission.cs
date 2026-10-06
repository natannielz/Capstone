using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    internal async Task<BsonDocument> ReadPackConversionSourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        GrantScope requested, long poolId, long expectedRevision, CancellationToken ct) {
        new InventoryRepository(database).RequireSession(tx); await SuspensionStorageAsync(ct);
        var context = await SuspensionContextAsync(actor, authority, tx, poolId, Permissions.StockConvert, ct);
        if (context.Pool["revision"] != expectedRevision) throw PackConversionRuleBson.Changed();
        var target = StockCountLocation(requested); var configured = StockSourceSetupBson.ReadInput(context.Current["input"].AsBsonDocument);
        if (configured.Location != target && !context.Configuration["mappings"].AsBsonArray.Any(alias => alias["requestedLocation"] == StockSourceSetupBson.Location(target) &&
            alias["enabled"] == true && alias["poolRevision"] == expectedRevision)) throw new AuthorizationDeniedException("scope_denied");
        Require(actor, authority, Permissions.StockConvert, Resource(target));
        await CompleteAuthorityAsync(actor, authority, Permissions.StockConvert, context.Resources, tx, ct);
        return new() { { "poolId", poolId }, { "revision", expectedRevision }, { "active", context.Pool["active"] },
            { "configurationHash", StockSourceSuspensionBson.Hash(context.Configuration) } };
    }
}
