using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Api.Identity;

public static class IdentityEndpoints
{
    private const string AccountPath = "/identity/account";
    private const string LoggedOutPath = "/identity/login?loggedOut=1";
    private const string FormToken = "__RequestVerificationToken";

    public static void MapIdentityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/identity/session", async (HttpContext http, [FromServices] IdentityOnboardingService onboarding,
            [FromServices] AuthorizationStore store, [FromServices] IClock clock, CancellationToken ct) =>
        {
            NoStore(http);
            var session = CurrentSession(http);
            if (session is null)
                return TypedResults.Ok(new IdentitySessionDto(false, null, null, null, null, null, clock.UtcNow, []));
            var actor = Actor(session);
            var snapshot = await store.ReadCurrentAsync(actor, ct);
            var profile = await onboarding.ReadOwnProfileAsync(actor, ct)
                ?? throw new AuthorizationDeniedException("account_profile_unavailable");
            var operations = Permissions.Catalog.Values.Where(permission =>
                    ScopePolicy.CanRequestStepUp(actor, snapshot, permission.Name, clock.UtcNow))
                .Select(permission => permission.Name).Order(StringComparer.Ordinal).ToArray();
            return TypedResults.Ok(new IdentitySessionDto(true, new(new(profile.UserId), profile.DisplayName, profile.VerifiedEmail),
                session.Proof.FactorCompletedAt, session.Proof.FactorCompletedAt + SessionPolicy.FreshFactorLifetime,
                session.IdleExpiresAt, session.AbsoluteExpiresAt, clock.UtcNow, operations));
        }).AllowAnonymous().WithName("IdentitySession").Produces<ApiProblem>(403, "application/problem+json")
            .Produces<ApiProblem>(503, "application/problem+json");

        var login = app.MapPost("/auth/login", async (HttpContext http, [FromServices] IdentityOnboardingService onboarding,
            [FromServices] OidcTransactionStore transactions, [FromServices] IOptions<Auth0Options> configured, CancellationToken ct) =>
        {
            NoStore(http);
            var form = await ReadFormAsync(http, ct, "returnPath", "invitationId", "invitationSecret");
            if (CurrentSession(http) is not null)
                throw new PersistenceConflictException("already_signed_in", "Sign out before starting a different sign-in");
            var destination = Destination(form, configured.Value);
            var invitationId = Optional(form, "invitationId");
            var secret = Optional(form, "invitationSecret");
            if ((invitationId is null) != (secret is null)) throw InvalidForm();
            var binding = invitationId is null ? null : await onboarding.BindInvitationAsync(LongId.Parse(invitationId).Value, secret!, ct);
            var started = await transactions.BeginAsync(new(OidcTransactionPurpose.Login, configured.Value.Issuer,
                configured.Value.ClientId, destination, InvitationId: binding?.InvitationId, InvitationSecretHash: binding?.SecretHash), ct);
            return Results.Challenge(UnitTokoOidcChallenge.Properties(started), [Auth0Options.Scheme]);
        }).AllowAnonymous().WithName("BeginIdentityLogin").Accepts<IdentityLoginForm>("application/x-www-form-urlencoded");
        FormResponses(login);

        var stepUp = app.MapPost("/auth/step-up", async (HttpContext http, [FromServices] AuthorizationStore store,
            [FromServices] OidcTransactionStore transactions, [FromServices] IOptions<Auth0Options> configured, [FromServices] IClock clock, CancellationToken ct) =>
        {
            NoStore(http);
            var form = await ReadFormAsync(http, ct, "returnPath", "operation", "expectedActorId");
            var session = CurrentSession(http);
            if (session is null) return Unauthenticated(http);
            ExpectedActor(form, session);
            var destination = Destination(form, configured.Value);
            var operation = Required(form, "operation");
            var actor = Actor(session);
            var snapshot = await store.ReadCurrentAsync(actor, ct);
            if (!ScopePolicy.CanRequestStepUp(actor, snapshot, operation, clock.UtcNow))
                throw new AuthorizationDeniedException("step_up_not_permitted");
            var started = await transactions.BeginAsync(new(OidcTransactionPurpose.StepUp, configured.Value.Issuer,
                configured.Value.ClientId, destination, session.UserId, session.Reference, session.GrantVersion,
                session.Proof.Subject, operation), ct);
            return Results.Challenge(UnitTokoOidcChallenge.Properties(started), [Auth0Options.Scheme]);
        }).AllowAnonymous().WithName("BeginIdentityStepUp").Accepts<IdentityStepUpForm>("application/x-www-form-urlencoded");
        FormResponses(stepUp);

        var logout = app.MapPost("/auth/logout", async (HttpContext http, [FromServices] MongoIdentitySessionStore sessions, CancellationToken ct) =>
        {
            NoStore(http);
            var form = await ReadFormAsync(http, ct, "expectedActorId");
            var session = CurrentSession(http);
            if (session is null) return Unauthenticated(http);
            ExpectedActor(form, session);
            // Local family revocation completes first. This endpoint makes no global-provider-logout claim.
            await sessions.RevokeAsync(http.Request.Cookies[OpaqueSessionCookie.Name]!, "owner_logout", ct);
            OpaqueSessionCookie.Delete(http.Response);
            return Results.LocalRedirect(LoggedOutPath);
        }).AllowAnonymous().WithName("IdentityLogout").Accepts<IdentityLogoutForm>("application/x-www-form-urlencoded");
        FormResponses(logout);
    }

    private static IdentitySession? CurrentSession(HttpContext http) => http.User.Identity?.IsAuthenticated == true
        ? http.Features.Get<VerifiedSessionFeature>()?.Session : null;
    private static ActorContext Actor(IdentitySession session) => new(session.UserId, session.Proof.Issuer,
        session.Proof.Subject, session.GrantVersion, session.Proof.FactorCompletedAt, FactorAssurance.MultiFactor, session.Reference);
    private static void NoStore(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
    private static IResult Unauthenticated(HttpContext http) => Results.Json(
        Problems.Create(http, 401, "unauthenticated", "Authentication required"), statusCode: 401, contentType: "application/problem+json");
    private static void FormResponses(RouteHandlerBuilder builder)
    {
        builder.Produces(302);
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            builder.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
    }
    private static async Task<IFormCollection> ReadFormAsync(HttpContext http, CancellationToken ct, params string[] allowed)
    {
        if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var mediaType) ||
            !string.Equals(mediaType.MediaType, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ||
            http.Request.Query.Count != 0) throw InvalidForm();
        var form = await http.Request.ReadFormAsync(ct);
        if (form.Files.Count != 0 || form.Count > allowed.Length + 1 ||
            form.Any(field => field.Value.Count != 1 || field.Key != FormToken && !allowed.Contains(field.Key, StringComparer.Ordinal)))
            throw InvalidForm();
        return form;
    }
    private static string? Optional(IFormCollection form, string key)
    {
        if (!form.TryGetValue(key, out var value)) return null;
        var text = value[0];
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > 512 || text.Any(char.IsControl)) throw InvalidForm();
        return text;
    }
    private static string Required(IFormCollection form, string key) => Optional(form, key) ?? throw InvalidForm();
    private static string Destination(IFormCollection form, Auth0Options options) => ReturnPathPolicy.Require(Optional(form, "returnPath") ?? AccountPath, options);
    private static void ExpectedActor(IFormCollection form, IdentitySession session)
    {
        if (LongId.Parse(Required(form, "expectedActorId")).Value != session.UserId)
            throw new PersistenceConflictException("actor_changed", "The authenticated account changed; review this request again");
    }
    private static DomainValidationException InvalidForm() => new("invalid_identity_form", "A complete, single-valued identity form is required");
}
