using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Authorization;

/// <summary>Actual predecessor history for forward-order upgrades and historical generic read probes.</summary>
internal static class LegacyOrderSubmissionTestSchema
{
    internal static async Task ApplyV42Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) {
        await ApplyV41Async(database, clock, ct); await ApplyAsync(database, clock, new PaymentManualReviewMigration(database), ct);
    }
    internal static Task UpgradeV42Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) => ApplyAsync(database, clock, new PaymentManualReviewMigration(database), ct);
    internal static async Task ApplyV43Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) {
        await ApplyV42Async(database, clock, ct); await UpgradeV43Async(database, clock, ct);
    }
    internal static Task UpgradeV43Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) => ApplyAsync(database, clock, new PaymentReviewActionMigration(database), ct);
    internal static async Task ApplyV44Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) {
        await ApplyV43Async(database, clock, ct); await UpgradeV44Async(database, clock, ct);
    }
    internal static Task UpgradeV44Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) => ApplyAsync(database, clock, new PaymentReplacementMigration(database), ct);
    internal static async Task ApplyV45Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) {
        await ApplyV44Async(database, clock, ct); await UpgradeV45Async(database, clock, ct);
    }
    internal static Task UpgradeV45Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) => ApplyAsync(database, clock, new PaymentNotificationMigration(database), ct);
    internal static Task UpgradeV46Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) => ApplyAsync(database, clock, new PaymentReviewExplanationMigration(database), ct);
    internal static Task UpgradeV47Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) => ApplyAsync(database, clock, new AfterSalesCaseMigration(database), ct);
    internal static Task UpgradeV48Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) => ApplyAsync(database, clock, new AfterSalesDecisionMigration(database), ct);
    internal static Task UpgradeV49Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) => ApplyAsync(database, clock, new ReturnQuarantineMigration(database), ct);
    internal static Task UpgradeV50Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) => ApplyAsync(database, clock, new ReturnInspectionMigration(database, clock), ct);
    internal static Task UpgradeV51Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) => ApplyAsync(database, clock, new ReturnResolutionMigration(database, clock), ct);
    internal static async Task ApplyV41Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) {
        await ApplyV40Async(database, clock, ct); await ApplyAsync(database, clock, new PaymentAllocationMigration(database), ct);
    }
    internal static async Task ApplyV40Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) {
        await ApplyV39Async(database, clock, ct); await ApplyAsync(database, clock, new PaymentVerificationMigration(database), ct);
    }
    internal static async Task ApplyV39Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) {
        await ApplyV38Async(database, clock, ct); await ApplyAsync(database, clock, new BankReceiptMigration(database), ct);
    }
    internal static async Task ApplyV38Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) {
        await ApplyV37Async(database, clock, ct); await ApplyAsync(database, clock, new BankStatementMigration(database), ct);
    }
    internal static async Task ApplyV37Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) {
        await ApplyV36Async(database, clock, ct); await ApplyAsync(database, clock, new ReceivingAccountMigration(database), ct);
    }
    internal static async Task ApplyV36Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) {
        await ApplyV35Async(database, clock, ct); await ApplyAsync(database, clock, new PaymentCorrectionMigration(database), ct);
    }
    internal static async Task ApplyV35Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) {
        await ApplyV34Async(database, clock, ct); await ApplyAsync(database, clock, new DocumentProcessingMigration(database), ct);
    }
    internal static async Task ApplyV34Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) {
        await ApplyV33Async(database, clock, ct); await UpgradeV34Async(database, clock, ct);
    }
    internal static Task UpgradeV34Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) => ApplyAsync(database, clock, new PaymentSubmissionMigration(database), ct);
    internal static async Task ApplyV33Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) {
        await ApplyV32Async(database, clock);
        await UpgradeV33Async(database, clock, ct);
    }
    internal static Task UpgradeV33Async(IMongoDatabase database, IClock clock, CancellationToken ct = default) => ApplyAsync(database, clock, new EvidenceAccessMigration(database), ct);
    internal static async Task ApplyV32Async(IMongoDatabase database, IClock clock) {
        await ApplyV31Async(database, clock);
        await ApplyAsync(database, clock, new EvidenceValidationMigration(database));
    }
    internal static async Task ApplyV31Async(IMongoDatabase database, IClock clock) {
        await ApplyV30Async(database, clock);
        await ApplyAsync(database, clock, new PaymentEvidenceMigration(database));
    }
    internal static async Task ApplyV30Async(IMongoDatabase database, IClock clock) {
        await ApplyV29Async(database, clock);
        await ApplyAsync(database, clock, new PaymentDraftMigration(database));
    }
    internal static async Task ApplyV29Async(IMongoDatabase database, IClock clock) {
        await ApplyV28Async(database, clock);
        await ApplyAsync(database, clock, new InvoiceMigration(database));
    }
    private static async Task ApplyAsync(IMongoDatabase database, IClock clock, IMigrationStep step, CancellationToken ct = default) {
        var lease = await new MigrationGate(database, clock, step.Version).AcquireAsync(step.Version, ct);
        if (lease is null) return;
        try { await step.ApplyAsync(lease, ct); await lease.CompleteAsync(ct); }
        catch { await lease.ReleaseAsync(); throw; }
    }
    internal static async Task ApplyV28Async(IMongoDatabase database, IClock clock) {
        await ApplyV27Async(database, clock);
        var durable = database.WithWriteConcern(WriteConcern.WMajority).WithReadPreference(ReadPreference.Primary);
        var lease = await new MigrationGate(durable, clock, 28).AcquireAsync(28, default);
        if (lease is null) return;
        try { await new DeliveryAttemptMigration(durable).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }
    internal static async Task ApplyV27Async(IMongoDatabase database, IClock clock) {
        await ApplyV26Async(database, clock);
        var durable = database.WithWriteConcern(WriteConcern.WMajority).WithReadPreference(ReadPreference.Primary);
        var lease = await new MigrationGate(durable, clock, 27).AcquireAsync(27, default);
        if (lease is null) return;
        try { await new IssuedReceiptProofMigration(durable).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }
    internal static async Task ApplyV26Async(IMongoDatabase database, IClock clock) {
        await ApplyV25Async(database, clock);
        var durable = database.WithWriteConcern(WriteConcern.WMajority).WithReadPreference(ReadPreference.Primary);
        var lease = await new MigrationGate(durable, clock, 26).AcquireAsync(26, default);
        if (lease is null) return;
        try { await new ShipmentReceiptMigration(durable).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }
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
