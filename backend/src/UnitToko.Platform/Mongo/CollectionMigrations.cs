using MongoDB.Driver;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Mongo;

// Run explicitly during deployment, never from an API request or application startup.
public sealed class CollectionMigrations(IMongoDatabase database, IClock clock)
{
    // Deployment history and document format versions are independent. Adding a migration
    // must not silently make existing collection writers produce a new document format.
    public const int CurrentVersion = 41;
    public const int FoundationDocumentSchemaVersion = 1;
    public const int AuthorizationDocumentSchemaVersion = 1;

    public Task<bool> IsCurrentAsync(CancellationToken cancellationToken = default) =>
        new MigrationGate(database, clock, CurrentVersion).IsCurrentAsync(cancellationToken);

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        var durableDatabase = database.WithWriteConcern(WriteConcern.WMajority)
            .WithReadPreference(ReadPreference.Primary);
        IMigrationStep[] steps =
        [
            new FoundationMigration(durableDatabase),
            new AuthorizationMigration(durableDatabase),
            new AuthorizationLifecycleMigration(durableDatabase),
            new IdentityMigration(durableDatabase),
            new ProviderLifecycleMigration(durableDatabase),
            new CatalogVisibilityMigration(durableDatabase),
            new HistoricalReadMigration(durableDatabase),
            new PersonAuthorityMigration(durableDatabase),
            new PersonGovernanceMigration(durableDatabase),
            new PersonReviewPreviewMigration(durableDatabase),
            new CatalogMigration(durableDatabase),
            new AccountingMigration(durableDatabase),
            new EvidenceMigration(durableDatabase),
            new StockCountMigration(durableDatabase),
            new StockSourceSetupMigration(durableDatabase),
            new StockSourceRevisionMigration(durableDatabase),
            new StockSourceAliasMigration(durableDatabase),
            new StockSourceSuspensionMigration(durableDatabase),
            new StockSourcePolicyMigration(durableDatabase, clock),
            new CatalogPriceMigration(durableDatabase),
            new OrderDraftMigration(durableDatabase),
            new OrderSubmissionMigration(durableDatabase),
            new ShipmentPreparationMigration(durableDatabase),
            new ShipmentDispatchMigration(durableDatabase),
            new IssuedDocumentMigration(durableDatabase),
            new ShipmentReceiptMigration(durableDatabase),
            new IssuedReceiptProofMigration(durableDatabase),
            new DeliveryAttemptMigration(durableDatabase),
            new InvoiceMigration(durableDatabase),
            new PaymentDraftMigration(durableDatabase),
            new PaymentEvidenceMigration(durableDatabase),
            new EvidenceValidationMigration(durableDatabase),
            new EvidenceAccessMigration(durableDatabase),
            new PaymentSubmissionMigration(durableDatabase),
            new DocumentProcessingMigration(durableDatabase),
            new PaymentCorrectionMigration(durableDatabase),
            new ReceivingAccountMigration(durableDatabase),
            new BankStatementMigration(durableDatabase),
            new BankReceiptMigration(durableDatabase),
            new PaymentVerificationMigration(durableDatabase),
            new PaymentAllocationMigration(durableDatabase)
        ];
        if (!steps.Select(step => step.Version).SequenceEqual(Enumerable.Range(1, CurrentVersion)))
            throw new InvalidOperationException("Schema migration registry must contain every version in order");
        var gate = new MigrationGate(database, clock, CurrentVersion);
        foreach (var step in steps)
        {
            var lease = await gate.AcquireAsync(step.Version, cancellationToken);
            if (lease is null) continue;
            try
            {
                await step.ApplyAsync(lease, cancellationToken);
                await lease.CompleteAsync(cancellationToken);
            }
            catch
            {
                try { await lease.ReleaseAsync(); }
                catch (MongoException) { /* Preserve failure; lease expiry enables recovery. */ }
                catch (OperationCanceledException) { /* Bounded cleanup must not hide failure. */ }
                throw;
            }
        }
    }
}
