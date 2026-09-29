using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Authorization;

/// <summary>Actual predecessor chain; no migration receipts are fabricated or removed.</summary>
internal static class LegacyCatalogPriceTestSchema
{
    internal static async Task ApplyV19Async(IMongoDatabase database, IClock clock)
    {
        await LegacyStockSourcePolicyTestSchema.ApplyV18Async(database, clock);
        var durable = database.WithWriteConcern(WriteConcern.WMajority).WithReadPreference(ReadPreference.Primary);
        var lease = await new MigrationGate(durable, clock, 19).AcquireAsync(19, default);
        if (lease is null) return;
        try { await new StockSourcePolicyMigration(durable, clock).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }
}
