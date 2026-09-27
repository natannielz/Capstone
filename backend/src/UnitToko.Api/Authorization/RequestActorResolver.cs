using UnitToko.Modules.Identity.Contracts;

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
        return await next(context);
    }
}

public sealed record ResolvedActor(ActorContext Actor);
