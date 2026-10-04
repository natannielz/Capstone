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
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Reporting;

// Synthetic authentication selection; actual HTTP anti-forgery, authorization, posting and Mongo owners.
[Collection("Mongo")]
public sealed class AccountingReportApiTests(MongoFixture fixture)
{
    private const string Origin = "https://portal.unit-toko.co.id", Root = "/api/v1/accounting/reports";
    private const string BooksBody = """{"purpose":"accounting-reporting"}""";

    [Fact]
    public async Task Http_reads_only_selected_book_exports_bound_snapshot_after_restart_and_denies_foreign_authority()
    {
        await using var data = await AccountingReportTestData.CreateAsync(fixture);
        var posted = await data.PostAsync();
        using var keys = new IdentityRuntimeTests.RuntimeKeys();
        using var host = Factory(data, data.Reader, keys); using var client = Client(host); var csrf = await TokenAsync(client);
        var books = await SendAsync(client, "/books/query", BooksBody, data.Reader.UserId, csrf);
        Assert.Single(books.GetProperty("items").EnumerateArray());
        var snapshot = await SendAsync(client, "/query", Body(AccountingReportTestData.Key, data.EndDate), data.Reader.UserId, csrf);
        Assert.Equal("100.00", snapshot.GetProperty("debit").GetString());
        Assert.Equal("100.00", snapshot.GetProperty("credit").GetString());
        Assert.True(snapshot.GetProperty("cumulative").GetBoolean()); Assert.False(snapshot.GetProperty("isApprovedClose").GetBoolean());
        Assert.Equal("Asia/Jakarta", snapshot.GetProperty("timeZone").GetString());
        Assert.Single(snapshot.GetProperty("journals").EnumerateArray());
        var originalReadId = snapshot.GetProperty("readId").GetString()!;
        var originalHash = snapshot.GetProperty("snapshotHash").GetString()!;
        var export = JsonSerializer.Serialize(new { purpose = "accounting-reporting", readId = originalReadId, snapshotHash = originalHash });
        // A later backdated posting must not silently join the original export.
        var later = await data.PostAsync(43, amount: 37m); var financial = await data.FinancialStateAsync();
        using var restarted = Factory(data, data.Reader, keys); using var newClient = Client(restarted); var newCsrf = await TokenAsync(newClient);
        using var request = Request("/export", export, data.Reader.UserId, newCsrf); using var response = await newClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(originalReadId, Assert.Single(response.Headers.GetValues("X-Snapshot-Read-Id")));
        Assert.Equal(originalHash, Assert.Single(response.Headers.GetValues("X-Snapshot-Hash")));
        var bytes = await response.Content.ReadAsByteArrayAsync(); Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, bytes[..3]);
        var csv = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\r\n\"'" + posted.Id + "\",\"" + data.EndDate + "\",\"fixture\",\"'42\",", csv);
        Assert.DoesNotContain("\r\n\"'" + later.Id + "\",\"" + data.EndDate + "\",\"fixture\",\"'43\",", csv);
        var fresh = await SendAsync(newClient, "/query", Body(AccountingReportTestData.Key, data.EndDate), data.Reader.UserId, newCsrf);
        Assert.Equal(2, fresh.GetProperty("journals").GetArrayLength()); Assert.Equal("137.00", fresh.GetProperty("debit").GetString());
        using var foreignHost = Factory(data, data.ForeignReader, keys); using var foreignClient = Client(foreignHost); var foreignCsrf = await TokenAsync(foreignClient);
        using var foreignRead = Request("/query", Body(AccountingReportTestData.Key, data.EndDate), data.ForeignReader.UserId, foreignCsrf);
        await StatusAsync(foreignClient, foreignRead, HttpStatusCode.Forbidden);
        using var stolenExport = Request("/export", export, data.ForeignReader.UserId, foreignCsrf);
        await StatusAsync(foreignClient, stolenExport, HttpStatusCode.BadRequest);
        Assert.Equal(financial.ToJson(), (await data.FinancialStateAsync()).ToJson());
        Assert.Equal(1L, await data.Rows("accountingReportReadEvents").CountDocumentsAsync(new BsonDocument("operation", "accounting.report.export")));
    }

    [Fact]
    public async Task Reporting_wire_requires_origin_csrf_actor_explicit_purpose_and_canonical_scoped_input()
    {
        await using var data = await AccountingReportTestData.CreateAsync(fixture);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); using var host = Factory(data, data.Reader, keys);
        using var client = Client(host); var csrf = await TokenAsync(client);
        var access = await client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        Assert.Contains("report.read", access.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()));
        using var warehouseHost = Factory(data, data.WarehouseReader, keys); using var warehouseClient = Client(warehouseHost);
        var warehouseAccess = await warehouseClient.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        Assert.DoesNotContain("report.read", warehouseAccess.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()));
        var routes = new[] { ("/books/query", BooksBody), ("/query", Body(AccountingReportTestData.Key, data.EndDate)),
            ("/export", """{"purpose":"accounting-reporting","readId":"1","snapshotHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""") };
        foreach (var (route, json) in routes) {
            foreach (var missing in new[] { "Origin", "X-CSRF-Token", "X-Expected-Actor-Id" }) {
                using var request = Request(route, json, data.Reader.UserId, csrf); request.Headers.Remove(missing);
                await StatusAsync(client, request, missing == "X-Expected-Actor-Id" ? HttpStatusCode.BadRequest : HttpStatusCode.Forbidden);
            }
            var body = JsonNode.Parse(json)!.AsObject(); body["userId"] = "602";
            using var unknown = Request(route, body.ToJsonString(), data.Reader.UserId, csrf); await StatusAsync(client, unknown, HttpStatusCode.BadRequest);
            using var changed = Request(route, json, data.Reader.UserId + 1, csrf); await StatusAsync(client, changed, HttpStatusCode.Conflict);
            using var wrongPurpose = Request(route, json.Replace("accounting-reporting", "global-report", StringComparison.Ordinal), data.Reader.UserId, csrf);
            await StatusAsync(client, wrongPurpose, HttpStatusCode.BadRequest);
        }
        foreach (var date in new[] { "2026-02-30", "2026-2-03", "2026-09-28T00:00:00Z", "0000-01-01" }) {
            using var invalidDate = Request("/query", Body(AccountingReportTestData.Key, date), data.Reader.UserId, csrf);
            await StatusAsync(client, invalidDate, HttpStatusCode.BadRequest);
        }
        foreach (var value in new[] { "0", "01", "9223372036854775808" }) {
            var body = JsonNode.Parse(Body(AccountingReportTestData.Key, data.EndDate))!.AsObject(); body["key"]!["shopId"] = value;
            using var invalidId = Request("/query", body.ToJsonString(), data.Reader.UserId, csrf); await StatusAsync(client, invalidId, HttpStatusCode.BadRequest);
        }
        using var missingKey = Request("/query", """{"purpose":"accounting-reporting","key":null,"endDate":"2026-09-28"}""", data.Reader.UserId, csrf);
        await StatusAsync(client, missingKey, HttpStatusCode.BadRequest);
        Assert.False(await data.Rows("accountingReportReadEvents").Find(new BsonDocument()).AnyAsync());
    }

    private static string Body(OperationalBookKey key, string date) => JsonSerializer.Serialize(new {
        purpose = "accounting-reporting", key = new { organizationId = key.OrganizationId.ToString(CultureInfo.InvariantCulture),
            shopId = key.ShopId.ToString(CultureInfo.InvariantCulture), channelId = key.ChannelId.ToString(CultureInfo.InvariantCulture), currency = key.Currency }, endDate = date });
    private static WebApplicationFactory<Program> Factory(AccountingReportTestData data, ActorContext actor, IdentityRuntimeTests.RuntimeKeys keys)
    {
        var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        settings["Release:Profile"] = "full";
        return IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.AddAuthentication("AccessTest").AddScheme<AuthenticationSchemeOptions, AccessTestAuthenticationHandler>("AccessTest", _ => { });
            services.PostConfigure<AuthenticationOptions>(options => { options.DefaultAuthenticateScheme = "AccessTest"; options.DefaultChallengeScheme = "AccessTest"; });
            services.RemoveAll<IRequestActorResolver>(); services.AddSingleton<IRequestActorResolver>(new Resolver(actor));
        }));
    }
    private static HttpClient Client(WebApplicationFactory<Program> host) => host.CreateClient(new() { BaseAddress = new(Origin), AllowAutoRedirect = false });
    private static async Task<string> TokenAsync(HttpClient client) => (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
    private static HttpRequestMessage Request(string path, string json, long actor, string csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Root + path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("Origin", Origin); request.Headers.Add("X-CSRF-Token", csrf);
        request.Headers.Add("X-Expected-Actor-Id", actor.ToString(CultureInfo.InvariantCulture)); return request;
    }
    private static async Task<JsonElement> SendAsync(HttpClient client, string path, string json, long actor, string csrf)
    {
        using var request = Request(path, json, actor, csrf); using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }
    private static async Task StatusAsync(HttpClient client, HttpRequestMessage request, HttpStatusCode expected)
    { using var response = await client.SendAsync(request); Assert.Equal(expected, response.StatusCode); }
    private sealed class Resolver(ActorContext actor) : IRequestActorResolver
    { public ValueTask<ActorContext?> ResolveAsync(HttpContext http, CancellationToken ct) => ValueTask.FromResult<ActorContext?>(actor); }
}
