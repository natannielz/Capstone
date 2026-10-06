using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace UnitToko.Tests.Inventory;

public sealed class StockCountOpenApiTests
{
    [Fact]
    public async Task Generated_stock_contract_keeps_required_zero_quantity_precise_references_and_protected_reads()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            web.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mongo:Uri"] = "mongodb://127.0.0.1:27028/?replicaSet=rs0",
                ["Mongo:Database"] = "unittoko_openapi_contract"
            })));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var request = schemas.GetProperty("StockCountRecordDto");
        Assert.Contains("packages", request.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("integer", request.GetProperty("properties").GetProperty("packages").GetProperty("type").GetString());
        foreach (var name in new[] { "skuId", "expectedSkuRevision", "expectedDefinitionId" })
        {
            var property = request.GetProperty("properties").GetProperty(name);
            if (property.TryGetProperty("$ref", out _)) property = schemas.GetProperty("LongId");
            Assert.Equal("string", property.GetProperty("type").GetString());
            Assert.Matches(property.GetProperty("pattern").GetString()!, "9007199254740993");
        }
        var paths = document.RootElement.GetProperty("paths");
        var inventory = paths.EnumerateObject().Where(path => path.Name.StartsWith("/api/v1/inventory/stock-counts", StringComparison.Ordinal)).ToArray();
        Assert.Equal(7, inventory.Length);
        foreach (var path in inventory)
        {
            var operation = path.Value.GetProperty("post");
            var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
            foreach (var header in new[] { "X-Expected-Actor-Id", "X-CSRF-Token" })
                Assert.True(Assert.Single(parameters, parameter => parameter.GetProperty("name").GetString() == header).GetProperty("required").GetBoolean());
            Assert.Contains(operation.GetProperty("security").EnumerateArray(), item => item.TryGetProperty("session", out _));
        }
        var detail = paths.GetProperty("/api/v1/inventory/stock-counts/{id}/query").GetProperty("post")
            .GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Contains(detail.GetProperty("anyOf").EnumerateArray(), item => item.TryGetProperty("type", out var type) && type.GetString() == "null");
        Assert.Equal("object", schemas.GetProperty("StockCountObservationDto").GetProperty("type").GetString());
        var selection = schemas.GetProperty("StockCountPoolSelectionDto");
        Assert.Equal(new[] { "configurationHash", "expectedPoolRevision", "poolId" },
            selection.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal));
        foreach (var name in new[] { "poolId", "expectedPoolRevision" })
        {
            var property = selection.GetProperty("properties").GetProperty(name);
            if (property.TryGetProperty("$ref", out _)) property = schemas.GetProperty("LongId");
            Assert.Equal("string", property.GetProperty("type").GetString());
            Assert.Matches(property.GetProperty("pattern").GetString()!, "9007199254740993");
        }
        Assert.DoesNotContain("poolSelection", request.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var observationFields = schemas.GetProperty("StockCountObservationDto").GetProperty("properties");
        foreach (var privateField in new[] { "issuer", "subject", "countedBy", "physicalItemKey", "auditId", "requestKey", "authoringOwner" })
            Assert.False(observationFields.TryGetProperty(privateField, out _));
    }
}
