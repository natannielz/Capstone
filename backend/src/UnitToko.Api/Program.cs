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
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Api.Authorization;
using UnitToko.Api.Identity;
using UnitToko.Api.Catalog;
using UnitToko.Api.Inventory;
using UnitToko.Platform.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Organizations;

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
builder.Services.AddSingleton<CatalogDefinitionQueryService>();
builder.Services.AddSingleton<StockCountService>();
builder.Services.AddSingleton<StockSourceSetupService>();
builder.Services.AddSingleton<HistoricalReadStore>();
builder.Services.AddSingleton<AccessAdministrationService>();
builder.Services.AddSingleton<OrganizationMasterDataService>();
var identityEnabled = builder.Services.AddUnitTokoIdentityRuntime(builder.Configuration);
builder.Services.AddAuthorization(options =>
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddOpenApi("v1", options =>
{
    options.AddSchemaTransformer<NumericStringSchemaTransformer>();
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new OpenApiComponents();
        CatalogOpenApi.Configure(document);
        StockCountOpenApi.Configure(document);
        StockSourceSetupOpenApi.Configure(document);
        StockSourceRevisionOpenApi.Configure(document);
        StockSourceAliasOpenApi.Configure(document);
        StockSourceSuspensionOpenApi.Configure(document);
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
                     path.Key.StartsWith("/api/v1/catalog/", StringComparison.Ordinal) ||
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
        if (context.Request.ContentLength > 65_536)
        {
            await Problems.WriteAsync(context, 413, "request_too_large", "Request body is too large");
            return;
        }
        // Always consume a bounded body: HTTP/2 can carry data on any method without length or Transfer-Encoding.
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
            await Problems.WriteAsync(context, exception.StatusCode == 413 ? 413 : 400,
                exception.StatusCode == 413 ? "request_too_large" : "invalid_request",
                exception.StatusCode == 413 ? "Request body is too large" : "Invalid request body");
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
app.MapIdentityAdministrationEndpoints();
app.MapPersonReviewEndpoints();
app.MapAccessEndpoints();
app.MapOrganizationEndpoints();
app.MapCatalogEndpoints();
app.MapStockCountEndpoints();
app.MapStockSourceSetupEndpoints();
app.MapStockSourceRevisionEndpoints();
app.MapStockSourceAliasEndpoints();
app.MapStockSourceSuspensionEndpoints();
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
