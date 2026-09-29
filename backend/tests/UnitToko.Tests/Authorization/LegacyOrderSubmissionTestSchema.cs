using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Authorization;

/// <summary>Actual predecessor history for forward-order upgrades and historical generic read probes.</summary>
internal static class LegacyOrderSubmissionTestSchema
{
    internal static async Task ApplyV25Async(IMongoDatabase database, IClock clock) {
        await ApplyV24Async(database, clock);
        var durable = database.WithWriteConcern(WriteConcern.WMajority).WithReadPreference(ReadPreference.Primary);
        var lease = await new MigrationGate(durable, clock, 25).AcquireAsync(25, default);
        if (lease is null) return;
        try { await new IssuedDocumentMigration(durable).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }
    internal static async Task ApplyV24Async(IMongoDatabase database, IClock clock) {
        await ApplyV23Async(database, clock);
        var durable = database.WithWriteConcern(WriteConcern.WMajority).WithReadPreference(ReadPreference.Primary);
        var lease = await new MigrationGate(durable, clock, 24).AcquireAsync(24, default);
        if (lease is null) return;
        try { await new ShipmentDispatchMigration(durable).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }
    internal static async Task ApplyV23Async(IMongoDatabase database, IClock clock) {
        await ApplyV22Async(database, clock);
        var durable = database.WithWriteConcern(WriteConcern.WMajority).WithReadPreference(ReadPreference.Primary);
        var lease = await new MigrationGate(durable, clock, 23).AcquireAsync(23, default);
        if (lease is null) return;
        try { await new ShipmentPreparationMigration(durable).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }
    internal static async Task ApplyV22Async(IMongoDatabase database, IClock clock) {
        await ApplyV21Async(database, clock);
        var durable = database.WithWriteConcern(WriteConcern.WMajority).WithReadPreference(ReadPreference.Primary);
        var lease = await new MigrationGate(durable, clock, 22).AcquireAsync(22, default);
        if (lease is null) return;
        try { await new OrderSubmissionMigration(durable).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }
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
