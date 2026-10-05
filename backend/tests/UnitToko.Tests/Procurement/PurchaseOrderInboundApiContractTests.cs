using System.Text.Json;
using System.Text.Json.Nodes;
using UnitToko.Api.Procurement;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Procurement;

// Maintained DTO boundaries; genuine Mongo authorization/custody is checked separately.
public sealed class PurchaseOrderInboundApiContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Input = """
        {"location":{"organizationId":"100","shopId":"101","warehouseId":"102","channelId":"103"},
         "requestId":"9007199254740993","requestVersion":"1","requestDecisionId":"2","supplierId":"3","supplierVersionId":"4","supplierVersion":"1",
         "businessDate":"2026-10-06","currency":"IDR","commercialReference":{"kind":"quote","reference":"SYNTHETIC-QUOTE","issuedDate":"2026-10-06","validUntil":"2026-10-07T00:00:00Z"},
         "externalOrderReference":null,"paymentTerms":{"kind":"credit","description":"Synthetic terms","requiredAdvance":"0.00","balanceTrigger":"due_date","dueDate":"2026-11-05"},
         "lines":[{"requestSkuId":"55","packages":4,"sourceSystem":"supplier","sourceAccountReference":"SYNTHETIC-SUPPLIER","externalItemCode":"SYNTHETIC-ITEM","definitionId":"56","definitionVersion":"1","netPricePerPackage":"1.00","taxAmount":"0.00","supplierChargeAmount":"0.00"}]}
        """;
    private static JsonObject Preview() => new() { ["purpose"] = "purchase-order-review", ["input"] = JsonNode.Parse(Input) };
    private static JsonObject Command() => new() { ["input"] = JsonNode.Parse(Input), ["binding"] = new string('a', 64), ["reason"] = "Synthetic planned receiving" };
    private static JsonNode Split() => JsonNode.Parse("""
        {"allocations":[{"requestSkuId":"55","packages":3,"poolId":"9007199254740995","poolRevision":"1"},
                        {"requestSkuId":"55","packages":1,"poolId":"9007199254740994","poolRevision":"1"}]}
        """)!;

    [Fact]
    public void Omitted_extension_retains_exact_legacy_wire_and_explicit_null_or_empty_is_not_legacy()
    {
        var preview = JsonSerializer.Deserialize<PurchaseOrderPreviewInputDto>(Preview().ToJsonString(), Json)!;
        var command = JsonSerializer.Deserialize<PurchaseOrderProposeDto>(Command().ToJsonString(), Json)!;
        Assert.False(preview.InboundPoolsSupplied); Assert.False(command.InboundPoolsSupplied);
        Assert.Null(preview.Selection(preview.Input.Domain())); Assert.Null(command.Selection(command.Input.Domain()));
        Assert.False(JsonNode.Parse(JsonSerializer.Serialize(preview, Json))!.AsObject().ContainsKey("inboundPools"));
        Assert.False(JsonNode.Parse(JsonSerializer.Serialize(command, Json))!.AsObject().ContainsKey("inboundPools"));
        foreach (var supplied in new JsonNode?[] { null, JsonNode.Parse("{\"allocations\":[]}") }) {
            var read = Preview(); read["inboundPools"] = supplied?.DeepClone();
            var write = Command(); write["inboundPools"] = supplied?.DeepClone();
            var checkedRead = JsonSerializer.Deserialize<PurchaseOrderPreviewInputDto>(read.ToJsonString(), Json)!;
            var checkedWrite = JsonSerializer.Deserialize<PurchaseOrderProposeDto>(write.ToJsonString(), Json)!;
            Assert.True(checkedRead.InboundPoolsSupplied); Assert.True(checkedWrite.InboundPoolsSupplied);
            Assert.Throws<DomainValidationException>(() => checkedRead.Selection(checkedRead.Input.Domain()));
            Assert.Throws<DomainValidationException>(() => checkedWrite.Selection(checkedWrite.Input.Domain()));
        }
    }
    [Fact]
    public void Explicit_split_keeps_lossless_ids_canonical_quantities_and_rejects_authority_or_numeric_substitution()
    {
        var request = Command(); request["inboundPools"] = Split();
        var dto = JsonSerializer.Deserialize<PurchaseOrderProposeDto>(request.ToJsonString(), Json)!;
        var selection = dto.Selection(dto.Input.Domain())!;
        Assert.Equal(new long[] { 9007199254740994L, 9007199254740995L }, selection.Allocations.Select(row => row.PoolId));
        Assert.Equal(new[] { 1, 3 }, selection.Allocations.Select(row => row.Packages));
        foreach (var field in new[] { "requestSkuId", "poolId", "poolRevision" }) {
            var changed = request.DeepClone(); changed["inboundPools"]!["allocations"]![0]![field] = 9007199254740993L;
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PurchaseOrderProposeDto>(changed.ToJsonString(), Json));
        }
        foreach (var field in new[] { "active", "stockReserved", "physicalStockReleased", "source", "approved", "policyId" }) {
            var changed = request.DeepClone(); changed["inboundPools"]![field] = true;
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PurchaseOrderProposeDto>(changed.ToJsonString(), Json));
        }
        foreach (var count in new[] { 0, 2, 5 }) {
            var changed = request.DeepClone(); changed["inboundPools"]!["allocations"]![0]!["packages"] = count;
            var invalid = JsonSerializer.Deserialize<PurchaseOrderProposeDto>(changed.ToJsonString(), Json)!;
            Assert.Throws<DomainValidationException>(() => invalid.Selection(invalid.Input.Domain()));
        }
        var unknown = request.DeepClone(); unknown["inboundPools"]!["allocations"]![0]!["requestSkuId"] = "57";
        var invalidSku = JsonSerializer.Deserialize<PurchaseOrderProposeDto>(unknown.ToJsonString(), Json)!;
        Assert.Throws<DomainValidationException>(() => invalidSku.Selection(invalidSku.Input.Domain()));
    }
    [Fact]
    public void Optional_response_extension_is_omitted_without_altering_original_command_result()
    {
        var result = new PurchaseOrderResultDto(new(7), "1", null, null, null, "proposed");
        Assert.Equal("{\"proposalId\":\"7\",\"proposalVersion\":\"1\",\"decisionId\":null,\"orderId\":null,\"orderVersion\":null,\"status\":\"proposed\"}", JsonSerializer.Serialize(result, Json));
        var input = JsonSerializer.Deserialize<PurchaseOrderInputDto>(Input, Json)!;
        // Serialization exercises absence only; it does not claim these empty
        // arrays are a semantically valid persisted PO/source for business use.
        var source = new ProcurementSourceDto(new(new(100), "ORG", "Synthetic organization", "1", "1"), new(new(101), "SHOP", "Synthetic shop", "1", "1"),
            new(new(102), "WAREHOUSE", "Synthetic warehouse", "1", "1"), new(new(103), "CHANNEL", "Synthetic channel", "1", "1"));
        var value = new PurchaseOrderPreviewDto(input, "ddo", source, new(new(1), "1", new(2)),
            new(new(3), new(4), "1", "SYNTHETIC", "Synthetic supplier"), [], new(new Money(0), new Money(0), new Money(0), new Money(0)), [], new string('a', 64), DateTimeOffset.Parse("2026-10-06T00:00:00Z"));
        Assert.False(JsonNode.Parse(JsonSerializer.Serialize(value, Json))!.AsObject().ContainsKey("inboundPools"));
        Assert.Contains("\"inboundPools\"", JsonSerializer.Serialize(value with { InboundPools = new([new(new(55), 4, new(9), "1")], []) }, Json));
    }
}
