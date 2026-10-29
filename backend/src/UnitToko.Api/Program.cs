using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Api.Authorization;
using UnitToko.Api.Identity;
using UnitToko.Api.Catalog;
using UnitToko.Api.Inventory;
using UnitToko.Api.Endpoints;
using UnitToko.Platform.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Organizations;
using UnitToko.Api.Organizations;
using UnitToko.Api.Ordering;
using UnitToko.Platform.Ordering;
using UnitToko.Api.Fulfillment;
using UnitToko.Platform.Fulfillment;
using UnitToko.Api.Billing;
using UnitToko.Platform.Billing;
using UnitToko.Api.Accounting;
using UnitToko.Platform.Accounting;
using UnitToko.Api.Reporting;
using UnitToko.Platform.Reporting;
using UnitToko.Api.Operations;
using UnitToko.Platform.Operations;
using UnitToko.Api.Procurement;
using UnitToko.Platform.Procurement;
using UnitToko.Api;

if (args is ["provision-seal-keys", ..])
{
    // Operator command: provision the provenance seal key ring in this environment's protected key directory (read from the
    // same configuration the API uses), then exit. It never rotates an existing ring and prints only the key id.
    var provisioning = WebApplication.CreateBuilder(args[1..]).Configuration;
    Console.WriteLine("Provenance seal key ring ready (current key " + IdentityKeyMaterial.ProvisionSealKeys(provisioning) + ").");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<GrantScopeKind>(JsonNamingPolicy.CamelCase, false));
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<ScopeNodeKind>(JsonNamingPolicy.CamelCase, false));
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<OrganizationChangeKind>(JsonNamingPolicy.CamelCase, false));
});
builder.Services.AddSingleton<IClock, UnitToko.Platform.SystemClock>();
builder.Services.AddSingleton<ISystemStatusProvider, ClockSystemStatusProvider>();
builder.Services.AddSingleton(sp => new MongoConnection(
    sp.GetRequiredService<IConfiguration>()["Mongo:Uri"]!,
    sp.GetRequiredService<IConfiguration>()["Mongo:Database"]!));
builder.Services.AddSingleton(sp => sp.GetRequiredService<MongoConnection>().Client);
builder.Services.AddSingleton(sp => sp.GetRequiredService<MongoConnection>().Database);
builder.Services.AddSingleton<MongoUnitOfWork>();
builder.Services.AddSingleton<IdAllocator>();
builder.Services.AddSingleton<IdempotencyStore>();
builder.Services.AddSingleton<OutboxStore>();
builder.Services.AddSingleton<CollectionMigrations>();
builder.Services.AddSingleton<AuthorizationStore>();
builder.Services.AddSingleton<CatalogVisibilityStore>();
builder.Services.AddSingleton<CatalogAuthoringService>();
builder.Services.AddSingleton<ScanResolver>();
builder.Services.AddSingleton<AccountContactService>();
builder.Services.AddSingleton<CatalogPriceService>();
builder.Services.AddSingleton<CatalogPublicSampleService>();
builder.Services.AddSingleton<CatalogDefinitionQueryService>();
builder.Services.AddSingleton(sp => {
    var options = sp.GetRequiredService<IConfiguration>().GetSection("Evidence:PrivateWindows");
    var root = options["Root"]; var storeId = options["StoreId"]; var operators = options.GetSection("TrustedOperatorSids").Get<string[]>() ?? [];
    CatalogPhotoStorageOptions? storage = null;
    if (root is not null || storeId is not null || operators.Length > 0) {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(storeId)) throw new InvalidOperationException("Complete private evidence storage settings are required");
        storage = new(root, storeId, operators);
    }
    return new CatalogPhotoWorkflow(sp.GetRequiredService<IMongoDatabase>(), sp.GetRequiredService<MongoUnitOfWork>(), sp.GetRequiredService<IdAllocator>(),
        sp.GetRequiredService<IClock>(), sp.GetRequiredService<CatalogAuthoringService>(), storage);
});
if (builder.Configuration.GetSection("Evidence:ImageRevalidation").Exists())
    builder.Services.AddSingleton(sp => {
        var options = sp.GetRequiredService<IConfiguration>().GetSection("Evidence:ImageRevalidation")
            .Get<EvidenceImageRevalidationOptions>() ?? throw new InvalidOperationException("Image revalidation is unconfigured");
        return EvidenceImageRevalidationRuntime.Create(sp.GetRequiredService<IMongoDatabase>(),
            sp.GetRequiredService<MongoUnitOfWork>(), sp.GetRequiredService<IdAllocator>(),
            sp.GetRequiredService<IClock>(), options);
    });
builder.Services.AddSingleton<OrderQuoteService>();
builder.Services.AddSingleton<OrderDraftService>();
builder.Services.AddSingleton<OrderViewService>();
builder.Services.AddSingleton<BranchOrderRequestService>();
builder.Services.AddSingleton(sp => OrderSubmissionService.CreateWithApprovedAdmission(
    sp.GetRequiredService<IMongoDatabase>(), sp.GetRequiredService<MongoUnitOfWork>(),
    sp.GetRequiredService<IdAllocator>(), sp.GetRequiredService<IClock>(),
    sp.GetRequiredService<OrganizationMasterDataService>()));
builder.Services.AddSingleton<ShipmentPreparationService>();
builder.Services.AddSingleton(sp => ShipmentDispatchService.CreateWithApprovedAdmission(
    sp.GetRequiredService<IMongoDatabase>(), sp.GetRequiredService<MongoUnitOfWork>(),
    sp.GetRequiredService<IdAllocator>(), sp.GetRequiredService<IClock>(),
    sp.GetRequiredService<OrderSubmissionService>(), sp.GetRequiredService<ShipmentPreparationService>(),
    sp.GetRequiredService<OrganizationMasterDataService>()));
builder.Services.AddSingleton<ShipmentQueryService>();
builder.Services.AddSingleton<ShipmentDocumentService>();
builder.Services.AddSingleton<ShipmentReceiptService>();
builder.Services.AddSingleton<ShipmentCourierService>();
builder.Services.AddSingleton<DeliveryAttemptService>();
builder.Services.AddSingleton<InvoiceReleaseService>();
builder.Services.AddSingleton<InvoiceService>();
builder.Services.AddSingleton<AfterSalesCaseService>();
builder.Services.AddSingleton<PaymentDraftService>();
builder.Services.AddSingleton<ReceivingAccountService>();
builder.Services.AddSingleton<BankStatementService>();
builder.Services.AddSingleton<BankReceiptService>();
builder.Services.AddSingleton<PaymentVerificationService>();
builder.Services.AddSingleton<PaymentAllocationService>();
builder.Services.AddSingleton<ReturnEntitlementService>();
builder.Services.AddSingleton<ReturnFinancialApprovalService>();
builder.Services.AddSingleton<RefundPayoutService>();
builder.Services.AddSingleton(sp => {
    var options = sp.GetRequiredService<IConfiguration>().GetSection("Evidence:PrivateWindows");
    var root = options["Root"]; var storeId = options["StoreId"]; var operators = options.GetSection("TrustedOperatorSids").Get<string[]>() ?? [];
    PaymentEvidenceStorageOptions? storage = null;
    if (root is not null || storeId is not null || operators.Length > 0) {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(storeId)) throw new InvalidOperationException("Complete private evidence storage settings are required");
        storage = new(root, storeId, operators);
    }
    return new RefundPayoutEvidenceService(sp.GetRequiredService<IMongoDatabase>(), sp.GetRequiredService<MongoUnitOfWork>(), sp.GetRequiredService<IdAllocator>(),
        sp.GetRequiredService<IClock>(), sp.GetRequiredService<RefundPayoutService>(), storage);
});
builder.Services.AddSingleton(sp => {
    var options = sp.GetRequiredService<IConfiguration>().GetSection("Evidence:PrivateWindows");
    var root = options["Root"]; var storeId = options["StoreId"]; var operators = options.GetSection("TrustedOperatorSids").Get<string[]>() ?? [];
    PaymentEvidenceStorageOptions? storage = null;
    if (root is not null || storeId is not null || operators.Length > 0) {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(storeId)) throw new InvalidOperationException("Complete private evidence storage settings are required");
        storage = new(root, storeId, operators);
    }
    return new PaymentEvidenceService(sp.GetRequiredService<IMongoDatabase>(), sp.GetRequiredService<MongoUnitOfWork>(), sp.GetRequiredService<IdAllocator>(),
        sp.GetRequiredService<IClock>(), sp.GetRequiredService<PaymentDraftService>(), storage);
});
builder.Services.AddSingleton(sp => {
    var options = sp.GetRequiredService<IConfiguration>().GetSection("Evidence:PrivateWindows");
    var root = options["Root"]; var storeId = options["StoreId"]; var operators = options.GetSection("TrustedOperatorSids").Get<string[]>() ?? [];
    PaymentEvidenceStorageOptions? storage = null;
    if (root is not null || storeId is not null || operators.Length > 0) {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(storeId)) throw new InvalidOperationException("Complete private evidence storage settings are required");
        storage = new(root, storeId, operators);
    }
    return new PaymentSupplementEvidenceService(sp.GetRequiredService<IMongoDatabase>(), sp.GetRequiredService<MongoUnitOfWork>(), sp.GetRequiredService<IdAllocator>(),
        sp.GetRequiredService<IClock>(), sp.GetRequiredService<PaymentDraftService>(), storage);
});
builder.Services.AddSingleton<StockCountService>();
builder.Services.AddSingleton<ReplenishmentService>();
builder.Services.AddSingleton<PackConversionRuleService>();
builder.Services.AddSingleton<StockReleaseService>();
builder.Services.AddSingleton<PackConversionService>();
builder.Services.AddSingleton<StockSourceSetupService>();
builder.Services.AddSingleton<HistoricalReadStore>();
builder.Services.AddSingleton<AccessAdministrationService>();
builder.Services.AddSingleton<TemporaryDelegationService>();
builder.Services.AddSingleton<OrganizationMasterDataService>();
builder.Services.AddSingleton<OrganizationAddressService>();
builder.Services.AddSingleton<OrderSpendingPolicyService>();
builder.Services.AddSingleton<OrderCommercialPolicyService>();
builder.Services.AddSingleton<AccountingBookSetupService>();
builder.Services.AddSingleton<AccountingReportService>();
builder.Services.AddSingleton<OperationalAnomalyService>();
builder.Services.AddSingleton<OperatorHealthService>();
// "Pekerjaan saya": one source per owner queue (each reads through its own authorised service).
builder.Services.AddSingleton<UnitToko.Platform.Work.IWorkSource, UnitToko.Platform.Billing.PaymentReviewWorkSource>();
builder.Services.AddSingleton<UnitToko.Platform.Work.IWorkSource, UnitToko.Platform.Ordering.OrderReviewWorkSource>();
builder.Services.AddSingleton<UnitToko.Platform.Work.IWorkSource, UnitToko.Platform.Ordering.IncomingRequestWorkSource>();
builder.Services.AddSingleton<UnitToko.Platform.Work.IWorkSource, UnitToko.Platform.Fulfillment.DispatchWorkSource>();
builder.Services.AddSingleton<UnitToko.Platform.Work.IWorkPermissions, UnitToko.Platform.Work.AuthorizedWorkPermissions>();
builder.Services.AddSingleton<UnitToko.Platform.Work.WorkSummaryService>();
builder.Services.AddSingleton<ProcurementRequestService>();
builder.Services.AddSingleton<SupplierService>();
builder.Services.AddSingleton<PurchaseOrderService>();
builder.Services.AddSingleton<ProcurementFundingService>();
builder.Services.AddSingleton<SupplierBankService>();
builder.Services.AddSingleton<ProcurementDisbursementService>();
builder.Services.AddSingleton<ProcurementSettlementService>();
builder.Services.AddSingleton<ProcurementReceivingService>();
builder.Services.AddSingleton<ProcurementInspectionService>();
builder.Services.AddSingleton<SupplierDocumentService>();
builder.Services.AddSingleton<SupplierClaimService>();
builder.Services.AddSingleton<ProcurementStockReceiptService>();
builder.Services.AddSingleton<ProcurementStockValuationService>();
builder.Services.AddSingleton<ProcurementSupplierPayableService>();
builder.Services.AddSingleton<ProcurementAdvanceReturnService>();
builder.Services.AddSingleton<ProcurementQualityAcceptanceService>();
builder.Services.AddSingleton(sp => {
    var options = sp.GetRequiredService<IConfiguration>().GetSection("Evidence:PrivateWindows");
    var root = options["Root"]; var storeId = options["StoreId"]; var operators = options.GetSection("TrustedOperatorSids").Get<string[]>() ?? [];
    PaymentEvidenceStorageOptions? storage = null;
    if (root is not null || storeId is not null || operators.Length > 0) {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(storeId)) throw new InvalidOperationException("Complete private evidence storage settings are required");
        storage = new(root, storeId, operators);
    }
    return new SupplierDocumentEvidenceService(sp.GetRequiredService<IMongoDatabase>(), sp.GetRequiredService<MongoUnitOfWork>(), sp.GetRequiredService<IdAllocator>(),
        sp.GetRequiredService<IClock>(), sp.GetRequiredService<SupplierDocumentService>(), storage);
});
builder.Services.AddSingleton(sp => {
    var options = sp.GetRequiredService<IConfiguration>().GetSection("Evidence:PrivateVideoWindows");
    var root = options["Root"]; var storeId = options["StoreId"]; var operators = options.GetSection("TrustedOperatorSids").Get<string[]>() ?? [];
    PaymentEvidenceStorageOptions? storage = null;
    if (root is not null || storeId is not null || operators.Length > 0) {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(storeId)) throw new InvalidOperationException("Complete private video storage settings are required");
        storage = new(root, storeId, operators);
    }
    return new ProcurementInspectionEvidenceService(sp.GetRequiredService<IMongoDatabase>(), sp.GetRequiredService<MongoUnitOfWork>(), sp.GetRequiredService<IdAllocator>(),
        sp.GetRequiredService<IClock>(), sp.GetRequiredService<ProcurementInspectionService>(), storage);
});
builder.Services.AddSingleton(sp => {
    var options = sp.GetRequiredService<IConfiguration>().GetSection("Evidence:PrivateWindows");
    var root = options["Root"]; var storeId = options["StoreId"]; var operators = options.GetSection("TrustedOperatorSids").Get<string[]>() ?? [];
    PaymentEvidenceStorageOptions? storage = null;
    if (root is not null || storeId is not null || operators.Length > 0) {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(storeId)) throw new InvalidOperationException("Complete private evidence storage settings are required");
        storage = new(root, storeId, operators);
    }
    return new ProcurementDisbursementEvidenceService(sp.GetRequiredService<IMongoDatabase>(), sp.GetRequiredService<MongoUnitOfWork>(), sp.GetRequiredService<IdAllocator>(),
        sp.GetRequiredService<IClock>(), sp.GetRequiredService<ProcurementDisbursementService>(), storage);
});
var identityEnabled = builder.Services.AddUnitTokoIdentityRuntime(builder.Configuration);
builder.Services.PostConfigure<Auth0Options>(options => {
    if (ReleaseProfile.IsPilot(builder.Configuration))
        options.AllowedReturnPaths = options.AllowedReturnPaths.Intersect(ReleaseProfile.PilotReturnPaths, StringComparer.Ordinal).ToArray();
});
builder.Services.AddAuthorization(options =>
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddOpenApi("v1", options =>
{
    options.AddSchemaTransformer<NumericStringSchemaTransformer>();
    options.AddSchemaTransformer((schema, context, _) =>
    {
        // Output-only native unions have no discriminator on the actual JSON wire.
        if (context.JsonTypeInfo.PolymorphismOptions is { DerivedTypes.Count: > 0 } polymorphism &&
            polymorphism.DerivedTypes.All(derived => derived.TypeDiscriminator is null)) schema.Discriminator = null;
        return Task.CompletedTask;
    });
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new OpenApiComponents();
        CatalogOpenApi.Configure(document);
        OrderCommercialPolicyEndpoints.ConfigureOpenApi(document);
        StockCountOpenApi.Configure(document);
        PackConversionRuleOpenApi.Configure(document);
        PackConversionExecutionOpenApi.Configure(document);
        StockSourceActivationOpenApi.Configure(document);
        StockSourceSetupOpenApi.Configure(document);
        StockSourceRevisionOpenApi.Configure(document);
        StockSourceAliasOpenApi.Configure(document);
        StockSourceSuspensionOpenApi.Configure(document);
        StockSourcePolicyOpenApi.Configure(document);
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            ["providerEvents"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer",
                Description = "Dedicated rotating machine credential for this Auth0 stream; never a browser session or API access token."
            },
            ["session"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Cookie,
                Name = "__Host-ut_session",
                Description = "Opaque application session after managed OIDC login."
            }
        };
        foreach (var path in document.Paths.Where(p => p.Key.StartsWith("/api/v1/", StringComparison.Ordinal)))
            foreach (var pair in path.Value.Operations ?? [])
            {
                var operation = pair.Value;
                if (pair.Key.ToString().Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    path.Key is ProviderSecurityEventEndpoints.EventsPath or ProviderSecurityEventEndpoints.LogsPath)
                {
                    operation.Security = [new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference("providerEvents", document)] = []
                    }];
                    continue;
                }
                if ((path.Key.StartsWith("/api/v1/access", StringComparison.Ordinal) || path.Key.StartsWith("/api/v1/organizations", StringComparison.Ordinal) ||
                     path.Key.StartsWith("/api/v1/identity/administration/", StringComparison.Ordinal) ||
                     path.Key.StartsWith("/api/v1/identity/contact", StringComparison.Ordinal) ||
                     path.Key.StartsWith("/api/v1/catalog/", StringComparison.Ordinal) ||
                     path.Key.StartsWith("/api/v1/ordering/", StringComparison.Ordinal) ||
                      path.Key.StartsWith("/api/v1/accounting/", StringComparison.Ordinal) ||
                      path.Key.StartsWith("/api/v1/procurement/", StringComparison.Ordinal) ||
                     path.Key.StartsWith("/api/v1/fulfillment/", StringComparison.Ordinal) ||
                     path.Key.StartsWith("/api/v1/billing/", StringComparison.Ordinal) ||
                     path.Key.StartsWith("/api/v1/inventory/", StringComparison.Ordinal)) &&
                    path.Key != "/api/v1/access/current")
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "X-Expected-Actor-Id", In = ParameterLocation.Header, Required = ActorRequiredFilter.NeedsExpectedActor(pair.Key.ToString(), path.Key),
                        Description = "The account reference that reviewed this request. Compared with the verified session; never used to authenticate.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 19, Pattern = "^[1-9][0-9]{0,18}$(?![\\s\\S])" }
                    });
                }
                foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>() ?? [])
                {
                    if (parameter.In != ParameterLocation.Header) continue;
                    if (parameter.Name == "Idempotency-Key")
                    {
                        parameter.Description = "Reuse this key only for an unchanged logical command, including after an uncertain response.";
                        parameter.Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 128,
                            Pattern = @"^[A-Za-z0-9._:-]+$(?![\s\S])" };
                    }
                    else if (parameter.Name == "If-Match")
                    {
                        parameter.Description = "One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number.";
                        parameter.Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 3, MaxLength = 21 };
                    }
                }
                if (path.Key is "/api/v1/identity/csrf" or "/api/v1/identity/session") continue;
                if (pair.Key.ToString().ToUpperInvariant() is "POST" or "PUT" or "PATCH" or "DELETE")
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "X-CSRF-Token", In = ParameterLocation.Header, Required = true,
                        Description = "Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    });
                }
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("session", document)] = []
                });
            }
        return Task.CompletedTask;
    });
});

var app = builder.Build();
// A present provenance seal that fails verification is possible tampering: log it for the integrity responder.
var integrityLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("UnitToko.Integrity");
UnitToko.Platform.Integrity.SealIntegrityAlerts.Raised += alert =>
    integrityLog.LogCritical("Provenance seal verification failed: kind {Kind}, owner {OwnerId}, check {Check}", alert.Kind, alert.OwnerId, alert.Check);
var pilotRelease = ReleaseProfile.IsPilot(app.Configuration);
if (identityEnabled) _ = app.Services.GetRequiredService<IdentityKeyMaterial>();
var mongoUri = app.Configuration["Mongo:Uri"];
var mongoDatabase = app.Configuration["Mongo:Database"];
if (string.IsNullOrWhiteSpace(mongoUri) || string.IsNullOrWhiteSpace(mongoDatabase) ||
    mongoDatabase is "admin" or "local" or "config" || mongoDatabase.Contains(' '))
    throw new InvalidOperationException("Mongo:Uri and a dedicated Mongo:Database are required.");
// Resolve concrete endpoint metadata before any browser/machine security decisions.
app.UseRouting();
app.Use(async (context, next) =>
{
    // Cover binding/authentication failures too, before endpoint filters can run.
    if (IdentityBrowserSecurity.IsProtectedPath(context.Request.Path))
        IdentityBrowserSecurity.ApplyResponseHeaders(context);
    var candidate = context.Request.Headers["X-Correlation-Id"].ToString();
    var correlation = candidate.Length is > 0 and <= 64 && candidate.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
        ? candidate : Guid.NewGuid().ToString("N");
    context.TraceIdentifier = correlation;
    context.Response.Headers["X-Correlation-Id"] = correlation;
    try
    {
        var evidenceStream = context.GetEndpoint()?.Metadata.GetMetadata<PaymentEvidenceBodyStream.UploadMetadata>() is not null ||
            context.GetEndpoint()?.Metadata.GetMetadata<CatalogPhotoBodyStream.UploadMetadata>() is not null;
        var videoStream = context.GetEndpoint()?.Metadata.GetMetadata<ProcurementInspectionEvidenceBodyStream.UploadMetadata>() is not null;
        // The video endpoint obtains its authorized finite policy and sets its request/source
        // bound before reading bytes. Image and JSON ceilings stay unchanged here.
        if (!videoStream && context.Request.ContentLength > (evidenceStream ? PaymentEvidenceBodyStream.MaximumBytes : 65_536))
        {
            await Problems.WriteAsync(context, 413, "request_too_large", "Request body is too large");
            return;
        }
        // JSON stays bounded on every method; the exact upload endpoint streams through its
        // separate bounded source after authorization, including requests without Content-Length.
        if (!evidenceStream && !videoStream)
        {
            await using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            try
            {
                while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) != 0)
                {
                    if (buffer.Length + read > 65_536)
                    {
                        await Problems.WriteAsync(context, 413, "request_too_large", "Request body is too large");
                        return;
                    }
                    buffer.Write(chunk, 0, read);
                }
            }
            catch (IOException) when (!context.RequestAborted.IsCancellationRequested)
            {
                await Problems.WriteAsync(context, 400, "invalid_request", "Invalid request body");
                return;
            }
            if (!ProviderSecurityEventEndpoints.IsReceiver(context) && buffer.Length > 0 && context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
            {
                buffer.Position = 0;
                try { using var _ = await JsonDocument.ParseAsync(buffer, cancellationToken: context.RequestAborted); }
                catch (JsonException)
                {
                    await Problems.WriteAsync(context, 400, "invalid_json", "Invalid JSON request");
                    return;
                }
            }
            context.Request.Body = new MemoryStream(buffer.ToArray(), writable: false);
        }
        await next(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (DomainValidationException exception)
    {
        if (!context.Response.HasStarted)
            await Problems.WriteAsync(context, 400, exception.Code, exception.SafeTitle);
    }
    catch (AuthorizationDeniedException exception)
    {
        if (!context.Response.HasStarted)
            await Problems.WriteAsync(context, 403, exception.Code, "Access denied");
    }
    catch (PersistenceConflictException exception)
    {
        if (!context.Response.HasStarted)
            await Problems.WriteAsync(context, 409, exception.Code, "Persistence conflict");
    }
    catch (UncertainCommitException exception)
    {
        app.Logger.LogError(exception, "Commit outcome unknown, trace {TraceId}", correlation);
        if (!context.Response.HasStarted)
        {
            context.Response.Headers.RetryAfter = "1";
            await Problems.WriteAsync(context, 503, "commit_outcome_unknown",
                "Commit outcome unknown; retry with the same idempotency key");
        }
    }
    catch (BadHttpRequestException exception)
    {
        if (!context.Response.HasStarted)
            await Problems.WriteAsync(context, exception.StatusCode is 413 or 415 ? exception.StatusCode : 400,
                exception.StatusCode == 413 ? "request_too_large" : exception.StatusCode == 415 ? "unsupported_media_type" : "invalid_request",
                exception.StatusCode == 413 ? "Request body is too large" : exception.StatusCode == 415 ? "Unsupported content type" : "Invalid request body");
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Unhandled request error, trace {TraceId}", correlation);
        if (!context.Response.HasStarted)
            await Problems.WriteAsync(context, 500, "internal_error", "Request failed");
    }
});

app.UseStatusCodePages(async statusContext =>
{
    var response = statusContext.HttpContext.Response;
    var (code, title) = response.StatusCode switch
    {
        401 => ("unauthenticated", "Authentication required"),
        403 => ("forbidden", "Access denied"),
        404 => ("not_found", "Resource not found"),
        405 => ("method_not_allowed", "Method not allowed"),
        413 => ("request_too_large", "Request body is too large"),
        _ => ("request_failed", "Request failed")
    };
    await Problems.WriteAsync(statusContext.HttpContext, response.StatusCode, code, title);
});
if (identityEnabled) app.UseForwardedHeaders();
app.UseIdentityTransport(identityEnabled);
if (identityEnabled) app.UseRateLimiter();
app.UseAuthentication();
app.UseIdentityAntiforgery(identityEnabled);
app.UseAuthorization();
app.MapIdentitySecurityEndpoints();
app.MapProviderSecurityEventEndpoints();
app.MapIdentityEndpoints();
app.MapAccessEndpoints(currentOnly: pilotRelease);
if (!pilotRelease) app.MapTemporaryDelegationEndpoints();
if (!pilotRelease) UnitToko.Api.Work.WorkEndpoints.MapWorkEndpoints(app);
if (!pilotRelease)
{
app.MapIdentityAdministrationEndpoints();
app.MapPersonReviewEndpoints();
app.MapOrganizationEndpoints();
app.MapOrganizationAddressEndpoints();
}
app.MapCatalogEndpoints();
app.MapCatalogIdentifierEndpoints();
app.MapScanEndpoints();
app.MapBranchOrderRequestEndpoints();
// Operators watch the pilot too (Q10): queue, purge, schema and replication health is part of every release.
app.MapOperatorHealthEndpoints();
if (!pilotRelease)
{
app.MapAccountContactEndpoints();
app.MapCatalogPriceEndpoints();
app.MapCatalogPhotoEndpoints();
app.MapCatalogPhotoRevalidationEndpoints();
app.MapOrderQuoteEndpoints();
app.MapOrderSpendingPolicyEndpoints();
app.MapOrderCommercialPolicyEndpoints();
app.MapAccountingBookSetupEndpoints();
app.MapAccountingReportEndpoints();
app.MapOperationalAnomalyEndpoints();
app.MapProcurementRequestEndpoints();
app.MapSupplierEndpoints();
app.MapPurchaseOrderEndpoints();
app.MapPurchaseArrivalEndpoints();
app.MapProcurementFundingEndpoints();
app.MapSupplierBankEndpoints();
app.MapProcurementDisbursementEndpoints();
app.MapProcurementSettlementEndpoints();
app.MapProcurementReceivingEndpoints();
app.MapProcurementInspectionEndpoints();
app.MapSupplierDocumentEndpoints();
app.MapSupplierClaimEndpoints();
app.MapProcurementStockReceiptEndpoints();
app.MapProcurementStockValuationEndpoints();
app.MapProcurementSupplierPayableEndpoints();
app.MapProcurementAdvanceReturnEndpoints();
app.MapProcurementQualityAcceptanceEndpoints();
app.MapOrderDraftEndpoints();
app.MapOrderViewEndpoints();
app.MapOrderSubmissionEndpoints();
app.MapOrderDecisionEndpoints();
app.MapOrderSubstitutionEndpoints();
app.MapShipmentPreparationEndpoints();
app.MapShipmentDispatchEndpoints();
app.MapShipmentReadEndpoints();
app.MapShipmentCourierEndpoints();
app.MapShipmentDocumentEndpoints();
app.MapShipmentReceiptEndpoints();
app.MapShipmentReceiptProofEndpoints();
app.MapDeliveryAttemptEndpoints();
app.MapInvoiceEndpoints();
app.MapInvoiceReleaseEndpoints();
app.MapAfterSalesCaseEndpoints();
app.MapPaymentDraftEndpoints();
app.MapPaymentEvidenceEndpoints();
app.MapPaymentSupplementEndpoints();
app.MapPdfEvidenceEndpoints();
app.MapPdfDocumentOcrEndpoints();
app.MapPaymentOcrEndpoints();
app.MapPaymentReviewEndpoints();
app.MapPaymentCorrectionEndpoints();
app.MapPaymentManualReviewEndpoints();
app.MapPaymentReviewActionEndpoints();
app.MapPaymentReviewHandoverEndpoints();
app.MapPaymentVerificationEndpoints();
app.MapPaymentAllocationEndpoints();
app.MapPaymentAllocationCorrectionEndpoints();
app.MapReturnEntitlementEndpoints();
app.MapReturnFinancialApprovalEndpoints();
app.MapRefundPayoutEndpoints();
app.MapReceivingAccountEndpoints();
app.MapBankStatementEndpoints();
app.MapBankReceiptEndpoints();
app.MapStockCountEndpoints();
app.MapReplenishmentEndpoints();
app.MapPackConversionRuleEndpoints();
app.MapPackConversionExecutionEndpoints();
app.MapStockSourceActivationEndpoints();
app.MapStockReleaseEndpoints();
app.MapStockSourceSetupEndpoints();
app.MapStockSourceRevisionEndpoints();
app.MapStockSourceAliasEndpoints();
app.MapStockSourceSuspensionEndpoints();
app.MapStockSourcePolicyEndpoints();
app.MapStockSourceReconciliationEndpoints();
}
// The signed-out catalog (no prices) is part of every release.
app.MapCatalogPublicEndpoints();
app.MapCatalogPublicSampleEndpoints();
app.MapCatalogCartEndpoints();
app.MapGet("/health/live", () => TypedResults.Ok(new HealthStatus("live"))).AllowAnonymous().WithName("Liveness");
app.MapGet("/health/ready", async (HttpContext context, MongoConnection mongo,
    CollectionMigrations migrations, CancellationToken cancellationToken) =>
{
    try
    {
        if (await mongo.IsReadyAsync(cancellationToken) && await migrations.IsCurrentAsync(cancellationToken))
            return Results.Ok(new HealthStatus("ready"));
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
    catch (Exception) { /* dependency health is reported without implementation details */ }
    return Results.Json(Problems.Create(context, 503, "dependency_unavailable", "Service is not ready"),
        statusCode: 503, contentType: "application/problem+json");
}).AllowAnonymous().WithName("Readiness").Produces<HealthStatus>(200)
    .Produces<ApiProblem>(503, "application/problem+json");
app.MapGet("/api/v1/system/status", (ISystemStatusProvider provider) =>
    TypedResults.Ok(provider.GetStatus()))
    .RequireAuthorization().WithName("SystemStatus")
    .Produces<ApiProblem>(400, "application/problem+json")
    .Produces<ApiProblem>(401, "application/problem+json")
    .Produces<ApiProblem>(403, "application/problem+json")
    .Produces<ApiProblem>(500, "application/problem+json");
if (app.Environment.IsDevelopment())
    app.MapOpenApi().AllowAnonymous();
app.Run();

public sealed record SystemStatus(string Status, DateTimeOffset UtcNow);
public sealed record HealthStatus(string Status);
public sealed record ApiProblem(string Type, string Title, int Status, string Code, string TraceId);
public interface ISystemStatusProvider { SystemStatus GetStatus(); }
public sealed class ClockSystemStatusProvider(IClock clock) : ISystemStatusProvider
{
    public SystemStatus GetStatus() => new("ready", clock.UtcNow);
}
public partial class Program { }

internal static class Problems
{
    public static async Task WriteAsync(HttpContext context, int status, string code, string title)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(Create(context, status, code, title),
            options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }

    public static ApiProblem Create(HttpContext context, int status, string code, string title) =>
        new("about:blank", title, status, code, context.TraceIdentifier);
}

internal sealed class UnavailableAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public UnavailableAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder) { }
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}
