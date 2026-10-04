using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;

namespace UnitToko.Api.Identity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountContactQueryDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountContactHistoryQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string? BeforeHistoryId, [property: JsonRequired] int Limit);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountContactSaveDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Name, [property: JsonRequired] string? Phone,
    [property: JsonRequired] string? Position, [property: JsonRequired] string? Address,
    [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountContactRecoveryQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Key);
public sealed record AccountContactCurrentDto(LongId UserId, string Revision, string? Name, string? Phone,
    string? Position, string? Address, DateTimeOffset? UpdatedAt);
public sealed record AccountContactResultDto(LongId UserId, string Revision, LongId HistoryId, DateTimeOffset RecordedAt);
public sealed record AccountContactFieldsDto(string Name, string? Phone, string? Position, string? Address);
public sealed record AccountContactHistoryItemDto(LongId HistoryId, string Revision, AccountContactFieldsDto Contact,
    DateTimeOffset RecordedAt, string Reason);
public sealed record AccountContactHistoryDto(IReadOnlyList<AccountContactHistoryItemDto> Items, LongId? NextBeforeHistoryId);
public sealed record AccountContactRecoveryDto(AccountContactResultDto? Result);

/// <summary>Own contact declarations only. No caller-selected user or provider identity/security change.</summary>
public static class AccountContactEndpoints
{
    public static void MapAccountContactEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/identity/contact")
            .RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));

        group.MapPost("/query", async (AccountContactQueryDto request, HttpContext http,
            [FromServices] AccountContactService service, CancellationToken ct) =>
        {
            Purpose(request.Purpose);
            var row = await service.CurrentAsync(Actor(http), ct);
            NoStore(http); http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row.Revision);
            return TypedResults.Ok(new AccountContactCurrentDto(new(row.UserId), Revision(row.Revision), row.Name,
                row.Phone, row.Position, row.Address, row.UpdatedAt));
        }).WithName("ReadOwnAccountContact");

        group.MapPost("/history/query", async (AccountContactHistoryQueryDto request, HttpContext http,
            [FromServices] AccountContactService service, CancellationToken ct) =>
        {
            Purpose(request.Purpose);
            var before = request.BeforeHistoryId is null ? (long?)null : LongId.Parse(request.BeforeHistoryId).Value;
            var row = await service.HistoryAsync(Actor(http), before, request.Limit, ct);
            NoStore(http);
            return TypedResults.Ok(new AccountContactHistoryDto(row.Items.Select(item => new AccountContactHistoryItemDto(
                new(item.HistoryId), Revision(item.Revision), new(item.Contact.Name, item.Contact.Phone,
                    item.Contact.Position, item.Contact.Address), item.RecordedAt, item.Reason)).ToArray(),
                row.NextBeforeHistoryId is { } next ? new LongId(next) : null));
        }).WithName("ReadOwnAccountContactHistory");

        group.MapPost("", async (AccountContactSaveDto request, HttpContext http,
            [FromServices] AccountContactService service,
            [FromHeader(Name = "Idempotency-Key")] string key,
            [FromHeader(Name = "If-Match")] string version, CancellationToken ct) =>
        {
            Purpose(request.Purpose);
            var row = await service.SaveAsync(Actor(http), new(request.Name, request.Phone, request.Position, request.Address),
                AccessRequestMetadata.ExpectedVersion(http.Request), new AuthorizationCommandMetadata(
                    AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            NoStore(http); http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row.Revision);
            return TypedResults.Ok(Result(row));
        }).WithName("SaveOwnAccountContact");

        group.MapPost("/operations/query", async (AccountContactRecoveryQueryDto request, HttpContext http,
            [FromServices] AccountContactService service, CancellationToken ct) =>
        {
            Purpose(request.Purpose);
            if (request.Key is null || request.Key.Length is < 1 or > 128 || request.Key.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or ':')))
                throw new DomainValidationException("invalid_idempotency_key", "Use the original contact operation key");
            var row = await service.LookupAsync(Actor(http), request.Key, ct);
            NoStore(http);
            return TypedResults.Ok(new AccountContactRecoveryDto(row is null ? null : Result(row)));
        }).WithName("RecoverOwnAccountContact");
    }

    private static AccountContactResultDto Result(AccountContactResult row) =>
        new(new(row.UserId), Revision(row.Revision), new(row.HistoryId), row.RecordedAt);
    private static string Revision(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ??
        throw new AuthorizationDeniedException("unauthenticated");
    private static void Purpose(string? value)
    {
        if (value != "account-contact")
            throw new DomainValidationException("invalid_account_contact_purpose", "An explicit own-contact purpose is required");
    }
    private static void NoStore(HttpContext http)
    {
        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers.Pragma = "no-cache";
        http.Response.Headers.XContentTypeOptions = "nosniff";
    }
}
