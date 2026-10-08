using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;

namespace UnitToko.Api.Authorization;

public sealed record DelegationRequestDto([property: JsonRequired] LongId SourceGrantId, [property: JsonRequired] string SourceVersion,
    [property: JsonRequired] LongId BeneficiaryUserId, [property: JsonRequired] LongId BeneficiaryMembershipId,
    [property: JsonRequired] AccessScopeDto Scope, [property: JsonRequired] string[] Permissions,
    [property: JsonRequired] DateTimeOffset EffectiveAt, [property: JsonRequired] DateTimeOffset ExpiresAt)
{
    public TemporaryDelegationRequest Domain() {
        if (Scope is null || Permissions is null || !long.TryParse(SourceVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            version <= 0 || version.ToString(CultureInfo.InvariantCulture) != SourceVersion) throw new DomainValidationException("invalid_delegation", "A complete source selection is required");
        var value = new TemporaryDelegationRequest(SourceGrantId.Value, version, BeneficiaryUserId.Value, BeneficiaryMembershipId.Value,
            Scope.ToDomain(), Permissions.ToImmutableArray(), EffectiveAt, ExpiresAt); TemporaryDelegationPolicy.Validate(value); return value;
    }
    public static DelegationRequestDto From(TemporaryDelegationRequest value) => new(new(value.SourceGrantId), V(value.SourceVersion),
        new(value.BeneficiaryUserId), new(value.BeneficiaryMembershipId), AccessScopeDto.From(value.Scope), [.. value.Permissions], value.EffectiveAt, value.ExpiresAt);
    internal static string V(long value) => value.ToString(CultureInfo.InvariantCulture);
}
public sealed record DelegationChangeDto([property: JsonRequired] string Action, [property: JsonRequired] LongId? Id,
    [property: JsonRequired] DelegationRequestDto? Request, [property: JsonRequired] string Reason);
public sealed record DelegationReadDto([property: JsonRequired] string Purpose, LongId? BeforeId = null);
public sealed record DelegationRecoveryDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Key);
public sealed record DelegationSummaryDto(LongId Id, string Revision, LongId SourceUserId, LongId? ApprovedBy, DelegationRequestDto Request,
    string Status, string SourceState, string Reason, DateTimeOffset UpdatedAt, string[] AllowedActions, string TaskReassignmentState);
public sealed record DelegationPageDto(DelegationSummaryDto[] Items, LongId? NextId);
public sealed record DelegationEventDto(LongId Id, string Revision, LongId ActorId, string Action, string Reason, DateTimeOffset RecordedAt);
public sealed record DelegationDetailDto(DelegationSummaryDto Item, DelegationEventDto[] History);
public sealed record DelegationSourceDto(LongId Id, string Version, AccessScopeDto Scope, string[] Permissions, DateTimeOffset EffectiveAt, DateTimeOffset? ExpiresAt);
public sealed record DelegationResultDto(LongId DelegationId, LongId EventId, string Revision, string Action, string Status, DateTimeOffset RecordedAt);
public sealed record DelegationRecoveryResultDto(string State, DelegationResultDto? Result);

public static class TemporaryDelegationEndpoints
{
    public static void MapTemporaryDelegationEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/access/delegations").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(global::ApiProblem), ["application/problem+json"]));
        group.MapPost("/sources/query", async (DelegationReadDto body, HttpContext http, TemporaryDelegationService service, CancellationToken ct) => {
            Purpose(body.Purpose, "delegation-review"); if (body.BeforeId is not null) throw Invalid();
            return TypedResults.Ok((await service.SourcesAsync(Actor(http), ct)).Select(source => new DelegationSourceDto(new(source.Id),
                DelegationRequestDto.V(source.Version), AccessScopeDto.From(source.Scope), [.. source.Permissions], source.EffectiveAt, source.ExpiresAt)).ToArray());
        }).WithName("TemporaryDelegationSources");
        group.MapPost("/query", async (DelegationReadDto body, HttpContext http, TemporaryDelegationService service, CancellationToken ct) => {
            Purpose(body.Purpose, "delegation-review"); var page = await service.ListAsync(Actor(http), body.BeforeId?.Value, ct);
            return TypedResults.Ok(new DelegationPageDto(page.Items.Select(Summary).ToArray(), page.NextId is { } id ? new LongId(id) : null));
        }).WithName("TemporaryDelegationList");
        group.MapPost("/{id}/query", async (string id, DelegationReadDto body, HttpContext http, TemporaryDelegationService service, CancellationToken ct) => {
            Purpose(body.Purpose, "delegation-review"); if (body.BeforeId is not null) throw Invalid();
            var detail = await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct);
            return TypedResults.Ok(new DelegationDetailDto(Summary(detail.Item), detail.History.Select(row => new DelegationEventDto(new(row.Id),
                DelegationRequestDto.V(row.Revision), new(row.ActorId), row.Action, row.Reason, row.RecordedAt)).ToArray()));
        }).WithName("TemporaryDelegationDetail");
        group.MapPost("/operations/query", async (DelegationRecoveryDto body, HttpContext http, TemporaryDelegationService service, CancellationToken ct) => {
            Purpose(body.Purpose, "operation-recovery"); var result = await service.RecoverAsync(Actor(http), body.Key, ct);
            return TypedResults.Ok(new DelegationRecoveryResultDto(result is null ? "not_confirmed" : "committed", result is null ? null : Result(result)));
        }).WithName("TemporaryDelegationRecovery");
        group.MapPost("", async (DelegationChangeDto body, HttpContext http, TemporaryDelegationService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) => {
            var result = await service.ChangeAsync(Actor(http), body.Action, body.Id?.Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                body.Request?.Domain(), new(AccessRequestMetadata.IdempotencyKey(http.Request), body.Reason, http.TraceIdentifier), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result["revision"].AsInt64); return TypedResults.Ok(Result(result));
        }).WithName("ChangeTemporaryDelegation");
    }
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ?? throw new AuthorizationDeniedException("unauthenticated");
    private static void Purpose(string value, string expected) { if (value != expected) throw Invalid(); }
    private static DomainValidationException Invalid() => new("invalid_delegation", "Select a valid delegation operation");
    private static DelegationSummaryDto Summary(TemporaryDelegationSummary value) => new(new(value.Id), DelegationRequestDto.V(value.Revision),
        new(value.SourceUserId), value.ApprovedBy == 0 ? null : new(value.ApprovedBy), DelegationRequestDto.From(value.Request), value.Status, value.SourceState,
        value.Reason, value.UpdatedAt, [.. value.AllowedActions], value.TaskReassignmentState);
    private static DelegationResultDto Result(MongoDB.Bson.BsonDocument value) => new(new(value["delegationId"].AsInt64), new(value["eventId"].AsInt64),
        DelegationRequestDto.V(value["revision"].AsInt64), value["action"].AsString, value["status"].AsString, new(value["recordedAt"].ToUniversalTime(), TimeSpan.Zero));
}
