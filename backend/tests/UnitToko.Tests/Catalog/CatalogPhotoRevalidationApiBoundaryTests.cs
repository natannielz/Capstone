using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed class CatalogPhotoRevalidationApiBoundaryTests(MongoFixture fixture)
{
    private const string Origin = "https://portal.unit-toko.co.id";
    private const string RequestBody = """{"purpose":"catalog-image-revalidation","expectedProofRevision":"0","reference":{"id":"1","version":"1","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"reason":"Review this exact private original again"}""";
    private const string QueryBody = """{"purpose":"catalog-image-revalidation","reference":{"id":"1","version":"1","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}""";
    private const string RecoveryBody = """{"purpose":"catalog-image-revalidation","key":"catalog.photo.revalidate.1.11111111-1111-4111-8111-111111111111"}""";

    [Fact]
    public async Task Revalidation_routes_require_real_actor_origin_csrf_and_dedicated_runtime()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync();
        var route = Route(sku.Id.Value, "1");
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        using var anonymousFactory = Factory(data, null, false, keys);
        using var anonymous = Client(anonymousFactory);
        using (var request = Request(route, RequestBody, data.Maker.UserId, await TokenAsync(anonymous)))
            await StatusAsync(anonymous, request, HttpStatusCode.Unauthorized);

        using var factory = Factory(data, data.Maker, true, keys);
        using var client = Client(factory);
        var token = await TokenAsync(client);
        foreach (var missing in new[] { "Origin", "X-CSRF-Token", "X-Expected-Actor-Id" }) {
            using var request = Request(route, RequestBody, data.Maker.UserId, token);
            request.Headers.Remove(missing);
            await StatusAsync(client, request, missing == "X-Expected-Actor-Id" ? HttpStatusCode.BadRequest : HttpStatusCode.Forbidden);
        }
        using (var wrongOrigin = Request(route, RequestBody, data.Maker.UserId, token)) {
            wrongOrigin.Headers.Remove("Origin"); wrongOrigin.Headers.Add("Origin", "https://other.unit-toko.co.id");
            await StatusAsync(client, wrongOrigin, HttpStatusCode.Forbidden);
        }
        using (var wrongActor = Request(route, RequestBody, data.Buyer.UserId, token))
            await StatusAsync(client, wrongActor, HttpStatusCode.Conflict);
        using (var request = Request(route, RequestBody, data.Maker.UserId, token))
            await UnavailableAsync(client, request);
        using (var request = Request(route + "/query", QueryBody, data.Maker.UserId, token))
            await UnavailableAsync(client, request);
        using (var request = Request("/api/v1/catalog/photo-revalidations/operations/query", RecoveryBody, data.Maker.UserId, token))
            await UnavailableAsync(client, request);
    }

    [Fact]
    public async Task Revalidation_wire_rejects_foreign_fields_purposes_and_noncanonical_lossless_values()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync();
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        using var factory = Factory(data, data.Maker, true, keys);
        using var client = Client(factory);
        var token = await TokenAsync(client);
        var route = Route(sku.Id.Value, "1");
        foreach (var (path, json) in new[] { (route, RequestBody), (route + "/query", QueryBody),
            ("/api/v1/catalog/photo-revalidations/operations/query", RecoveryBody) }) {
            foreach (var edit in new[] { "wrongPurpose", "missingPurpose", "privatePath" }) {
                var body = JsonNode.Parse(json)!.AsObject();
                if (edit == "wrongPurpose") body["purpose"] = "catalog-photo";
                else if (edit == "missingPurpose") body.Remove("purpose");
                else body["privatePath"] = "untrusted/original.png";
                using var request = Request(path, body.ToJsonString(), data.Maker.UserId, token);
                await StatusAsync(client, request, HttpStatusCode.BadRequest);
            }
        }
        foreach (var invalid in new[] { "0", "01", "-1", "1.0", "9223372036854775808" }) {
            using (var request = Request(Route(sku.Id.Value, invalid) + "/query", QueryBody, data.Maker.UserId, token))
                await StatusAsync(client, request, HttpStatusCode.BadRequest);
            using (var request = Request("/api/v1/catalog/skus/" + invalid + "/photo-evidence/1/revalidation/query",
                QueryBody, data.Maker.UserId, token))
                await StatusAsync(client, request, HttpStatusCode.BadRequest);
            foreach (var field in new[] { "id", "version" }) {
                var body = JsonNode.Parse(QueryBody)!.AsObject(); body["reference"]![field] = invalid;
                using var request = Request(route + "/query", body.ToJsonString(), data.Maker.UserId, token);
                await StatusAsync(client, request, HttpStatusCode.BadRequest);
            }
        }
        foreach (var invalid in new[] { "00", "01", "-1", "1.0", "256", "9223372036854775808" }) {
            var body = JsonNode.Parse(RequestBody)!.AsObject(); body["expectedProofRevision"] = invalid;
            using var request = Request(route, body.ToJsonString(), data.Maker.UserId, token);
            await StatusAsync(client, request, HttpStatusCode.BadRequest);
        }
        foreach (var field in new[] { "expectedProofRevision", "reference.id", "reference.version" }) {
            var body = JsonNode.Parse(RequestBody)!.AsObject();
            if (field.StartsWith("reference.", StringComparison.Ordinal)) body["reference"]![field.Split('.')[1]] = 9007199254740993L;
            else body[field] = 9007199254740993L;
            using var request = Request(route, body.ToJsonString(), data.Maker.UserId, token);
            await StatusAsync(client, request, HttpStatusCode.BadRequest);
        }
        using (var request = Request(route, RequestBody, data.Maker.UserId, token)) {
            request.Headers.Remove("If-Match");
            await StatusAsync(client, request, HttpStatusCode.BadRequest);
        }
        using (var request = Request(route, RequestBody, data.Maker.UserId, token)) {
            request.Headers.Remove("Idempotency-Key");
            await StatusAsync(client, request, HttpStatusCode.BadRequest);
        }
    }

    private static WebApplicationFactory<Program> Factory(CatalogAuthoringTestData data, ActorContext? actor,
        bool authenticated, IdentityRuntimeTests.RuntimeKeys keys)
    {
        var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        settings["Release:Profile"] = "full";
        return IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production")
            .ConfigureTestServices(services => {
                services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
                if (authenticated) {
                    services.AddAuthentication("AccessTest").AddScheme<AuthenticationSchemeOptions, AccessTestAuthenticationHandler>("AccessTest", _ => { });
                    services.PostConfigure<AuthenticationOptions>(options => {
                        options.DefaultAuthenticateScheme = "AccessTest"; options.DefaultChallengeScheme = "AccessTest";
                    });
                }
                services.RemoveAll<IRequestActorResolver>(); services.AddSingleton<IRequestActorResolver>(new Resolver(actor));
            }));
    }
    private static HttpClient Client(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new() { BaseAddress = new(Origin), AllowAutoRedirect = false });
    private static async Task<string> TokenAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
    private static string Route(long skuId, string reservationId) => "/api/v1/catalog/skus/" +
        skuId.ToString(CultureInfo.InvariantCulture) + "/photo-evidence/" + reservationId + "/revalidation";
    private static HttpRequestMessage Request(string path, string body, long actorId, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Origin", Origin); request.Headers.Add("X-CSRF-Token", token);
        request.Headers.Add("X-Expected-Actor-Id", actorId.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("Idempotency-Key", "catalog.photo.revalidate.1.11111111-1111-4111-8111-111111111111");
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        return request;
    }
    private static async Task StatusAsync(HttpClient client, HttpRequestMessage request, HttpStatusCode expected)
    {
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
    }
    private static async Task UnavailableAsync(HttpClient client, HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("image_revalidation_runtime_unavailable", body.RootElement.GetProperty("code").GetString());
    }
    private sealed class Resolver(ActorContext? actor) : IRequestActorResolver
    {
        public ValueTask<ActorContext?> ResolveAsync(HttpContext context, CancellationToken ct) => ValueTask.FromResult(actor);
    }
}
