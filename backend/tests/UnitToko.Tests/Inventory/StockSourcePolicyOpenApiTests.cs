using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
namespace UnitToko.Tests.Inventory;

public sealed class StockSourcePolicyOpenApiTests
{
    [Fact]
    public async Task Policy_contract_has_closed_actions_exact_numbers_required_nullable_tenant_and_private_safe_projections()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web => web.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> {
                ["Mongo:Uri"] = "mongodb://127.0.0.1:27028/?replicaSet=rs0", ["Mongo:Database"] = "unittoko_policy_openapi" })));
        using var client = factory.CreateClient(); using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode(); using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal(new[] { "stock.source.policy.propose", "stock.source.policy.approve", "stock.source.policy.reject", "stock.source.policy.withdraw" }, Values("StockSourcePolicyRecoveryDto", "operation"));
        Assert.Equal(new[] { "approve", "reject", "withdraw" }, Values("StockSourcePolicyReviewRequestDto", "action"));
        Assert.Equal(new[] { "approve", "reject", "withdraw" }, Values("StockSourcePolicyReviewDto", "action"));
        Assert.Equal(new[] { "unitToko", "externalSystem" }, Values("StockSourcePolicyInputDto", "writer"));
        Assert.Equal(new[] { "proposed", "approved", "rejected", "withdrawn" }, Values("StockSourcePolicyCommandDto", "status"));
        Assert.Equal(new[] { "not_confirmed", "committed" }, Values("StockSourcePolicyReceiptDto", "state"));
        foreach (var (type, property) in new[] { ("StockSourcePolicyProposeDto", "expectedPolicyVersion"), ("StockSourcePolicyPreviewDto", "poolVersion"),
            ("StockSourcePolicyDetailDto", "suspensionVersion"), ("StockSourcePolicyMappingDto", "aliasVersion"), ("StockSourcePolicyInputDto", "feedIntervalMilliseconds") })
        {
            var field = schemas.GetProperty(type).GetProperty("properties").GetProperty(property);
            Assert.Equal("string", field.GetProperty("type").GetString());
            var pattern = field.GetProperty("pattern").GetString()!;
            Assert.Matches(pattern, "9007199254740993"); Assert.DoesNotMatch(pattern, "01"); Assert.DoesNotMatch(pattern, "1\n");
        }
        var input = schemas.GetProperty("StockSourcePolicyInputDto");
        Assert.Contains("externalTenant", input.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("null", input.GetProperty("properties").GetProperty("externalTenant").GetProperty("type").EnumerateArray().Select(item => item.GetString()));
        Assert.False(input.GetProperty("additionalProperties").GetBoolean());
        var hash = schemas.GetProperty("StockSourcePolicyProposeDto").GetProperty("properties").GetProperty("configurationHash").GetProperty("pattern").GetString()!;
        Assert.Matches(hash, new string('a', 64)); Assert.DoesNotMatch(hash, new string('A', 64)); Assert.DoesNotMatch(hash, new string('a', 64) + "\n");
        var paths = document.RootElement.GetProperty("paths");
        var detailResponse = paths.GetProperty("/api/v1/inventory/sources/pools/{poolId}/policy-proposals/{id}/query").GetProperty("post")
            .GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Contains(detailResponse.GetProperty("anyOf").EnumerateArray(), item => item.TryGetProperty("type", out var type) && type.GetString() == "null");
        foreach (var schema in schemas.EnumerateObject().Where(item => item.Name.StartsWith("StockSourcePolicy", StringComparison.Ordinal)))
            foreach (var hidden in new[] { "proposedBy", "decidedBy", "personApproval", "auditId", "bodyHash", "correlationId", "schemaVersion", "issuer", "subject", "fence" })
                Assert.False(schema.Value.GetProperty("properties").TryGetProperty(hidden, out _));
        string[] Values(string type, string property) => schemas.GetProperty(type).GetProperty("properties").GetProperty(property).GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();
    }
}