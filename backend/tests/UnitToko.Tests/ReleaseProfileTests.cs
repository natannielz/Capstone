using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UnitToko.Api;

namespace UnitToko.Tests;

public sealed class ReleaseProfileTests
{
    [Fact]
    public void Default_is_pilot_and_unknown_release_cannot_enable_backlog()
    {
        Assert.True(ReleaseProfile.IsPilot(new ConfigurationBuilder().Build()));
        Assert.False(ReleaseProfile.IsPilot(Settings("full")));
        Assert.Throws<InvalidOperationException>(() => ReleaseProfile.IsPilot(Settings("typo")));
    }

    [Theory]
    [InlineData("/api/v1/catalog/prices/skus/query")]
    [InlineData("/api/v1/ordering/drafts")]
    [InlineData("/api/v1/ordering/orders")]
    [InlineData("/api/v1/inventory/stock-counts/query")]
    [InlineData("/api/v1/accounting/books/current/query")]
    [InlineData("/api/v1/procurement/requests/query")]
    public async Task Pilot_does_not_map_deferred_commands(string path)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Release:Profile"] = "release1", ["Mongo:Uri"] = "mongodb://127.0.0.1:27028/?replicaSet=rs0", ["Mongo:Database"] = "unittoko_release_scope" }))
            .ConfigureTestServices(services => services.AddAuthentication("TestActor")
                .AddScheme<AuthenticationSchemeOptions, TestActorHandler>("TestActor", _ => { })));
        using var client = factory.CreateClient();
        using var response = await client.PostAsync(path, new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText?.TrimEnd('/') == path);
        using var full = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Release:Profile"] = "full", ["Mongo:Uri"] = "mongodb://127.0.0.1:27028/?replicaSet=rs0", ["Mongo:Database"] = "unittoko_release_scope" })));
        using var fullClient = full.CreateClient();
        Assert.Contains(full.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText?.TrimEnd('/') == path);
        using var retained = await fullClient.PostAsync(path, new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, retained.StatusCode);
    }

    private static IConfiguration Settings(string profile) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Release:Profile"] = profile }).Build();
}
