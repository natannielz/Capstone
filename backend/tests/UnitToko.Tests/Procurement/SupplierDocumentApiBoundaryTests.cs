using System.Text.Json;
using System.Text.Json.Nodes;
using UnitToko.Api.Procurement;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Procurement;

public sealed class SupplierDocumentApiBoundaryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Line = """
        {"sourceLineReference":"001","description":"Observed item","sourceSystem":null,"sourceAccount":null,"sourceCode":null,
         "packages":null,"netPricePerPackage":null,"taxAmount":null,"chargeAmount":null,"relatedInvoiceReference":null,"statementAmount":null}
        """;

    [Fact]
    public void Unknown_facts_are_explicit_null_and_quantity_cannot_be_coerced_from_a_string()
    {
        var unknown = JsonSerializer.Deserialize<SupplierDocumentLineInputDto>(Line, Json)!;
        Assert.Null(unknown.Packages); Assert.Null(unknown.NetPricePerPackage); Assert.Null(unknown.StatementAmount);
        var changed = JsonNode.Parse(Line)!.AsObject(); changed["packages"] = 2;
        Assert.Equal(2, JsonSerializer.Deserialize<SupplierDocumentLineInputDto>(changed.ToJsonString(), Json)!.Packages);
        changed["packages"] = "2";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SupplierDocumentLineInputDto>(changed.ToJsonString(), Json));
        changed.Remove("packages");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SupplierDocumentLineInputDto>(changed.ToJsonString(), Json));
        changed["packages"] = null; changed["statementAmount"] = 12.50;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SupplierDocumentLineInputDto>(changed.ToJsonString(), Json));
        changed["statementAmount"] = null; changed["paid"] = true;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SupplierDocumentLineInputDto>(changed.ToJsonString(), Json));
    }

    [Fact]
    public void Match_line_indices_require_actual_integers_and_lossless_string_source_ids()
    {
        const string source = """
            {"documentLineNumber":1,"packages":2,"inspectionId":"9007199254740993","inspectionLineNumber":1,"invoiceMatchId":null,
             "invoiceMatchLineNumber":null,"netAmount":"24.00","taxAmount":"0.00","chargeAmount":"0.00","statementAmount":null,"discrepancy":null}
            """;
        var checkedLine = JsonSerializer.Deserialize<SupplierDocumentMatchLineInputDto>(source, Json)!;
        Assert.Equal(9007199254740993, checkedLine.InspectionId!.Value.Value);
        Assert.Null(checkedLine.QualityAllocationId);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(source), JsonSerializer.SerializeToNode(checkedLine, Json)));
        var quality = JsonNode.Parse(source)!.AsObject(); quality["qualityAllocationId"] = "9007199254740993";
        var qualityLine = JsonSerializer.Deserialize<SupplierDocumentMatchLineInputDto>(quality.ToJsonString(), Json)!;
        Assert.Equal(9007199254740993, qualityLine.QualityAllocationId!.Value.Value);
        Assert.Equal(9007199254740993, qualityLine.Domain().QualityAllocationId);
        quality["qualityAllocationId"] = 9007199254740993L;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SupplierDocumentMatchLineInputDto>(quality.ToJsonString(), Json));
        foreach (var invalid in new[] { "0", "-1", "1.0" }) {
            quality["qualityAllocationId"] = invalid;
            Assert.Throws<DomainValidationException>(() => JsonSerializer.Deserialize<SupplierDocumentMatchLineInputDto>(quality.ToJsonString(), Json));
        }
        foreach (var name in new[] { "documentLineNumber", "packages", "inspectionLineNumber" }) {
            var row = JsonNode.Parse(source)!.AsObject(); row[name] = "1";
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SupplierDocumentMatchLineInputDto>(row.ToJsonString(), Json));
        }
        var numericId = JsonNode.Parse(source)!.AsObject(); numericId["inspectionId"] = 9007199254740993;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SupplierDocumentMatchLineInputDto>(numericId.ToJsonString(), Json));
    }
}
