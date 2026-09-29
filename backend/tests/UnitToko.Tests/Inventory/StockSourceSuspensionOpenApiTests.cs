using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
namespace UnitToko.Tests.Inventory;

public sealed class StockSourceSuspensionOpenApiTests
{
    [Fact]
    public async Task Suspension_contract_preserves_exact_versions_private_fields_and_closed_operation_choices()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web => web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Mongo:Uri"] = "mongodb://127.0.0.1:27028/?replicaSet=rs0", ["Mongo:Database"] = "unittoko_suspension_openapi" })));
        using var client = factory.CreateClient(); using var response = await client.GetAsync("/openapi/v1.json"); response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal(["stock.source.suspend"], Values("StockSourceSuspensionRecoveryDto", "operation"));
        Assert.Equal(["suspended"], Values("StockSourceSuspensionCommandDto", "status"));
        Assert.Equal(["not_confirmed", "committed"], Values("StockSourceSuspensionReceiptDto", "state"));
        Assert.Equal(["inventory-source-suspension-preview"], Values("StockSourceSuspensionPreviewRequestDto", "purpose"));
        foreach (var (type, property) in new[] { ("StockSourceSuspensionRequestDto", "expectedSuspensionVersion"), ("StockSourceSuspensionPreviewDto", "poolVersion"), ("StockSourceSuspensionDetailDto", "suspensionVersion") })
        {
            var field = schemas.GetProperty(type).GetProperty("properties").GetProperty(property);
            Assert.Equal("string", field.GetProperty("type").GetString()); var pattern = field.GetProperty("pattern").GetString()!;
            Assert.Matches(pattern, "9007199254740993"); Assert.DoesNotMatch(pattern, "01"); Assert.DoesNotMatch(pattern, "1\n");
        }
        var hash = schemas.GetProperty("StockSourceSuspensionRequestDto").GetProperty("properties").GetProperty("configurationHash").GetProperty("pattern").GetString()!;
        Assert.Matches(hash, new string('a', 64)); Assert.DoesNotMatch(hash, new string('A', 64)); Assert.DoesNotMatch(hash, new string('a', 64) + "\n");
        foreach (var path in new[] { "/api/v1/inventory/sources/pools/{poolId}/suspension/query", "/api/v1/inventory/sources/pools/{poolId}/suspensions/{id}/query" })
        {
            var schema = doc.RootElement.GetProperty("paths").GetProperty(path).GetProperty("post").GetProperty("responses").GetProperty("200")
                .GetProperty("content").GetProperty("application/json").GetProperty("schema");
            Assert.Contains(schema.GetProperty("anyOf").EnumerateArray(), item => item.TryGetProperty("type", out var type) && type.GetString() == "null");
        }
        foreach (var schema in schemas.EnumerateObject().Where(item => item.Name.StartsWith("StockSourceSuspension", StringComparison.Ordinal)))
            foreach (var hidden in new[] { "suspendedBy", "auditId", "bodyHash", "correlationId", "input", "fence", "issuer", "subject", "handoffReference" })
                Assert.False(schema.Value.GetProperty("properties").TryGetProperty(hidden, out _));
        string[] Values(string type, string property) => schemas.GetProperty(type).GetProperty("properties").GetProperty(property).GetProperty("enum").EnumerateArray().Select(item => item.GetString()!).ToArray();
    }
}