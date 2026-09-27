using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform;

namespace UnitToko.Tests;

[Collection("Mongo")]
public sealed class FoundationTests(MongoFixture mongo)
{
    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(web => web.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mongo:Uri"] = "mongodb://127.0.0.1:27028/?replicaSet=rs0",
                ["Mongo:Database"] = mongo.DatabaseName
            })));

    [Fact]
    public async Task Health_is_public_but_system_endpoint_fails_closed()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        var denied = await client.GetAsync("/api/v1/system/status");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var body = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Assert.Equal("unauthenticated", body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("traceId").GetString()));
        Assert.Equal("application/problem+json", denied.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Test_host_can_inject_authenticated_actor_and_fixed_clock()
    {
        var fixedTime = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        using var factory = Factory().WithWebHostBuilder(web => web.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(new FixedClock(fixedTime));
            services.AddAuthentication("TestActor")
                .AddScheme<AuthenticationSchemeOptions, TestActorHandler>("TestActor", _ => { });
        }));
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/system/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(fixedTime, body.RootElement.GetProperty("utcNow").GetDateTimeOffset());
    }

    [Fact]
    public async Task Known_domain_validation_maps_to_safe_400_problem()
    {
        using var factory = Factory().WithWebHostBuilder(web => web.ConfigureTestServices(services =>
        {
            services.AddAuthentication("TestActor")
                .AddScheme<AuthenticationSchemeOptions, TestActorHandler>("TestActor", _ => { });
            services.RemoveAll<ISystemStatusProvider>();
            services.AddSingleton<ISystemStatusProvider>(new ThrowingStatusProvider(() =>
            {
                DomainValues.ParseMoney("1.999");
                throw new InvalidOperationException("unreachable");
            }));
        }));
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/system/status");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_money", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Unexpected_exception_maps_to_redacted_500_problem()
    {
        using var factory = Factory().WithWebHostBuilder(web => web.ConfigureTestServices(services =>
        {
            services.AddAuthentication("TestActor")
                .AddScheme<AuthenticationSchemeOptions, TestActorHandler>("TestActor", _ => { });
            services.RemoveAll<ISystemStatusProvider>();
            services.AddSingleton<ISystemStatusProvider>(new ThrowingStatusProvider(() =>
                throw new InvalidOperationException("private-details-should-not-leak")));
        }));
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/system/status");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        Assert.Equal("internal_error", body.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("private-details-should-not-leak", text);
    }

    [Fact]
    public async Task Generated_openapi_matches_checked_in_contract()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        actual["servers"] = new JsonArray();
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "openapi.json")))!;
        Assert.True(JsonNode.DeepEquals(expected, actual), "OpenAPI drift: run backend/contracts/export-openapi.ps1.");
    }

    [Fact]
    public void Missing_required_configuration_prevents_boot()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            web.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Mongo:Uri"] = null, ["Mongo:Database"] = null })));
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Unavailable_database_is_not_ready_but_liveness_stays_available()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            web.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Mongo:Uri"] = "mongodb://127.0.0.1:27029/?serverSelectionTimeoutMS=300",
                    ["Mongo:Database"] = mongo.DatabaseName
                })));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var unavailable = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("application/problem+json", unavailable.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync());
        Assert.Equal("dependency_unavailable", problem.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[1,,2]")]
    public async Task Invalid_json_returns_safe_problem(string json)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/system/status")
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Correlation-Id", "known-id");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_json", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("known-id", body.RootElement.GetProperty("traceId").GetString());
        Assert.DoesNotContain("Exception", body.RootElement.GetRawText());
    }

    [Fact]
    public async Task Advertised_and_streamed_oversized_bodies_are_rejected()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        foreach (var content in new HttpContent[]
        {
            new StringContent(new string('a', 65_537), Encoding.UTF8, "application/json"),
            new ChunkedContent(65_537)
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/system/status") { Content = content };
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task Unknown_length_body_is_bounded_for_every_method(string method)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/health/live")
        { Content = new ChunkedContent(65_537) };
        request.Version = new Version(2, 0);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Transaction_failure_rolls_back_on_real_replica_set()
    {
        var collection = mongo.Client.GetDatabase(mongo.DatabaseName).GetCollection<BsonDocument>("rollback_test");
        using var session = await mongo.Client.StartSessionAsync();
        session.StartTransaction();
        await collection.InsertOneAsync(session, new BsonDocument("_id", Guid.NewGuid().ToString("N")));
        await session.AbortTransactionAsync();
        Assert.Equal(0, await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public void Domain_numeric_boundaries_are_lossless()
    {
        Assert.Equal(long.MaxValue, DomainValues.ParseId(long.MaxValue.ToString()));
        Assert.Equal(999_999_999_999_999.99m, DomainValues.ParseMoney("999999999999999.99"));
        Assert.Throws<DomainValidationException>(() => DomainValues.ParseMoney("1000000000000000.00"));
        Assert.Throws<DomainValidationException>(() => DomainValues.ParseMoney("1.001"));
        Assert.Throws<DomainValidationException>(() => DomainValues.ParseMoney("1.000"));
        Assert.Throws<DomainValidationException>(() => DomainValues.ParseMoney("0.123456789012345678901234567890"));
        Assert.Throws<DomainValidationException>(() => DomainValues.ParseId("9007199254740992.0"));
        Assert.Throws<DomainValidationException>(() => DomainValues.ParseMoney("-79228162514264337593543950335", signed: true));
        Assert.Throws<DomainValidationException>(() => DomainValues.FormatMoney(1.999m));
        Assert.Throws<DomainValidationException>(() => DomainValues.FormatMoney(DomainValues.MaximumMoney + 0.01m));
    }
}

internal sealed class ChunkedContent(int size) : HttpContent
{
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var chunk = Encoding.UTF8.GetBytes(new string('a', 1024));
        for (var remaining = size; remaining > 0; remaining -= Math.Min(remaining, chunk.Length))
            await stream.WriteAsync(chunk.AsMemory(0, Math.Min(remaining, chunk.Length)));
    }
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
}

internal sealed class FixedClock(DateTimeOffset value) : IClock
{
    public DateTimeOffset UtcNow => value;
}

internal sealed class ThrowingStatusProvider(Func<SystemStatus> getStatus) : ISystemStatusProvider
{
    public SystemStatus GetStatus() => getStatus();
}

internal sealed class TestActorHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestActorHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "123")], "TestActor"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "TestActor")));
    }
}
