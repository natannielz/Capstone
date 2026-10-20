using MongoDB.Driver;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Mongo;

// Run explicitly during deployment, never from an API request or application startup.
public sealed class CollectionMigrations(IMongoDatabase database, IClock clock)
{
    // Deployment history and document format versions are independent. Adding a migration
    // must not silently make existing collection writers produce a new document format.
    public const int CurrentVersion = 121;
    public const int FoundationDocumentSchemaVersion = 1;
    public const int AuthorizationDocumentSchemaVersion = 1;

    public Task<bool> IsCurrentAsync(CancellationToken cancellationToken = default) =>
        new MigrationGate(database, clock, CurrentVersion).IsCurrentAsync(cancellationToken);

    internal IReadOnlyList<IMigrationStep> RegisteredSteps()
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
            new PaymentAllocationMigration(durableDatabase),
            new PaymentManualReviewMigration(durableDatabase),
            new PaymentReviewActionMigration(durableDatabase),
            new PaymentReplacementMigration(durableDatabase),
            new PaymentNotificationMigration(durableDatabase),
            new PaymentReviewExplanationMigration(durableDatabase),
            new AfterSalesCaseMigration(durableDatabase),
            new AfterSalesDecisionMigration(durableDatabase),
            new ReturnQuarantineMigration(durableDatabase),
            new ReturnInspectionMigration(durableDatabase, clock),
            new ReturnResolutionMigration(durableDatabase, clock),
            new PaymentAllocationCorrectionMigration(durableDatabase),
            new ReturnEntitlementMigration(durableDatabase, clock),
            new ReturnFinancialApprovalMigration(durableDatabase, clock),
            new RefundPayoutMigration(durableDatabase, clock),
            new BankTransactionClaimMigration(durableDatabase),
            new RefundSettlementMigration(durableDatabase, clock),
            new OrganizationAddressMigration(durableDatabase),
            new OrderSpendingPolicyMigration(durableDatabase),
            new AccountingBookSetupMigration(durableDatabase),
            new ProcurementRequestMigration(durableDatabase),
            new SupplierMigration(durableDatabase),
            new PurchaseOrderMigration(durableDatabase),
            new ProcurementFundingMigration(durableDatabase),
            new SupplierBankMigration(durableDatabase),
            new ProcurementDisbursementMigration(durableDatabase, clock),
            new ProcurementSettlementMigration(durableDatabase, clock),
            new ProcurementReceivingMigration(durableDatabase, clock),
            new ProcurementInspectionMigration(durableDatabase, clock),
            new SupplierDocumentMigration(durableDatabase, clock),
            new SupplierClaimMigration(durableDatabase, clock),
            new ProcurementStockReceiptMigration(durableDatabase, clock),
            new ProcurementStockValuationMigration(durableDatabase, clock),
            new StockSourceReconciliationMigration(durableDatabase, clock),
            new ProcurementSupplierPayableMigration(durableDatabase, clock),
            new ProcurementAdvanceReturnMigration(durableDatabase, clock),
            new ProcurementQualityAcceptanceMigration(durableDatabase, clock),
            new BranchOrderRequestMigration(durableDatabase),
            new ProcurementQualityValuationMigration(durableDatabase, clock),
            new CatalogPhotoMigration(durableDatabase),
            new EvidenceImageRevalidationMigration(durableDatabase),
            new AccountContactMigration(durableDatabase),
            new PdfEvidenceValidationMigration(durableDatabase),
            new AccountingReportMigration(durableDatabase),
            new PdfEvidenceAdmissionMigration(durableDatabase),
            new PdfDocumentExtractionMigration(durableDatabase),
            new PaymentSupplementMigration(durableDatabase),
            new PaymentSupplementCorrectionMigration(durableDatabase),
            new SupplierDocumentPdfMatchMigration(durableDatabase, clock),
            new OrderStockSourceMigration(durableDatabase, clock),
            new ReconciliationStockSourceMigration(durableDatabase, clock),
            new FulfillmentStockSourceMigration(durableDatabase, clock),
            new ProcurementStockSourceMigration(durableDatabase, clock),
            new PurchaseOrderInboundBindingMigration(durableDatabase, clock),
            new AfterSalesStockSourceMigration(durableDatabase, clock),
            new InventoryCountStockSourceMigration(durableDatabase, clock),
            new PackConversionRuleMigration(durableDatabase, clock),
            new StockSourceTopologyMigration(durableDatabase),
            new AfterSalesCausalCoverageMigration(durableDatabase, clock),
            new ProcurementStockSourceCoverageMigration(durableDatabase, clock),
            new InventoryCountCoverageMigration(durableDatabase, clock),
            new StockSourceActivationMigration(durableDatabase, clock),
            new StockReleaseMigration(durableDatabase),
            new PackConversionExecutionMigration(durableDatabase),
            new ShipmentDispatchValueMigration(durableDatabase),
            new OrderCommercialPolicyMigration(durableDatabase),
            new OperationalAnomalyMigration(durableDatabase),
            new ReplenishmentMigration(durableDatabase),
            new PurchaseArrivalMigration(durableDatabase),
            new CatalogIdentifierMigration(durableDatabase),
            new OperationalScanBindingMigration(durableDatabase),
            new OrderViewMigration(durableDatabase),
            new TemporaryDelegationMigration(durableDatabase),
            new OrderSubstitutionMigration(durableDatabase, database.Client as MongoClient ?? throw new InvalidOperationException("Migrations require the actual Mongo client"), clock),
            new ProvenanceSealMigration(durableDatabase),
            new InventorySealMigration(durableDatabase),
            new OrderProgressSealMigration(durableDatabase),
            new OrderDraftDiscardMigration(durableDatabase),
            new InvoiceReleaseMigration(durableDatabase),
            new ShipmentCourierMigration(durableDatabase, database.Client as MongoClient ?? throw new InvalidOperationException("Migrations require the actual Mongo client"), clock),
            new CatalogPublicMigration(durableDatabase)
        ];
        if (!steps.Select(step => step.Version).SequenceEqual(Enumerable.Range(1, CurrentVersion)))
            throw new InvalidOperationException("Schema migration registry must contain every version in order");
        return steps;
    }

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        var steps = RegisteredSteps();
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
