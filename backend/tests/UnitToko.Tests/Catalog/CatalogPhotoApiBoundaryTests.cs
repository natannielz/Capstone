using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
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
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Catalog;

// Production middleware and actual Mongo/catalog authority; AccessTest replaces only provider authentication.
// These HTTP boundary checks do not assert acceptance of a hosted identity provider or private image store.
[Collection("Mongo")]
public sealed class CatalogPhotoApiBoundaryTests(MongoFixture fixture)
{
    private const string Origin = "https://portal.unit-toko.co.id";
    private const string ReadBody = """{"purpose":"catalog-photo"}""";
    private const int ImageLimit = 10 * 1024 * 1024;

    [Fact]
    public async Task Photo_routes_require_authentication_current_actor_origin_and_antiforgery_without_business_changes()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync();
        var route = Staff(sku.Id.Value, "/primary-photo/query");
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        using var anonymousFactory = Factory(data, null, false, keys);
        using var anonymous = Client(anonymousFactory);
        var anonymousToken = await TokenAsync(anonymous);
        var before = await BusinessStateAsync(data);
        foreach (var target in new[] { route, "/api/v1/catalog/variants/" + (await data.RowAsync("skus", sku.Id.Value))["publicId"].AsString + "/primary-photo/query" }) {
            using var request = Request(target, ReadBody, data.Maker.UserId, anonymousToken);
            await StatusAsync(anonymous, request, HttpStatusCode.Unauthorized);
        }

        using var factory = Factory(data, data.Maker, true, keys);
        using var client = Client(factory);
        var token = await TokenAsync(client);
        foreach (var missing in new[] { "Origin", "X-CSRF-Token", "X-Expected-Actor-Id" }) {
            using var request = Request(route, ReadBody, data.Maker.UserId, token);
            request.Headers.Remove(missing);
            await StatusAsync(client, request, missing == "X-Expected-Actor-Id" ? HttpStatusCode.BadRequest : HttpStatusCode.Forbidden);
        }
        using (var wrongOrigin = Request(route, ReadBody, data.Maker.UserId, token)) {
            wrongOrigin.Headers.Remove("Origin"); wrongOrigin.Headers.Add("Origin", "https://other.unit-toko.co.id");
            await StatusAsync(client, wrongOrigin, HttpStatusCode.Forbidden);
        }
        using (var forgedCsrf = Request(route, ReadBody, data.Maker.UserId, "forged"))
            await StatusAsync(client, forgedCsrf, HttpStatusCode.Forbidden);
        using (var changedActor = Request(route, ReadBody, data.Buyer.UserId, token))
            await StatusAsync(client, changedActor, HttpStatusCode.Conflict);
        // An authenticated principal without a resolved current application actor is still denied.
        using var unresolvedFactory = Factory(data, null, true, keys);
        using var unresolved = Client(unresolvedFactory);
        using (var request = Request(route, ReadBody, data.Maker.UserId, await TokenAsync(unresolved)))
            await StatusAsync(unresolved, request, HttpStatusCode.Unauthorized);
        Assert.Equal(before, await BusinessStateAsync(data));
    }

    [Fact]
    public async Task Photo_wire_rejects_unknown_members_wrong_purposes_and_noncanonical_ids_and_revisions_before_writes()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync();
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        using var factory = Factory(data, data.Maker, true, keys);
        using var client = Client(factory);
        var token = await TokenAsync(client);
        var before = await BusinessStateAsync(data);
        var key = "synthetic-photo-boundary";
        var reference = """{"id":"1","version":"1","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""";
        var routes = new (string Route, string Body)[] {
            (Staff(sku.Id.Value, "/primary-photo/query"), ReadBody),
            (Staff(sku.Id.Value, "/primary-photo/original/query"), ReadBody),
            (Staff(sku.Id.Value, "/photo-evidence/operations/query"), """{"purpose":"catalog-photo","key":"synthetic-photo-boundary"}"""),
            (Staff(sku.Id.Value, "/photo-evidence/1/query"), ReadBody),
            (Staff(sku.Id.Value, "/photo-evidence/1/recover"), """{"purpose":"catalog-photo","reason":"Synthetic boundary review"}"""),
            (Staff(sku.Id.Value, "/photo-evidence/1/original/query"), "{\"purpose\":\"catalog-photo-association\",\"reference\":" + reference + "}"),
            ("/api/v1/catalog/primary-photos/operations/query", """{"purpose":"catalog-photo","key":"synthetic-photo-boundary"}""")
        };
        foreach (var (route, json) in routes) {
            foreach (var change in new[] { "purpose", "owner", "missing" }) {
                var body = JsonNode.Parse(json)!.AsObject();
                if (change == "purpose") body["purpose"] = "payment-evidence";
                else if (change == "owner") body["ownerScope"] = new JsonObject { ["organizationId"] = "100" };
                else body.Remove("purpose");
                using var request = Request(route, body.ToJsonString(), data.Maker.UserId, token, key);
                await StatusAsync(client, request, HttpStatusCode.BadRequest);
            }
        }
        foreach (var invalid in new[] { "0", "01", "-1", "1.0", "9223372036854775808" }) {
            using (var request = Request("/api/v1/catalog/skus/" + invalid + "/primary-photo/query", ReadBody, data.Maker.UserId, token))
                await StatusAsync(client, request, HttpStatusCode.BadRequest);
            using (var request = Request(Staff(sku.Id.Value, "/photo-evidence/" + invalid + "/query"), ReadBody, data.Maker.UserId, token))
                await StatusAsync(client, request, HttpStatusCode.BadRequest);
        }
        var validAssociation = "{\"expectedPhotoRevision\":\"0\",\"reservationId\":\"1\",\"reference\":" + reference + ",\"reason\":\"Synthetic boundary review\"}";
        foreach (var invalid in new[] { "0", "01", "-1", "1.0", "9223372036854775808" }) {
            foreach (var field in new[] { "reservationId", "reference.id", "reference.version" }) {
                var body = JsonNode.Parse(validAssociation)!.AsObject();
                if (field == "reservationId") body[field] = invalid;
                else body["reference"]![field.Split('.')[1]] = invalid;
                using var request = Request(Staff(sku.Id.Value, "/primary-photo"), body.ToJsonString(), data.Maker.UserId, token, key);
                await StatusAsync(client, request, HttpStatusCode.BadRequest);
            }
        }
        foreach (var field in new[] { "reservationId", "reference.id", "reference.version", "expectedPhotoRevision" }) {
            var body = JsonNode.Parse(validAssociation)!.AsObject();
            if (field.StartsWith("reference.", StringComparison.Ordinal)) body["reference"]![field.Split('.')[1]] = 9007199254740993L;
            else body[field] = 9007199254740993L;
            using var request = Request(Staff(sku.Id.Value, "/primary-photo"), body.ToJsonString(), data.Maker.UserId, token, key);
            await StatusAsync(client, request, HttpStatusCode.BadRequest);
        }
        // Photo revision zero is the valid initial head; positive resource IDs never permit zero.
        foreach (var invalid in new[] { "00", "01", "-1", "1.0", "256", "9223372036854775808" }) {
            var body = JsonNode.Parse(validAssociation)!.AsObject(); body["expectedPhotoRevision"] = invalid;
            using var request = Request(Staff(sku.Id.Value, "/primary-photo"), body.ToJsonString(), data.Maker.UserId, token, key);
            await StatusAsync(client, request, HttpStatusCode.BadRequest);
        }
        foreach (var (route, json) in new[] {
            (Staff(sku.Id.Value, "/primary-photo"), validAssociation),
            (Staff(sku.Id.Value, "/photo-evidence"), """{"reason":"Synthetic boundary review"}""")
        }) {
            var body = JsonNode.Parse(json)!.AsObject(); body["privatePath"] = "untrusted/private/image.png";
            using var request = Request(route, body.ToJsonString(), data.Maker.UserId, token, key);
            await StatusAsync(client, request, HttpStatusCode.BadRequest);
        }
        Assert.Equal(before, await BusinessStateAsync(data));
    }

    [Fact]
    public async Task Original_routes_reject_ranges_encoded_uploads_and_oversize_bodies_without_evidence_or_money_changes()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync();
        var publicId = (await data.RowAsync("skus", sku.Id.Value))["publicId"].AsString;
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        using var factory = Factory(data, data.Maker, true, keys);
        using var client = Client(factory);
        var token = await TokenAsync(client);
        var before = await BusinessStateAsync(data);
        foreach (var (route, body) in new[] {
            (Staff(sku.Id.Value, "/primary-photo/original/query"), ReadBody),
            (Staff(sku.Id.Value, "/photo-evidence/1/original/query"), """{"purpose":"catalog-photo-association","reference":{"id":"1","version":"1","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}"""),
            ("/api/v1/catalog/variants/" + publicId + "/primary-photo/query", ReadBody)
        }) {
            using var request = Request(route, body, data.Maker.UserId, token);
            request.Headers.Range = new RangeHeaderValue(0, 15);
            await StatusAsync(client, request, HttpStatusCode.BadRequest);
        }
        var upload = Staff(sku.Id.Value, "/photo-evidence/1/content");
        foreach (var encoding in new[] { "gzip", "identity" }) {
            using var request = Request(upload, "", data.Maker.UserId, token, "synthetic-encoded-photo");
            request.Content = new ByteArrayContent([137, 80, 78, 71]);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentEncoding.Add(encoding);
            await StatusAsync(client, request, HttpStatusCode.UnsupportedMediaType);
        }
        foreach (var mime in new[] { "image/png", "application/json", "multipart/form-data" }) {
            using var request = Request(upload, "{}", data.Maker.UserId, token, "synthetic-wrong-photo-type");
            request.Content!.Headers.ContentType = new MediaTypeHeaderValue(mime);
            await StatusAsync(client, request, HttpStatusCode.UnsupportedMediaType);
        }
        using (var request = Request(upload, "", data.Maker.UserId, token, "synthetic-oversize-photo")) {
            request.Content = new ByteArrayContent(new byte[ImageLimit + 1]);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            await StatusAsync(client, request, HttpStatusCode.RequestEntityTooLarge);
        }
        using (var request = Request(Staff(sku.Id.Value, "/primary-photo/query"), new string(' ', 65_537), data.Maker.UserId, token))
            await StatusAsync(client, request, HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal(before, await BusinessStateAsync(data));
    }

    [Fact]
    public async Task Published_buyer_photo_accepts_only_public_uuid_and_staff_and_buyer_without_a_photo_return_private_no_store()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync(); await data.AssortmentsAsync();
        var published = await data.Service.SetPublicationsAsync(data.Maker,
            new(sku.Id, sku.Version, [new(501)], [new(new(501), 1)]), CatalogAuthoringTestData.Metadata("photo-boundary-publication"));
        var row = await data.RowAsync("skus", sku.Id.Value);
        var publicId = row["publicId"].AsString;
        // The real catalog selector must see this exact current publication before HTTP tests use it.
        Assert.Equal(sku.Id, (await data.BuyerService.ReadPublicAsync(data.Buyer, publicId))!.Sku.Id);
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        using var staffFactory = Factory(data, data.Maker, true, keys);
        using var staff = Client(staffFactory);
        var staffToken = await TokenAsync(staff);
        var before = await BusinessStateAsync(data, excludeFences: true);
        using (var request = Request(Staff(sku.Id.Value, "/primary-photo/query"), ReadBody, data.Maker.UserId, staffToken)) {
            using var response = await staff.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
            var current = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(Id(sku.Id.Value), current.GetProperty("skuId").GetString());
            Assert.Equal(Id(published.Version), current.GetProperty("skuRevision").GetString());
            Assert.Equal("0", current.GetProperty("photoRevision").GetString());
            Assert.Equal(JsonValueKind.Null, current.GetProperty("photo").ValueKind);
        }
        using (var request = Request(Staff(sku.Id.Value, "/primary-photo/original/query"), ReadBody, data.Maker.UserId, staffToken)) {
            using var response = await staff.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); NoStore(response);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }
        using var buyerFactory = Factory(data, data.Buyer, true, keys);
        using var buyer = Client(buyerFactory);
        var buyerToken = await TokenAsync(buyer);
        var route = "/api/v1/catalog/variants/" + publicId + "/primary-photo/query";
        using (var request = Request(route, ReadBody, data.Buyer.UserId, buyerToken)) {
            using var response = await buyer.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); NoStore(response);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }
        foreach (var invalid in new[] { Id(sku.Id.Value), "0", "01", "9223372036854775808", publicId.Replace("-", "", StringComparison.Ordinal), "00000000-0000-1000-8000-000000000000" }) {
            using var request = Request("/api/v1/catalog/variants/" + invalid + "/primary-photo/query", ReadBody, data.Buyer.UserId, buyerToken);
            await StatusAsync(buyer, request, HttpStatusCode.BadRequest);
        }
        foreach (var field in new[] { "reference", "reservationId", "skuId", "ownerScope", "organizationId", "shopId", "privatePath" }) {
            var body = JsonNode.Parse(ReadBody)!.AsObject(); body[field] = "1";
            using var request = Request(route, body.ToJsonString(), data.Buyer.UserId, buyerToken);
            await StatusAsync(buyer, request, HttpStatusCode.BadRequest);
        }
        // A public catalog read grant does not authorize the staff photo/evidence surface.
        using (var request = Request(Staff(sku.Id.Value, "/primary-photo/query"), ReadBody, data.Buyer.UserId, buyerToken))
            await StatusAsync(buyer, request, HttpStatusCode.Forbidden);
        using var foreignFactory = Factory(data, data.Other, true, keys);
        using var foreign = Client(foreignFactory);
        using (var request = Request(Staff(sku.Id.Value, "/primary-photo/original/query"), ReadBody, data.Other.UserId, await TokenAsync(foreign)))
            await StatusAsync(foreign, request, HttpStatusCode.Forbidden);
        Assert.Equal(before, await BusinessStateAsync(data, excludeFences: true));
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
    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
    private static string Staff(long id, string suffix) => "/api/v1/catalog/skus/" + Id(id) + suffix;
    private static HttpRequestMessage Request(string route, string body, long actorId, string token,
        string key = "synthetic-photo-boundary")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Origin", Origin); request.Headers.Add("X-CSRF-Token", token);
        request.Headers.Add("X-Expected-Actor-Id", Id(actorId)); request.Headers.Add("Idempotency-Key", key);
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        return request;
    }
    private static async Task StatusAsync(HttpClient client, HttpRequestMessage request, HttpStatusCode expected)
    {
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
    }
    private static void NoStore(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.Private); Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(TimeSpan.Zero, response.Headers.CacheControl!.MaxAge);
        Assert.Contains(response.Headers.Pragma, value => value.Name == "no-cache");
    }
    private static async Task<BsonDocument> BusinessStateAsync(CatalogAuthoringTestData data, bool excludeFences = false)
    {
        var collections = new[] { "catalogPrimaryPhotos", "catalogPhotoHeads", "catalogPhotoAudits", "catalogPhotoReceipts",
            "catalogCategories", "productFamilies", "skus", "variantDefinitions", "catalogIdentifiers", "catalogIdentifierHistory",
            "catalogAssortments", "documentVersions", "auditEvents", "idempotencyRecords", "outboxEvents",
            "stockBalances", "lots", "movements", "journalEntries", "paymentAllocations", "paymentReceiptBalances", "paymentInvoiceBalances" };
        using var names = await data.Database.ListCollectionNamesAsync();
        var evidence = (await names.ToListAsync()).Where(name => name.StartsWith("evidence", StringComparison.Ordinal));
        var result = new BsonDocument();
        foreach (var name in collections.Concat(evidence).Distinct().Order(StringComparer.Ordinal)) {
            var rows = await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            // Authorized reads may fence their actual catalog sources; all content/revisions/hashes remain exact.
            if (excludeFences) foreach (var row in rows) row.Remove("fence");
            result[name] = new BsonArray(rows);
        }
        return result;
    }
    private sealed class Resolver(ActorContext? actor) : IRequestActorResolver
    {
        public ValueTask<ActorContext?> ResolveAsync(HttpContext context, CancellationToken ct) => ValueTask.FromResult(actor);
    }
}
