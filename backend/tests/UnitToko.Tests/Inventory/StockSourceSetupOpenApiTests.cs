using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace UnitToko.Tests.Inventory;

public sealed class StockSourceSetupOpenApiTests
{
    [Fact]
    public async Task Source_contract_preserves_exact_numbers_closed_choices_and_private_security_boundaries()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            web.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mongo:Uri"] = "mongodb://127.0.0.1:27028/?replicaSet=rs0",
                ["Mongo:Database"] = "unittoko_source_openapi"
            })));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var proposal = schemas.GetProperty("StockSourceSetupProposeDto");
        foreach (var name in new[] { "location", "physicalKey", "label", "mode", "control", "maximumAgeMilliseconds",
            "safetyBufferBaseUnits", "discrepancyOwnerReference", "handoffReference", "reason" })
            Assert.Contains(name, proposal.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        foreach (var name in new[] { "maximumAgeMilliseconds", "safetyBufferBaseUnits" })
        {
            var number = proposal.GetProperty("properties").GetProperty(name);
            Assert.Equal("string", number.GetProperty("type").GetString());
            Assert.Matches(number.GetProperty("pattern").GetString()!, "9007199254740993");
            Assert.DoesNotMatch(number.GetProperty("pattern").GetString()!, "01");
            Assert.DoesNotMatch(number.GetProperty("pattern").GetString()!, "-1");
        }
        Assert.Matches(proposal.GetProperty("properties").GetProperty("safetyBufferBaseUnits").GetProperty("pattern").GetString()!, "0");
        Assert.DoesNotMatch(proposal.GetProperty("properties").GetProperty("maximumAgeMilliseconds").GetProperty("pattern").GetString()!, "0");
        Assert.Equal(["webLedger", "externalLedger"], Values("StockSourceSetupProposeDto", "mode"));
        Assert.Equal(["exclusiveWebAllocation", "sharedWebLedger", "externalHold"], Values("StockSourceSetupProposeDto", "control"));
        Assert.Equal(["stock.source.propose", "stock.source.approve", "stock.source.reject", "stock.source.withdraw"],
            Values("StockSourceSetupRecoveryDto", "operation"));
        Assert.Equal(["inventory-source-propose", "inventory-source-review", "inventory-source-history", "stock-source-reconciliation-review"], Values("StockSourceWarehouseQueryDto", "purpose"));
        Assert.Equal(["not_confirmed", "committed"], Values("StockSourceSetupReceiptDto", "state"));
        Assert.Equal(["proposed", "approved", "rejected", "withdrawn"], Values("StockSourceSetupCommandDto", "status"));

        var paths = document.RootElement.GetProperty("paths");
        var sources = paths.EnumerateObject().Where(path => path.Name.StartsWith("/api/v1/inventory/sources", StringComparison.Ordinal)).ToArray();
        Assert.Equal(49, sources.Length); // 31 existing source routes, 10 operating-policy routes and 8 activation routes.
        foreach (var path in sources)
        {
            var operation = path.Value.GetProperty("post");
            var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
            var headers = path.Name.EndsWith("/query", StringComparison.Ordinal)
                ? new[] { "X-Expected-Actor-Id", "X-CSRF-Token" }
                : path.Name == "/api/v1/inventory/sources"
                    ? new[] { "X-Expected-Actor-Id", "X-CSRF-Token", "Idempotency-Key" }
                    : new[] { "X-Expected-Actor-Id", "X-CSRF-Token", "Idempotency-Key", "If-Match" };
            foreach (var header in headers)
                Assert.True(Assert.Single(parameters, parameter => parameter.GetProperty("name").GetString() == header).GetProperty("required").GetBoolean());
            Assert.Contains(operation.GetProperty("security").EnumerateArray(), item => item.TryGetProperty("session", out _));
            foreach (var status in new[] { "400", "401", "403", "409", "503" })
                Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
        }
        var detail = paths.GetProperty("/api/v1/inventory/sources/{id}/query").GetProperty("post")
            .GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Contains(detail.GetProperty("anyOf").EnumerateArray(), item => item.TryGetProperty("type", out var type) && type.GetString() == "null");
        foreach (var schema in schemas.EnumerateObject().Where(item => item.Name.StartsWith("StockSourceSetup", StringComparison.Ordinal)))
            foreach (var name in new[] { "proposedBy", "decidedBy", "personApproval", "auditId", "bodyHash", "issuer", "subject", "active", "record" })
                Assert.False(schema.Value.GetProperty("properties").TryGetProperty(name, out _));
        var id = schemas.GetProperty("LongId");
        Assert.Equal("string", id.GetProperty("type").GetString());
        Assert.Matches(id.GetProperty("pattern").GetString()!, "9007199254740993");

        string[] Values(string schema, string property) =>
            schemas.GetProperty(schema).GetProperty("properties").GetProperty(property).GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()!).ToArray();
    }
}
