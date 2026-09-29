using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
namespace UnitToko.Tests.Authorization;

/// <summary>Runs the actual version-17 chain without creating or deleting deployment receipts.</summary>
internal static class LegacyStockSourceSuspensionTestSchema
{
    internal static async Task ApplyV17Async(IMongoDatabase database, IClock clock)
    {
        await LegacyStockSourceAliasTestSchema.ApplyV16Async(database, clock);
        var durable = database.WithWriteConcern(WriteConcern.WMajority).WithReadPreference(ReadPreference.Primary);
        var lease = await new MigrationGate(durable, clock, 17).AcquireAsync(17, default);
        if (lease is null) return;
        try { await new StockSourceAliasMigration(durable).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }
}