using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace UnitToko.Tests.Inventory;

public sealed class StockSourceRevisionOpenApiTests
{
    [Fact]
    public async Task Revision_contract_closes_operation_choices_and_keeps_private_history_out_of_public_projections()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web => web.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Mongo:Uri"] = "mongodb://127.0.0.1:27028/?replicaSet=rs0", ["Mongo:Database"] = "unittoko_revision_openapi" })));
        using var client = factory.CreateClient(); using var response = await client.GetAsync("/openapi/v1.json"); response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal(["stock.source.revision.propose", "stock.source.revision.approve", "stock.source.revision.reject", "stock.source.revision.withdraw"],
            Values("StockSourceRevisionRecoveryDto", "operation"));
        Assert.Equal(["proposed", "approved", "rejected", "withdrawn"], Values("StockSourceRevisionCommandDto", "status"));
        Assert.Equal(["inactive", "active"], Values("StockSourceCurrentDto", "poolState"));
        Assert.Equal(["not_confirmed", "committed"], Values("StockSourceRevisionReceiptDto", "state"));
        Assert.Equal(["inventory-source-current"], Values("StockSourceCurrentQueryDto", "purpose"));
        Assert.Equal(["inventory-source-revisions"], Values("StockSourceRevisionQueryDto", "purpose"));
        Assert.Equal(["webLedger", "externalLedger"], Values("StockSourceSettingsDto", "mode"));
        foreach (var (type, property) in new[] { ("StockSourceCurrentDto", "poolVersion"), ("StockSourceRevisionDetailDto", "basePoolVersion"),
            ("StockSourceSettingsDto", "maximumAgeMilliseconds"), ("StockSourceSettingsDto", "safetyBufferBaseUnits") })
        {
            var field = schemas.GetProperty(type).GetProperty("properties").GetProperty(property);
            Assert.Equal("string", field.GetProperty("type").GetString());
            Assert.Matches(field.GetProperty("pattern").GetString()!, "9007199254740993");
            Assert.DoesNotMatch(field.GetProperty("pattern").GetString()!, "01");
            Assert.DoesNotMatch(field.GetProperty("pattern").GetString()!, "1\n");
        }
        var detail = document.RootElement.GetProperty("paths").GetProperty("/api/v1/inventory/sources/revisions/{id}/query")
            .GetProperty("post").GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Contains(detail.GetProperty("anyOf").EnumerateArray(), item => item.TryGetProperty("type", out var type) && type.GetString() == "null");
        foreach (var schema in schemas.EnumerateObject().Where(item => item.Name.StartsWith("StockSource", StringComparison.Ordinal)))
            foreach (var name in new[] { "proposedBy", "decidedBy", "personApproval", "auditId", "bodyHash", "issuer", "subject", "record", "fence" })
                Assert.False(schema.Value.GetProperty("properties").TryGetProperty(name, out _));
        string[] Values(string type, string field) => schemas.GetProperty(type).GetProperty("properties").GetProperty(field)
            .GetProperty("enum").EnumerateArray().Select(item => item.GetString()!).ToArray();
    }
}
