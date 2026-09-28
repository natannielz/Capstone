using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;

namespace UnitToko.Api.Identity;

public static class PersonReviewEndpoints
{
    public static void MapPersonReviewEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/identity/administration/person-reviews")
            .RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));

        group.MapPost("/proposals/query", async (PersonReviewListDto request, HttpContext http,
            [FromServices] PersonWithdrawalService service, CancellationToken ct) =>
        {
            Page(request.Offset, request.Limit);
            var status = request.Status?.Trim().ToLowerInvariant();
            if (request.Status?.Any(char.IsControl) == true || status is not (null or "pending" or "approved" or "rejected" or "withdrawn"))
                throw new DomainValidationException("invalid_review_status", "Choose a review status");
            return TypedResults.Ok(PersonReviewResponses.From(await service.ListReviewsAsync(Actor(http), status,
                request.Offset, request.Limit, ReadMetadata(http, request.Purpose), ct)));
        }).WithName("PersonReviewList");
        group.MapPost("/targets/search", async (PersonReviewSearchDto request, HttpContext http,
            [FromServices] PersonWithdrawalService service, CancellationToken ct) =>
        {
            Page(request.Offset, request.Limit);
            if (request.Term is null || request.Term.Any(char.IsControl) || request.Term.Trim().Length is < 2 or > 80)
                throw new DomainValidationException("invalid_review_search", "Enter a bounded account search");
            return TypedResults.Ok(PersonReviewResponses.From(await service.SearchTargetsAsync(Actor(http), request.Term.Trim(),
                request.Offset, request.Limit, ReadMetadata(http, request.Purpose), ct)));
        }).WithName("PersonReviewTargetSearch");
        group.MapPost("/proposals/{id}/query", async (string id, PersonReviewReadDto request, HttpContext http,
            [FromServices] PersonWithdrawalService service, CancellationToken ct) =>
        {
            var result = await service.ReadReviewAsync(Actor(http), LongId.Parse(id).Value, ReadMetadata(http, request.Purpose), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Summary.Result.Version);
            return TypedResults.Ok(PersonReviewResponses.From(result));
        }).WithName("PersonReviewDetail");

        MapPreview(group, "/targets/{id}/preview", "PersonReviewProposalPreview",
            (service, actor, id, version, audit, ct) => service.PreviewProposeAsync(actor, id, version, audit, ct));
        MapPreview(group, "/proposals/{id}/preview", "PersonReviewApprovalPreview",
            (service, actor, id, version, audit, ct) => service.PreviewApproveAsync(actor, id, version, audit, ct));

        group.MapPost("/proposals", async (PersonReviewSubmissionDto request, HttpContext http,
            [FromServices] PersonWithdrawalService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            CancellationToken ct) =>
        {
            var result = await service.SubmitProposalAsync(Actor(http), Reference(request.PreviewReference), Metadata(http, request.Reason), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Version);
            return TypedResults.Ok(PersonReviewResponses.From(result));
        }).WithName("PersonReviewPropose");
        group.MapPost("/proposals/{id}/approve", async (string id, PersonReviewSubmissionDto request, HttpContext http,
            [FromServices] PersonWithdrawalService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
        {
            var result = await service.ApproveReviewedAsync(Actor(http), LongId.Parse(id).Value,
                Version(http), Reference(request.PreviewReference), Metadata(http, request.Reason), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Version);
            return TypedResults.Ok(PersonReviewResponses.From(result));
        }).WithName("PersonReviewApprove");
        MapDecision(group, "/proposals/{id}/reject", "PersonReviewReject",
            (service, actor, id, version, metadata, ct) => service.RejectAsync(actor, id, version, metadata, ct));
        MapDecision(group, "/proposals/{id}/withdraw", "PersonReviewWithdraw",
            (service, actor, id, version, metadata, ct) => service.WithdrawAsync(actor, id, version, metadata, ct));

        group.MapPost("/operations/query", async (PersonReviewReceiptQueryDto request, HttpContext http,
            [FromServices] PersonWithdrawalService service, CancellationToken ct) =>
        {
            if (request.Operation is not ("person.review.propose" or "person.review.approve" or "person.review.reject" or "person.review.withdraw"))
                throw new DomainValidationException("invalid_operation", "Choose a person review operation");
            if (string.IsNullOrEmpty(request.Key) || request.Key.Length > 128 ||
                request.Key.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or ':')))
                throw new DomainValidationException("invalid_idempotency_key", "A bounded request key is required");
            var result = await service.ReadOperationOutcomeAsync(Actor(http), request.Operation, request.Key,
                ReadMetadata(http, request.Purpose, "operation-recovery"), ct);
            if (result.Result is { } committed) http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(committed.Version);
            return TypedResults.Ok(PersonReviewResponses.From(result));
        }).WithName("PersonReviewReceiptQuery");
        group.MapPost("/operations/resolve", async (PersonReviewResolutionDto request, HttpContext http,
            [FromServices] PersonWithdrawalService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            CancellationToken ct) =>
        {
            if (request.Operation is not ("person.review.propose" or "person.review.approve" or "person.review.reject" or "person.review.withdraw"))
                throw new DomainValidationException("invalid_operation", "Choose a person review operation");
            var result = await service.ResolveOperationAsync(Actor(http), request.Operation, Metadata(http, request.Reason), ct);
            if (result.Result is { } committed) http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(committed.Version);
            return TypedResults.Ok(PersonReviewResponses.From(result));
        }).WithName("PersonReviewReceiptResolve");
    }

    private static void MapPreview(RouteGroupBuilder group, string path, string name,
        Func<PersonWithdrawalService, ActorContext, long, long, AuthorizationReadMetadata, CancellationToken, Task<PersonReviewPreview>> execute) =>
        group.MapPost(path, async (string id, PersonReviewReadDto request, HttpContext http,
            [FromServices] PersonWithdrawalService service, [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
        {
            var result = await execute(service, Actor(http), LongId.Parse(id).Value,
                Version(http), ReadMetadata(http, request.Purpose), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Proposal?.Result.Version ?? result.Target.Version);
            return TypedResults.Ok(PersonReviewResponses.From(result));
        }).WithName(name);
    private static void MapDecision(RouteGroupBuilder group, string path, string name,
        Func<PersonWithdrawalService, ActorContext, long, long, AuthorizationCommandMetadata, CancellationToken, Task<PersonWithdrawalResult>> execute) =>
        group.MapPost(path, async (string id, IdentityDecisionDto request, HttpContext http,
            [FromServices] PersonWithdrawalService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
        {
            var result = await execute(service, Actor(http), LongId.Parse(id).Value,
                Version(http), Metadata(http, request.Reason), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Version);
            return TypedResults.Ok(PersonReviewResponses.From(result));
        }).WithName(name);
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ??
        throw new AuthorizationDeniedException("unauthenticated");
    private static AuthorizationReadMetadata ReadMetadata(HttpContext http, string purpose, string expected = "access-review")
    {
        if (purpose != expected) throw new DomainValidationException("invalid_read_purpose", "Choose the required review purpose");
        return new(purpose, http.TraceIdentifier);
    }
    private static AuthorizationCommandMetadata Metadata(HttpContext http, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000 || reason.Any(char.IsControl))
            throw new DomainValidationException("invalid_reason", "A bounded reason is required");
        return new(AccessRequestMetadata.IdempotencyKey(http.Request), reason.Trim(), http.TraceIdentifier);
    }
    private static void Page(int offset, int limit)
    {
        if (offset is < 0 or > 10_000 || limit is < 1 or > 100)
            throw new DomainValidationException("invalid_page", "Choose a bounded result page");
    }
    private static long Version(HttpContext http)
    {
        var version = AccessRequestMetadata.ExpectedVersion(http.Request);
        if (version <= 0) throw new DomainValidationException("invalid_version", "Choose a positive reviewed version");
        return version;
    }
    private static string Reference(string reference)
    {
        if (!Guid.TryParseExact(reference, "D", out var parsed) || parsed.ToString("D") != reference ||
            reference[14] != '4' || !"89ab".Contains(reference[19]))
            throw new DomainValidationException("invalid_preview_reference", "Choose a current review preview");
        return reference;
    }
}
