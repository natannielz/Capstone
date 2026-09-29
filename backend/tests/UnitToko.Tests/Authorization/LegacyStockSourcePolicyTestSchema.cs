using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Authorization;

/// <summary>Runs the actual version-18 chain without editing deployment receipts.</summary>
internal static class LegacyStockSourcePolicyTestSchema
{
    internal static async Task ApplyV18Async(IMongoDatabase database, IClock clock)
    {
        await LegacyStockSourceSuspensionTestSchema.ApplyV17Async(database, clock);
        var durable = database.WithWriteConcern(WriteConcern.WMajority).WithReadPreference(ReadPreference.Primary);
        var lease = await new MigrationGate(durable, clock, 18).AcquireAsync(18, default);
        if (lease is null) return;
        try { await new StockSourceSuspensionMigration(durable).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }
}