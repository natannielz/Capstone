using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class PaymentAllocationInvoiceChoiceApiBoundaryTests(MongoFixture fixture)
{
    private const string Origin = "https://portal.unit-toko.co.id";
    private const string Route = "/api/v1/billing/payment-verifications/1/allocations/invoices/query";

    [Fact]
    public async Task Invoice_choices_require_authenticated_actor_origin_csrf_and_expected_actor()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(88101, new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103));
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        using var anonymousFactory = Factory(data, null, false, keys);
        using var anonymous = anonymousFactory.CreateClient(new() { BaseAddress = new(Origin) });
        var anonymousCsrf = await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf");
        using (var anonymousRequest = Request("""{"purpose":"payment-allocation"}""", actor.UserId,
            anonymousCsrf.GetProperty("requestToken").GetString()!))
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(anonymousRequest)).StatusCode);

        using var factory = Factory(data, actor, true, keys);
        using var client = factory.CreateClient(new() { BaseAddress = new(Origin) });
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf");
        var token = csrf.GetProperty("requestToken").GetString()!;
        using (var noOrigin = Request("""{"purpose":"payment-allocation"}""", actor.UserId, token, origin: false))
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(noOrigin)).StatusCode);
        using (var noCsrf = Request("""{"purpose":"payment-allocation"}""", actor.UserId, token, csrf: false))
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(noCsrf)).StatusCode);
        using (var noActor = Request("""{"purpose":"payment-allocation"}""", actor.UserId, token, expectedActor: false))
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(noActor)).StatusCode);
        using (var wrongActor = Request("""{"purpose":"payment-allocation"}""", actor.UserId + 1, token))
            Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(wrongActor)).StatusCode);
    }

    [Fact]
    public async Task Invoice_choices_reject_wrong_purpose_unknown_fields_and_non_lossless_identifiers_before_source_read()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(88102, new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103));
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        using var factory = Factory(data, actor, true, keys);
        using var client = factory.CreateClient(new() { BaseAddress = new(Origin) });
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf");
        var token = csrf.GetProperty("requestToken").GetString()!;
        foreach (var body in new[] {
            """{"purpose":"payment-review"}""",
            """{"purpose":"payment-allocation","buyingOrganizationId":"10"}""",
            """{"purpose":"payment-allocation","beforeInvoiceId":9007199254740993}""",
            """{"purpose":"payment-allocation","beforeInvoiceId":"0"}""",
            """{"purpose":"payment-allocation","beforeInvoiceId":"01"}""",
        }) {
            using var request = Request(body, actor.UserId, token);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        }
        using var invalidSource = Request("""{"purpose":"payment-allocation"}""", actor.UserId, token);
        invalidSource.RequestUri = new Uri(Origin + "/api/v1/billing/payment-verifications/0/allocations/invoices/query");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(invalidSource)).StatusCode);
        Assert.Equal(0L, await data.Database.GetCollection<BsonDocument>("auditEvents").CountDocumentsAsync(new BsonDocument()));
    }

    private static WebApplicationFactory<Program> Factory(StoreTestData data, ActorContext? actor, bool authenticated,
        IdentityRuntimeTests.RuntimeKeys keys) {
        var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        return IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web =>
            web.ConfigureTestServices(services => {
                services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
                if (authenticated) {
                    services.AddAuthentication("AccessTest").AddScheme<AuthenticationSchemeOptions,
                        AccessTestAuthenticationHandler>("AccessTest", _ => { });
                    services.PostConfigure<AuthenticationOptions>(options => {
                        options.DefaultAuthenticateScheme = "AccessTest";
                        options.DefaultChallengeScheme = "AccessTest";
                    });
                }
                if (actor is not null) {
                    services.RemoveAll<IRequestActorResolver>();
                    services.AddSingleton<IRequestActorResolver>(new Resolver(actor));
                }
            }));
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");
    private static HttpRequestMessage Request(string json, long actorId, string token, bool origin = true, bool csrf = true,
        bool expectedActor = true) {
        var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = Body(json) };
        if (origin) request.Headers.Add("Origin", Origin);
        if (csrf) request.Headers.Add("X-CSRF-Token", token);
        if (expectedActor) request.Headers.Add("X-Expected-Actor-Id", actorId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return request;
    }
    private sealed class Resolver(ActorContext actor) : IRequestActorResolver {
        public ValueTask<ActorContext?> ResolveAsync(HttpContext context, CancellationToken ct) => ValueTask.FromResult<ActorContext?>(actor);
    }
}
