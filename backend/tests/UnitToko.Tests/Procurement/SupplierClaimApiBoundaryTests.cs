using System.Text.Json;
using System.Text.Json.Nodes;
using UnitToko.Api.Procurement;

namespace UnitToko.Tests.Procurement;

public sealed class SupplierClaimApiBoundaryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Line = """
        {"basis":"physical_received","inspectionId":"9007199254740993","inspectionLineNumber":1,"packages":2,"issue":"damaged",
         "invoiceDocumentId":null,"invoiceDocumentLineNumber":null,"invoiceMatchId":null,"invoiceMatchLineNumber":null}
        """;

    [Fact]
    public void Physical_claim_has_lossless_source_identity_and_explicit_unknown_invoice_basis()
    {
        var line = JsonSerializer.Deserialize<SupplierClaimLineInputDto>(Line, Json)!;
        Assert.Equal(9007199254740993L, line.InspectionId.Value);
        Assert.Null(line.InvoiceDocumentId); Assert.Null(line.InvoiceMatchId);
        foreach (var field in new[] { "inspectionLineNumber", "packages", "invoiceDocumentLineNumber", "invoiceMatchLineNumber" }) {
            var input = JsonNode.Parse(Line)!.AsObject(); input[field] = "1";
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SupplierClaimLineInputDto>(input.ToJsonString(), Json));
        }
        foreach (var field in new[] { "inspectionId", "invoiceDocumentId", "invoiceMatchId" }) {
            var input = JsonNode.Parse(Line)!.AsObject(); input[field] = 9007199254740993L;
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SupplierClaimLineInputDto>(input.ToJsonString(), Json));
        }
        foreach (var field in new[] { "basis", "packages", "invoiceDocumentId", "invoiceMatchId" }) {
            var input = JsonNode.Parse(Line)!.AsObject(); input.Remove(field);
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SupplierClaimLineInputDto>(input.ToJsonString(), Json));
        }
        foreach (var field in new[] { "paid", "approved", "sellable", "sourceInvoiceAmount" }) {
            var input = JsonNode.Parse(Line)!.AsObject(); input[field] = "1";
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SupplierClaimLineInputDto>(input.ToJsonString(), Json));
        }
    }
}
