using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Mongo;

namespace UnitToko.Api.Authorization;

// T03 supplies this from a verified, current application session. Never resolve an
// actor from request headers, submitted role names, or the legacy demo cookie.
public interface IRequestActorResolver
{
    ValueTask<ActorContext?> ResolveAsync(HttpContext context, CancellationToken cancellationToken);
}

public sealed class UnavailableRequestActorResolver : IRequestActorResolver
{
    public ValueTask<ActorContext?> ResolveAsync(HttpContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ActorContext?>(null);
}

public sealed class ActorRequiredFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var resolver = http.RequestServices.GetRequiredService<IRequestActorResolver>();
        var actor = http.User.Identity?.IsAuthenticated == true
            ? await resolver.ResolveAsync(http, http.RequestAborted)
            : null;
        if (actor is null)
            return Results.Json(Problems.Create(http, 401, "unauthenticated", "Authentication required"),
                statusCode: 401, contentType: "application/problem+json");
        http.Features.Set(new ResolvedActor(actor.Validate()));
        // This is a stale-tab consistency guard, never an authentication source.
        if (NeedsExpectedActor(http.Request.Method, http.Request.Path.Value ?? "") || http.Request.Headers.ContainsKey("X-Expected-Actor-Id"))
        {
            var expected = http.Request.Headers["X-Expected-Actor-Id"];
            if (expected.Count != 1 || string.IsNullOrEmpty(expected[0]))
                throw new DomainValidationException("expected_actor_required", "The reviewed account reference is required");
            if (LongId.Parse(expected[0]!).Value != actor.UserId)
                throw new PersistenceConflictException("actor_changed", "The authenticated account changed; reload before continuing");
        }
        return await next(context);
    }
    internal static bool NeedsExpectedActor(string method, string path) => HttpMethods.IsPost(method) &&
        (path.StartsWith("/api/v1/identity/administration/", StringComparison.OrdinalIgnoreCase) ||
         ((path.StartsWith("/api/v1/access/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/v1/organizations", StringComparison.OrdinalIgnoreCase)) &&
          !path.EndsWith("/preview", StringComparison.OrdinalIgnoreCase) && !path.EndsWith("/query", StringComparison.OrdinalIgnoreCase)));
}

public sealed record ResolvedActor(ActorContext Actor);
