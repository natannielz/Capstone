using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Platform.Catalog;

public sealed partial class CatalogAuthoringService
{
    // Recovery proves the current canonical owner without requiring a once-active SKU to
    // remain active or its definition to remain current. New associations use the stricter
    // CatalogImage upload target bridge instead.
    internal async Task<T> WithCatalogPhotoRecoveryOwnerAsync<T>(IMongoDatabase commandDatabase, IClientSessionHandle tx,
        ActorContext actor, long skuId, Func<GrantScope, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        if (tx is null || !tx.IsInTransaction || commandDatabase is null ||
            !ReferenceEquals(tx.Client, database.Client) || !ReferenceEquals(commandDatabase.Client, database.Client) ||
            !commandDatabase.DatabaseNamespace.Equals(database.DatabaseNamespace) || skuId <= 0)
            throw CatalogRepository.Invalid("catalog_photo_owner_unavailable");
        var context = await BeginAsync(actor, tx, ct);
        var sku = await MasterAsync(context, "skus", skuId, ct);
        var owner = RowOwner(sku);
        var result = await read(owner, ct);
        await CompleteAsync(context, ct);
        return result;
    }
}
