using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;

namespace UnitToko.Api.Identity;

public static class IdentityAdministrationEndpoints
{
    public static void MapIdentityAdministrationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/identity/administration")
            .RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));

        group.MapPost("/directory/query", async (IdentityDirectoryQueryDto request, HttpContext http,
            [FromServices] IdentityOnboardingService service, CancellationToken ct) =>
            TypedResults.Ok(IdentityAdministrationResponses.From(await service.ListProfilesAsync(
                Actor(http), request.Offset, request.Limit, ReadMetadata(http, request.Purpose), request.UserId?.Value, ct))))
            .WithName("IdentityAdministrationDirectoryQuery");

        group.MapPost("/directory/search", async (IdentityDirectorySearchDto request, HttpContext http,
            [FromServices] IdentityOnboardingService service, CancellationToken ct) =>
            TypedResults.Ok(IdentityAdministrationResponses.From(await service.SearchProfilesAsync(
                Actor(http), request.Term, request.Offset, request.Limit, ReadMetadata(http, request.Purpose), ct))))
            .WithName("IdentityAdministrationDirectorySearch");

        group.MapPost("/invitations/query", async (IdentityInvitationQueryDto request, HttpContext http,
            [FromServices] IdentityOnboardingService service, CancellationToken ct) =>
            TypedResults.Ok(IdentityAdministrationResponses.From(await service.ListInvitationsAsync(
                Actor(http), request.Offset, request.Limit, ReadMetadata(http, request.Purpose), ct))))
            .WithName("IdentityAdministrationInvitationsQuery");

        group.MapPost("/invitations", async (IdentityInvitationProposalDto request, HttpContext http,
            [FromServices] IdentityOnboardingService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            CancellationToken ct) =>
        {
            var result = await service.ProposeAsync(Actor(http), request.ToDomain(), Metadata(http, request.Reason), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Version);
            return TypedResults.Ok(IdentityAdministrationResponses.From(result));
        }).WithName("IdentityAdministrationProposeInvitation");

        MapDecision(group, "/invitations/{id}/approve", "IdentityAdministrationApproveInvitation",
            (service, actor, id, version, metadata, ct) => service.ApproveAsync(actor, id, version, metadata, ct));
        MapDecision(group, "/invitations/{id}/reject", "IdentityAdministrationRejectInvitation",
            (service, actor, id, version, metadata, ct) => service.RejectAsync(actor, id, version, metadata, ct));
        MapDecision(group, "/invitations/{id}/withdraw", "IdentityAdministrationWithdrawInvitation",
            (service, actor, id, version, metadata, ct) => service.WithdrawAsync(actor, id, version, metadata, ct));
        MapDecision(group, "/invitations/{id}/revoke", "IdentityAdministrationRevokeInvitation",
            (service, actor, id, version, metadata, ct) => service.RevokeAsync(actor, id, version, metadata, ct));

        group.MapPost("/recovery", async (IdentityRecoveryRequestDto request, HttpContext http,
            [FromServices] AccountRecoveryService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
        {
            var kind = request.Kind switch
            {
                "password" => AccountRecoveryKind.Password,
                "factor" => AccountRecoveryKind.Factor,
                _ => throw new DomainValidationException("invalid_recovery_kind", "Choose password or factor recovery")
            };
            var result = await service.InitiateAsync(Actor(http), request.UserId.Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), kind, Metadata(http, request.Reason), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.SecurityVersion);
            return TypedResults.Ok(IdentityAdministrationResponses.From(result));
        }).WithName("IdentityAdministrationRequestRecovery");
    }

    private static void MapDecision(RouteGroupBuilder group, string path, string name,
        Func<IdentityOnboardingService, ActorContext, long, long, AuthorizationCommandMetadata,
            CancellationToken, Task<InvitationResult>> execute) =>
        group.MapPost(path, async (string id, IdentityDecisionDto request, HttpContext http,
            [FromServices] IdentityOnboardingService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
        {
            var result = await execute(service, Actor(http), LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), Metadata(http, request.Reason), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Version);
            return TypedResults.Ok(IdentityAdministrationResponses.From(result));
        }).WithName(name);

    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ??
        throw new AuthorizationDeniedException("unauthenticated");
    private static AuthorizationReadMetadata ReadMetadata(HttpContext http, string purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > 1000 || purpose.Any(char.IsControl))
            throw new DomainValidationException("invalid_read_purpose", "A bounded purpose is required");
        return new(purpose.Trim(), http.TraceIdentifier);
    }
    private static AuthorizationCommandMetadata Metadata(HttpContext http, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000 || reason.Any(char.IsControl))
            throw new DomainValidationException("invalid_reason", "A bounded reason is required");
        return new(AccessRequestMetadata.IdempotencyKey(http.Request), reason.Trim(), http.TraceIdentifier);
    }
}
