using System.Text.Json;
using System.Text.Json.Nodes;
using UnitToko.Api.Procurement;

namespace UnitToko.Tests.Procurement;

public sealed class ProcurementStockReceiptApiBoundaryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Input = """
        {"location":{"organizationId":"100","shopId":"101","warehouseId":"102","channelId":"103"},
         "orderId":"9007199254740993","orderVersion":"1","orderApprovalId":"2","inspectionId":"3","inspectionLineNumber":1,
         "packages":2,"poolId":"4","poolRevision":"1","receiptReference":"RECEIPT-1","lotReference":"LOT-1","expiresOn":null,"reason":"Record inspected held goods"}
        """;

    [Fact]
    public void Held_receipt_keeps_lossless_ids_and_explicit_unknown_expiry_without_client_stock_or_cost_authority()
    {
        var input = JsonSerializer.Deserialize<ProcurementStockReceiptInputDto>(Input, Json)!;
        Assert.Equal(9007199254740993L, input.OrderId.Value); Assert.Null(input.ExpiresOn);
        foreach (var field in new[] { "orderId", "orderApprovalId", "inspectionId", "poolId", "orderVersion", "poolRevision" }) {
            var invalid = JsonNode.Parse(Input)!.AsObject(); invalid[field] = 9007199254740993L;
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProcurementStockReceiptInputDto>(invalid.ToJsonString(), Json));
        }
        foreach (var field in new[] { "inspectionLineNumber", "packages" }) {
            foreach (JsonNode value in new JsonNode[] { JsonValue.Create("1")!, JsonValue.Create(1.5)!, JsonValue.Create(2147483648L)! }) {
                var invalid = JsonNode.Parse(Input)!.AsObject(); invalid[field] = value.DeepClone();
                Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProcurementStockReceiptInputDto>(invalid.ToJsonString(), Json));
            }
        }
        foreach (var field in JsonNode.Parse(Input)!.AsObject().Select(pair => pair.Key)) {
            var invalid = JsonNode.Parse(Input)!.AsObject(); invalid.Remove(field);
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProcurementStockReceiptInputDto>(invalid.ToJsonString(), Json));
        }
        foreach (var field in new[] { "active", "available", "approved", "cost", "valuation", "lotId", "balanceId", "movementId", "sourceAuthority" }) {
            var invalid = JsonNode.Parse(Input)!.AsObject(); invalid[field] = "1";
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProcurementStockReceiptInputDto>(invalid.ToJsonString(), Json));
        }
    }
}
