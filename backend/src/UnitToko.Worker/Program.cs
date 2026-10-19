using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Billing;
using UnitToko.Worker;

var migrate = args.Contains("--migrate", StringComparer.Ordinal);
var builder = Host.CreateApplicationBuilder(args.Where(arg => arg != "--migrate").ToArray());
// The lifetime is context-aware: console runs and the one-shot migration keep
// their existing behavior; SCM activates it only on the authorized Windows host.
if (!migrate && OperatingSystem.IsWindows())
    builder.Services.AddWindowsService(options => options.ServiceName = "UnitToko.Worker");
var mongoUri = builder.Configuration["Mongo:Uri"];
var databaseName = builder.Configuration["Mongo:Database"];
if (string.IsNullOrWhiteSpace(mongoUri) || string.IsNullOrWhiteSpace(databaseName) ||
    databaseName is "admin" or "local" or "config" || databaseName.Contains(' '))
    throw new InvalidOperationException("Mongo:Uri and a dedicated Mongo:Database are required.");

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(_ => new MongoConnection(mongoUri, databaseName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<MongoConnection>().Client);
builder.Services.AddSingleton(sp => sp.GetRequiredService<MongoConnection>().Database);
builder.Services.AddSingleton<MongoUnitOfWork>();
builder.Services.AddSingleton<IdAllocator>();
builder.Services.AddSingleton<IdempotencyStore>();
builder.Services.AddSingleton<OutboxStore>();
builder.Services.AddSingleton<CollectionMigrations>();
var evidenceOptions = migrate ? null : builder.Configuration.GetSection("Evidence:Validation").Get<EvidenceValidationRuntimeOptions>();
var imageRevalidationOptions = migrate ? null : builder.Configuration.GetSection("Evidence:ImageRevalidation").Get<EvidenceImageRevalidationOptions>();
var videoOptions = migrate ? null : builder.Configuration.GetSection("Evidence:VideoValidation").Get<VideoValidationRuntimeOptions>();
var pdfOptions = migrate ? null : PdfEvidenceWorkerConfiguration.Read(builder.Configuration);
var ocrOptions = migrate ? null : builder.Configuration.GetSection("Evidence:PaymentOcr").Get<PaymentOcrRuntimeOptions>();
var supplierOcrOptions = migrate ? null : builder.Configuration.GetSection("Evidence:SupplierDocumentOcr").Get<SupplierDocumentOcrRuntimeOptions>();
if (!migrate) PdfDocumentOcrWorkerConfiguration.RejectUnsupportedPayment(builder.Configuration);
var supplierPdfOcrOptions = migrate ? null : PdfDocumentOcrWorkerConfiguration.ReadSupplierInvoice(builder.Configuration);
var paymentSupplementPdfOcrOptions = migrate ? null : PdfDocumentOcrWorkerConfiguration.ReadPaymentSupplement(builder.Configuration);
var paymentNotices = !migrate && builder.Configuration.GetValue<bool>("Notifications:PaymentReviewInApp:Enabled");
if (evidenceOptions is not null) {
    builder.Services.AddSingleton(sp => EvidenceValidationRuntime.Create(sp.GetRequiredService<IMongoDatabase>(), sp.GetRequiredService<MongoUnitOfWork>(),
        sp.GetRequiredService<IdAllocator>(), sp.GetRequiredService<IClock>(), evidenceOptions));
    builder.Services.AddHostedService<EvidenceValidationWorker>();
}
if (imageRevalidationOptions is not null) {
    builder.Services.AddSingleton(sp => EvidenceImageRevalidationRuntime.Create(sp.GetRequiredService<IMongoDatabase>(),
        sp.GetRequiredService<MongoUnitOfWork>(), sp.GetRequiredService<IdAllocator>(),
        sp.GetRequiredService<IClock>(), imageRevalidationOptions));
    builder.Services.AddHostedService<EvidenceImageRevalidationWorker>();
}
if (ocrOptions is not null) {
    builder.Services.AddSingleton(sp => PaymentOcrRuntime.Create(sp.GetRequiredService<IMongoDatabase>(), sp.GetRequiredService<MongoUnitOfWork>(),
        sp.GetRequiredService<IdAllocator>(), sp.GetRequiredService<IClock>(), ocrOptions));
    builder.Services.AddHostedService<PaymentOcrWorker>();
}
if (videoOptions is not null) {
    // Video and supplier OCR require independent, explicitly configured worker approvals.
    builder.Services.AddSingleton(sp => VideoValidationRuntime.Create(sp.GetRequiredService<IMongoDatabase>(), sp.GetRequiredService<MongoUnitOfWork>(),
        sp.GetRequiredService<IdAllocator>(), sp.GetRequiredService<IClock>(), videoOptions));
    builder.Services.AddHostedService<VideoValidationWorker>();
}
if (pdfOptions is not null) {
    builder.Services.AddSingleton(sp => PdfEvidenceValidationRuntime.Create(sp.GetRequiredService<IMongoDatabase>(), sp.GetRequiredService<MongoUnitOfWork>(),
        sp.GetRequiredService<IdAllocator>(), sp.GetRequiredService<IClock>(), pdfOptions));
    builder.Services.AddHostedService<PdfEvidenceValidationWorker>();
}
if (supplierOcrOptions is not null) {
    builder.Services.AddSingleton(sp => SupplierDocumentOcrRuntime.Create(sp.GetRequiredService<IMongoDatabase>(), sp.GetRequiredService<MongoUnitOfWork>(),
        sp.GetRequiredService<IdAllocator>(), sp.GetRequiredService<IClock>(), supplierOcrOptions));
    builder.Services.AddHostedService<SupplierDocumentOcrWorker>();
}
if (supplierPdfOcrOptions is not null) builder.Services.AddSingleton(sp => PdfDocumentOcrRuntime.CreateSupplierInvoice(sp.GetRequiredService<IMongoDatabase>(),
    sp.GetRequiredService<MongoUnitOfWork>(), sp.GetRequiredService<IdAllocator>(), sp.GetRequiredService<IClock>(), supplierPdfOcrOptions));
if (paymentSupplementPdfOcrOptions is not null) builder.Services.AddSingleton(sp => PdfDocumentOcrRuntime.CreatePaymentSupplement(sp.GetRequiredService<IMongoDatabase>(),
    sp.GetRequiredService<MongoUnitOfWork>(), sp.GetRequiredService<IdAllocator>(), sp.GetRequiredService<IClock>(), paymentSupplementPdfOcrOptions));
if (supplierPdfOcrOptions is not null || paymentSupplementPdfOcrOptions is not null) builder.Services.AddHostedService<PdfDocumentOcrWorker>();
if (paymentNotices) {
    builder.Services.AddSingleton<PaymentReviewNotifications>();
    builder.Services.AddHostedService<PaymentReviewNotificationWorker>();
}

// D02 retention: discarded order drafts are purged after their 30-day restore window (on unless explicitly disabled).
var draftPurge = !migrate && builder.Configuration.GetValue("Retention:OrderDraftPurge:Enabled", true);
if (draftPurge) {
    builder.Services.AddSingleton(sp => new UnitToko.Platform.Ordering.OrderDraftPurge(sp.GetRequiredService<IMongoDatabase>(),
        sp.GetRequiredService<MongoUnitOfWork>(), sp.GetRequiredService<IdAllocator>(), sp.GetRequiredService<IClock>()));
    builder.Services.AddHostedService<OrderDraftPurgeWorker>();
}
using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("UnitToko.Worker");
if (migrate)
{
    await host.Services.GetRequiredService<CollectionMigrations>().ApplyAsync();
    logger.LogInformation("MongoDB schema migration complete at version {Version}", CollectionMigrations.CurrentVersion);
    return;
}

if (evidenceOptions is null) logger.LogWarning("Evidence validation is unconfigured; validation jobs will not be claimed.");
else logger.LogInformation("Evidence validation pipeline configured: {PipelineId}; current store and worker approvals remain required", host.Services.GetRequiredService<EvidenceValidationRuntime>().PipelineId);
if (imageRevalidationOptions is null) logger.LogWarning("Image revalidation is unconfigured; revalidation jobs will not be claimed.");
else logger.LogInformation("Image revalidation pipeline configured: {PipelineId}; separate current policy, store and worker approvals remain required", host.Services.GetRequiredService<EvidenceImageRevalidationRuntime>().PipelineId);
if (videoOptions is null) logger.LogWarning("Private video validation is unconfigured; video jobs will not be claimed.");
else logger.LogInformation("Private video validation pipeline configured: {PipelineId}; current host, scanner and worker approvals remain required", host.Services.GetRequiredService<VideoValidationRuntime>().PipelineId);
if (pdfOptions is null) logger.LogWarning("Private PDF validation is unconfigured; PDF jobs will not be claimed.");
else logger.LogInformation("Private PDF validation pipeline configured: {PipelineId}; independent finite host, scanner and worker approvals remain required", host.Services.GetRequiredService<PdfEvidenceValidationRuntime>().PipelineId);
if (ocrOptions is null) logger.LogWarning("Payment OCR is unconfigured; OCR jobs will not be claimed.");
else logger.LogInformation("Payment OCR pipeline configured: {PipelineId}; current host and model approvals remain required", host.Services.GetRequiredService<PaymentOcrRuntime>().PipelineId);
if (supplierOcrOptions is null) logger.LogWarning("Supplier document OCR is unconfigured; supplier OCR jobs will not be claimed.");
else logger.LogInformation("Supplier document OCR pipeline configured: {PipelineId}; current host and model approvals remain required", host.Services.GetRequiredService<SupplierDocumentOcrRuntime>().PipelineId);
if (supplierPdfOcrOptions is null) logger.LogWarning("Whole-PDF supplier OCR is unconfigured; its distinct document jobs will not be claimed.");
foreach (var runtime in host.Services.GetServices<PdfDocumentOcrRuntime>())
    logger.LogInformation("Whole-PDF {SourceKind} pipeline configured: {PipelineId}; independent finite host, raster, model and source policy approvals remain required", runtime.SourceKind, runtime.PipelineId);
if (!draftPurge) logger.LogWarning("Order draft purge is disabled; discarded drafts will not be deleted after 30 days.");
if (!paymentNotices) logger.LogWarning("Payment in-app notices are unconfigured; review events will remain pending.");
else logger.LogInformation("Payment in-app notice worker enabled; availability is private and sends no external messages");
await host.RunAsync();

internal sealed class EvidenceImageRevalidationWorker(EvidenceImageRevalidationRuntime runtime,
    ILogger<EvidenceImageRevalidationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? previous = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            string status;
            try { status = await runtime.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (PersistenceConflictException) { status = "claim_or_policy_changed"; }
            catch (UncertainCommitException) { status = "commit_unknown_recover_same_job"; }
            catch { status = "processing_error"; } // Never log original content, paths, adapter details or configuration.
            if (status != previous) { logger.LogInformation("Image revalidation worker status: {Status}", status); previous = status; }
            await Task.Delay(status == "completed" ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
