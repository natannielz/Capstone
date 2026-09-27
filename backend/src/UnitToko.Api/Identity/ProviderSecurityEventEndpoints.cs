using Microsoft.AspNetCore.Mvc;
using UnitToko.Platform.Identity;

namespace UnitToko.Api.Identity;

internal sealed record ProviderSecurityEndpoint(ProviderEventTransport Transport);

/// <summary>Dedicated machine receivers. Only mapped endpoint metadata grants the browser
/// CSRF exemption; the receiver still requires its own credential and the trusted HTTPS origin.</summary>
public static class ProviderSecurityEventEndpoints
{
    public const string EventsPath = "/api/v1/identity/provider-events/auth0-events";
    public const string LogsPath = "/api/v1/identity/provider-events/auth0-logs";

    internal static bool IsReceiver(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<ProviderSecurityEndpoint>() is not null;

    public static void MapProviderSecurityEventEndpoints(this WebApplication app)
    {
        Map(app, EventsPath, ProviderEventTransport.Auth0Events, "Auth0SecurityEvents");
        Map(app, LogsPath, ProviderEventTransport.Auth0Logs, "Auth0SecurityLogs");
    }

    private static void Map(WebApplication app, string path, ProviderEventTransport transport, string name) =>
        app.MapPost(path, (HttpContext context, [FromHeader(Name = "Authorization")] string? authorization,
            [FromServices] Auth0SecurityEventOptions options) => ReceiveAsync(context, transport, authorization, options))
            .WithMetadata(new ProviderSecurityEndpoint(transport)).AllowAnonymous().WithName(name)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiProblem>(400, "application/problem+json")
            .Produces<ApiProblem>(403, "application/problem+json")
            .Produces<ApiProblem>(409, "application/problem+json")
            .Produces<ApiProblem>(413, "application/problem+json")
            .Produces<ApiProblem>(415, "application/problem+json")
            .Produces<ApiProblem>(429, "application/problem+json")
            .Produces<ApiProblem>(500, "application/problem+json")
            .Produces<ApiProblem>(503, "application/problem+json");

    private static async Task<IResult> ReceiveAsync(HttpContext context, ProviderEventTransport transport,
        string? authorization, Auth0SecurityEventOptions options)
    {
        var enabled = transport == ProviderEventTransport.Auth0Events ? options.EventsEnabled : options.LogsEnabled;
        if (!enabled) return Problem(context, 503, "provider_events_disabled", "Provider event receiver is not configured");
        if (context.Request.Headers.Authorization.Count != 1)
            return Problem(context, 403, "invalid_provider_credential", "Access denied");
        var mediaType = context.Request.ContentType?.Split(';', 2)[0].Trim();
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
            !(transport == ProviderEventTransport.Auth0Events &&
              string.Equals(mediaType, "application/cloudevents+json", StringComparison.OrdinalIgnoreCase)))
            return Problem(context, 415, "unsupported_media_type", "JSON content is required");

        // The outer request middleware already bounded this stream to 64 KiB without parsing it.
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestAborted);
        var service = context.RequestServices.GetRequiredService<ProviderSecurityEventService>();
        await service.ProcessAsync(transport, body.ToArray(), authorization ?? "", context.RequestAborted);
        // Unknown subjects, duplicates and ignored events disclose no account details to callers.
        return TypedResults.NoContent();
    }

    private static IResult Problem(HttpContext context, int status, string code, string title) =>
        Results.Json(Problems.Create(context, status, code, title), statusCode: status, contentType: "application/problem+json");
}
