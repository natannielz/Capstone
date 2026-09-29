using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Authorization;

/// <summary>Actual predecessor history for forward-order upgrades and historical generic read probes.</summary>
internal static class LegacyOrderSubmissionTestSchema
{
    internal static async Task ApplyV21Async(IMongoDatabase database, IClock clock) {
        await LegacyCatalogPriceTestSchema.ApplyV19Async(database, clock);
        var durable = database.WithWriteConcern(WriteConcern.WMajority).WithReadPreference(ReadPreference.Primary);
        IMigrationStep[] steps = [new CatalogPriceMigration(durable), new OrderDraftMigration(durable)];
        foreach (var step in steps) {
            var lease = await new MigrationGate(durable, clock, 21).AcquireAsync(step.Version, default);
            if (lease is null) continue;
            try { await step.ApplyAsync(lease, default); await lease.CompleteAsync(default); }
            catch { await lease.ReleaseAsync(); throw; }
        }
    }
}
