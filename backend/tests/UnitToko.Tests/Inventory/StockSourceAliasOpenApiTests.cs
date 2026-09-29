using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace UnitToko.Tests.Inventory;

public sealed class StockSourceAliasOpenApiTests
{
    [Fact]
    public async Task Alias_contract_keeps_exact_versions_closed_choices_and_private_fields_out()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web => web.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Mongo:Uri"] = "mongodb://127.0.0.1:27028/?replicaSet=rs0", ["Mongo:Database"] = "unittoko_alias_openapi" })));
        using var client = factory.CreateClient(); using var response = await client.GetAsync("/openapi/v1.json"); response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal(["stock.source.alias.propose", "stock.source.alias.approve", "stock.source.alias.reject", "stock.source.alias.withdraw"], Values("StockSourceAliasRecoveryDto", "operation"));
        Assert.Equal(["proposed", "approved", "rejected", "withdrawn"], Values("StockSourceAliasCommandDto", "status"));
        Assert.Equal(["absent", "disabled", "stale", "current"], Values("StockSourceAliasCurrentDto", "bindingState"));
        Assert.Equal(["not_confirmed", "committed"], Values("StockSourceAliasReceiptDto", "state"));
        Assert.Equal(["inventory-source-alias"], Values("StockSourceAliasReadDto", "purpose"));
        Assert.Equal(["inventory-source-alias-history"], Values("StockSourceAliasHistoryDto", "purpose"));
        foreach (var (type, property) in new[] { ("StockSourceAliasProposeDto", "expectedAliasVersion"), ("StockSourceAliasDetailDto", "poolVersion"), ("StockSourceAliasDetailDto", "baseAliasVersion") })
        {
            var field = schemas.GetProperty(type).GetProperty("properties").GetProperty(property);
            Assert.Equal("string", field.GetProperty("type").GetString());
            Assert.Matches(field.GetProperty("pattern").GetString()!, "9007199254740993");
            Assert.DoesNotMatch(field.GetProperty("pattern").GetString()!, "01");
            Assert.DoesNotMatch(field.GetProperty("pattern").GetString()!, "1\n");
        }
        Assert.Contains(schemas.GetProperty("StockSourceAliasProposeDto").GetProperty("required").EnumerateArray(), item => item.GetString() == "enabled");
        var detail = document.RootElement.GetProperty("paths").GetProperty("/api/v1/inventory/sources/pools/{poolId}/alias-proposals/{id}/query")
            .GetProperty("post").GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Contains(detail.GetProperty("anyOf").EnumerateArray(), item => item.TryGetProperty("type", out var type) && type.GetString() == "null");
        foreach (var schema in schemas.EnumerateObject().Where(item => item.Name.StartsWith("StockSourceAlias", StringComparison.Ordinal)))
            foreach (var field in new[] { "proposedBy", "decidedBy", "personApproval", "auditId", "bodyHash", "issuer", "subject", "record", "fence" })
                Assert.False(schema.Value.GetProperty("properties").TryGetProperty(field, out _));
        string[] Values(string type, string field) => schemas.GetProperty(type).GetProperty("properties").GetProperty(field).GetProperty("enum").EnumerateArray().Select(item => item.GetString()!).ToArray();
    }
}
