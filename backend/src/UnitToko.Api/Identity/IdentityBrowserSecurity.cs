using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace UnitToko.Api.Identity;

public static class IdentityBrowserSecurity
{
    public static bool IsProtectedPath(PathString path) => path.StartsWithSegments("/api/v1") || path.StartsWithSegments("/auth");
    public static bool IsIdentityPath(PathString path) => path.StartsWithSegments("/api/v1/identity") || path.StartsWithSegments("/auth");

    public static IApplicationBuilder UseIdentityTransport(this IApplicationBuilder app, bool enabled) => app.Use(async (context, next) =>
    {
        if (!IsProtectedPath(context.Request.Path)) { await next(context); return; }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
        if (!enabled)
        {
            if (IsIdentityPath(context.Request.Path))
            {
                await Problems.WriteAsync(context, 503, "identity_unavailable", "Sign-in service is not configured");
                return;
            }
            await next(context); return;
        }
        var origin = context.RequestServices.GetRequiredService<IOptions<Auth0Options>>().Value.PublicOrigin;
        if (!context.Request.IsHttps || context.Request.PathBase.HasValue ||
            context.Request.Scheme + "://" + context.Request.Host.Value != origin)
        {
            await Problems.WriteAsync(context, 403, "untrusted_origin", "Request origin is not allowed");
            return;
        }
        if (!HasTrustedBrowserContext(context, origin))
        {
            await Problems.WriteAsync(context, 403, "untrusted_origin", "Request origin is not allowed"); return;
        }
        context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
        // Only the maintained query callback may arrive as a cross-site navigation.
        // POST callbacks are not supported by this code+PKCE query flow.
        if ((context.Request.Path.Equals(Auth0Options.CallbackPath, StringComparison.OrdinalIgnoreCase) ||
             context.Request.Path.Equals(Auth0Options.SignedOutCallbackPath, StringComparison.OrdinalIgnoreCase)) &&
            !HttpMethods.IsGet(context.Request.Method))
        {
            await Problems.WriteAsync(context, 405, "method_not_allowed", "Method not allowed"); return;
        }
        await next(context);
    });

    public static IApplicationBuilder UseIdentityAntiforgery(this IApplicationBuilder app, bool enabled) => app.Use(async (context, next) =>
    {
        if (!enabled || !IsProtectedPath(context.Request.Path)) { await next(context); return; }
        var safe = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method);
        if (!safe)
        {
            try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException)
            {
                await Problems.WriteAsync(context, 403, "antiforgery_failed", "Refresh this page before trying again"); return;
            }
        }
        await next(context);
    });
    private static bool HasTrustedBrowserContext(HttpContext context, string origin)
    {
        var safe = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method);
        var suppliedOrigin = context.Request.Headers.Origin;
        var fetchSite = context.Request.Headers["Sec-Fetch-Site"];
        return !((!safe && (suppliedOrigin.Count != 1 || suppliedOrigin[0] != origin)) ||
            fetchSite.Count > 1 || (!safe && fetchSite.Count == 1 && fetchSite[0] != "same-origin") ||
            (context.Request.Path.StartsWithSegments("/api/v1") && (fetchSite.ToString() is "cross-site" or "same-site" ||
                suppliedOrigin.Count > 0 && (suppliedOrigin.Count != 1 || suppliedOrigin[0] != origin))));
    }}
