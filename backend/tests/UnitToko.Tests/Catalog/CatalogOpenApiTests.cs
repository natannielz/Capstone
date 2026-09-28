using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace UnitToko.Tests.Catalog;

public sealed class CatalogOpenApiTests
{
    [Fact]
    public async Task Generated_catalog_contract_preserves_string_identifier_arrays_and_nullable_detail_only()
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
        foreach (var (schema, property) in new[]
        {
            ("CatalogPublicationSaveDto", "assortmentIds"),
            ("CatalogFamilyCreateDto", "imageAssetIds"),
            ("CatalogFamilyEditDto", "imageAssetIds"),
            ("CatalogDefinitionInputDto", "imageAssetIds")
        })
        {
            var array = schemas.GetProperty(schema).GetProperty("properties").GetProperty(property);
            Assert.Equal("array", array.GetProperty("type").GetString());
            Assert.True(array.TryGetProperty("items", out var item), schema + "." + property + " must specify its identifier item contract");
            if (item.TryGetProperty("$ref", out var reference))
            {
                Assert.Equal("#/components/schemas/LongId", reference.GetString());
                item = schemas.GetProperty("LongId");
            }
            Assert.Equal("string", item.GetProperty("type").GetString());
            Assert.Equal(19, item.GetProperty("maxLength").GetInt32());
            var pattern = item.GetProperty("pattern").GetString()!;
            foreach (var value in new[] { "1", "9007199254740993", "9223372036854775807" })
                Assert.Matches(pattern, value);
            foreach (var value in new[] { "0", "01", "+1", "1e2", "1.0", " 1", "1\n", "9223372036854775808" })
                Assert.False(Regex.IsMatch(value, pattern), "Noncanonical or overflowing ID accepted: " + value);
        }
        var detail = document.RootElement.GetProperty("paths").GetProperty("/api/v1/catalog/variants/{publicId}/query")
            .GetProperty("post").GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Contains(detail.GetProperty("anyOf").EnumerateArray(), item => item.TryGetProperty("type", out var type) && type.GetString() == "null");
        Assert.Equal("object", schemas.GetProperty("CatalogPublishedVariantDto").GetProperty("type").GetString());
        var pageItem = schemas.GetProperty("CatalogPublishedVariantPageDto").GetProperty("properties").GetProperty("items").GetProperty("items");
        Assert.Equal("#/components/schemas/CatalogPublishedVariantDto", pageItem.GetProperty("$ref").GetString());
        Assert.False(pageItem.TryGetProperty("anyOf", out _));
    }
}