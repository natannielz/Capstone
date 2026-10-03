using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Billing;
using UnitToko.Worker;

var migrate = args.Contains("--migrate", StringComparer.Ordinal);
var builder = Host.CreateApplicationBuilder(args.Where(arg => arg != "--migrate").ToArray());
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
var videoOptions = migrate ? null : builder.Configuration.GetSection("Evidence:VideoValidation").Get<VideoValidationRuntimeOptions>();
var ocrOptions = migrate ? null : builder.Configuration.GetSection("Evidence:PaymentOcr").Get<PaymentOcrRuntimeOptions>();
var supplierOcrOptions = migrate ? null : builder.Configuration.GetSection("Evidence:SupplierDocumentOcr").Get<SupplierDocumentOcrRuntimeOptions>();
var paymentNotices = !migrate && builder.Configuration.GetValue<bool>("Notifications:PaymentReviewInApp:Enabled");
if (evidenceOptions is not null) {
    builder.Services.AddSingleton(sp => EvidenceValidationRuntime.Create(sp.GetRequiredService<IMongoDatabase>(), sp.GetRequiredService<MongoUnitOfWork>(),
        sp.GetRequiredService<IdAllocator>(), sp.GetRequiredService<IClock>(), evidenceOptions));
    builder.Services.AddHostedService<EvidenceValidationWorker>();
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
if (supplierOcrOptions is not null) {
    builder.Services.AddSingleton(sp => SupplierDocumentOcrRuntime.Create(sp.GetRequiredService<IMongoDatabase>(), sp.GetRequiredService<MongoUnitOfWork>(),
        sp.GetRequiredService<IdAllocator>(), sp.GetRequiredService<IClock>(), supplierOcrOptions));
    builder.Services.AddHostedService<SupplierDocumentOcrWorker>();
}
if (paymentNotices) {
    builder.Services.AddSingleton<PaymentReviewNotifications>();
    builder.Services.AddHostedService<PaymentReviewNotificationWorker>();
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
if (videoOptions is null) logger.LogWarning("Private video validation is unconfigured; video jobs will not be claimed.");
else logger.LogInformation("Private video validation pipeline configured: {PipelineId}; current host, scanner and worker approvals remain required", host.Services.GetRequiredService<VideoValidationRuntime>().PipelineId);
if (ocrOptions is null) logger.LogWarning("Payment OCR is unconfigured; OCR jobs will not be claimed.");
else logger.LogInformation("Payment OCR pipeline configured: {PipelineId}; current host and model approvals remain required", host.Services.GetRequiredService<PaymentOcrRuntime>().PipelineId);
if (supplierOcrOptions is null) logger.LogWarning("Supplier document OCR is unconfigured; supplier OCR jobs will not be claimed.");
else logger.LogInformation("Supplier document OCR pipeline configured: {PipelineId}; current host and model approvals remain required", host.Services.GetRequiredService<SupplierDocumentOcrRuntime>().PipelineId);
if (!paymentNotices) logger.LogWarning("Payment in-app notices are unconfigured; review events will remain pending.");
else logger.LogInformation("Payment in-app notice worker enabled; availability is private and sends no external messages");
await host.RunAsync();
