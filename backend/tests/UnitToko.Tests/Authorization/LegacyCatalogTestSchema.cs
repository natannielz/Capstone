using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Authorization;

// Only legacy metadata defenses use this pinned schema. Current producers use the complete registered chain.
internal static class LegacyCatalogTestSchema
{
    internal static async Task ApplyV6Async(IMongoDatabase database, IClock clock)
    {
        var durable = database.WithWriteConcern(WriteConcern.WMajority);
        IMigrationStep[] steps = [new FoundationMigration(durable), new AuthorizationMigration(durable),
            new AuthorizationLifecycleMigration(durable), new IdentityMigration(durable),
            new ProviderLifecycleMigration(durable), new CatalogVisibilityMigration(durable)];
        var gate = new MigrationGate(database, clock, 6);
        foreach (var step in steps)
        {
            var lease = await gate.AcquireAsync(step.Version, CancellationToken.None);
            if (lease is null) continue;
            await step.ApplyAsync(lease, CancellationToken.None);
            await lease.CompleteAsync(CancellationToken.None);
        }
    }
}
