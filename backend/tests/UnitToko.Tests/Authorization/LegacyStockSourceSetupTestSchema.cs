using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Authorization;

/// <summary>Actual historical deployment steps, never forged or deleted applied history.</summary>
internal static class LegacyStockSourceSetupTestSchema
{
    internal static async Task ApplyV14Async(IMongoDatabase database, IClock clock)
    {
        var durable = database.WithWriteConcern(WriteConcern.WMajority).WithReadPreference(ReadPreference.Primary);
        IMigrationStep[] steps =
        [
            new FoundationMigration(durable), new AuthorizationMigration(durable), new AuthorizationLifecycleMigration(durable),
            new IdentityMigration(durable), new ProviderLifecycleMigration(durable), new CatalogVisibilityMigration(durable),
            new HistoricalReadMigration(durable), new PersonAuthorityMigration(durable), new PersonGovernanceMigration(durable),
            new PersonReviewPreviewMigration(durable), new CatalogMigration(durable), new AccountingMigration(durable),
            new EvidenceMigration(durable), new StockCountMigration(durable)
        ];
        var gate = new MigrationGate(database, clock, 14);
        foreach (var step in steps)
        {
            var lease = await gate.AcquireAsync(step.Version, default);
            if (lease is null) continue;
            try { await step.ApplyAsync(lease, default); await lease.CompleteAsync(default); }
            catch { await lease.ReleaseAsync(); throw; }
        }
    }
}
