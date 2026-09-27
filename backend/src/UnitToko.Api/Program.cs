using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using UnitToko.Modules.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IClock, UnitToko.Platform.SystemClock>();
builder.Services.AddSingleton<ISystemStatusProvider, ClockSystemStatusProvider>();
builder.Services.AddSingleton(sp => new MongoConnection(
    sp.GetRequiredService<IConfiguration>()["Mongo:Uri"]!,
    sp.GetRequiredService<IConfiguration>()["Mongo:Database"]!));
builder.Services.AddAuthentication("Unavailable")
    .AddScheme<AuthenticationSchemeOptions, UnavailableAuthenticationHandler>("Unavailable", _ => { });
builder.Services.AddAuthorization(options =>
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Components ??= new OpenApiComponents();
    document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
    {
        ["session"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = "__Host-ut_session",
            Description = "Opaque application session after managed OIDC login."
        }
    };
    foreach (var path in document.Paths.Where(p => p.Key.StartsWith("/api/v1/", StringComparison.Ordinal)))
        foreach (var operation in (IEnumerable<OpenApiOperation>?)path.Value.Operations?.Values ?? Enumerable.Empty<OpenApiOperation>())
        {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("session", document)] = []
            });
        }
    return Task.CompletedTask;
}));

var app = builder.Build();
var mongoUri = app.Configuration["Mongo:Uri"];
var mongoDatabase = app.Configuration["Mongo:Database"];
if (string.IsNullOrWhiteSpace(mongoUri) || string.IsNullOrWhiteSpace(mongoDatabase) ||
    mongoDatabase is "admin" or "local" or "config" || mongoDatabase.Contains(' '))
    throw new InvalidOperationException("Mongo:Uri and a dedicated Mongo:Database are required.");
app.Use(async (context, next) =>
{
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
            if (buffer.Length > 0 && context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
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
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health/live", () => TypedResults.Ok(new HealthStatus("live"))).AllowAnonymous().WithName("Liveness");
app.MapGet("/health/ready", async (HttpContext context, MongoConnection mongo, CancellationToken cancellationToken) =>
{
    try
    {
        if (await mongo.IsReadyAsync(cancellationToken))
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
