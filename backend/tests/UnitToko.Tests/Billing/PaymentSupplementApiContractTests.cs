using System.Text.Json;
using UnitToko.Api.Billing;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;

namespace UnitToko.Tests.Billing;

// Concrete DTO/mapper tests. Draft only: not compiled or executed before coordinated integration.
public sealed class PaymentSupplementApiContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly EvidenceRef Image = new(9007199254740999, 1, new string('a', 64));
    private static readonly EvidenceRef Pdf = new(9007199254741005, 1, new string('b', 64));
    private static PaymentSupplementView Draft => new(9007199254740995, 2, 9007199254740997, Image,
        9007199254741001, 1, "draft", null, null, null, "Lampiran tambahan dengan foto utama tetap wajib");
    private static PaymentSupplementView Attached => Draft with {State = "attached", SupplementRevision = 2,
        ReservationId = 9007199254741003, PdfEvidence = Pdf, AttachedAt = DateTimeOffset.Parse("2026-10-05T10:00:00Z")};
    private static string CreateJson => "{\"submissionId\":\"9007199254740997\",\"primaryEvidence\":{\"id\":\"9007199254740999\",\"version\":\"1\",\"sha256\":\"" + new string('a', 64) + "\"},\"reason\":\"Lampiran tambahan\"}";

    [Fact]
    public void Distinct_supplement_state_serializes_lossless_owner_reference_and_own_revision_without_rewriting_primary()
    {
        var primaryBefore = JsonSerializer.Serialize(Draft.PrimaryEvidence, Json);
        foreach (var view in new[] {Draft, Attached}) {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(PaymentSupplementDtos.View(view), Json)); var row = json.RootElement;
            Assert.Equal("9007199254740995", row.GetProperty("declarationId").GetString()); Assert.Equal("2", row.GetProperty("declarationRevision").GetString());
            Assert.Equal("9007199254740997", row.GetProperty("submissionId").GetString()); Assert.Equal("9007199254741001", row.GetProperty("supplementId").GetString());
            Assert.Equal(view.SupplementRevision.ToString(), row.GetProperty("supplementRevision").GetString());
            Assert.Equal("9007199254740999", row.GetProperty("primaryEvidence").GetProperty("id").GetString());
            Assert.Equal(view.State, row.GetProperty("state").GetString());
            Assert.Equal(view.State == "draft" ? JsonValueKind.Null : JsonValueKind.String, row.GetProperty("reservationId").ValueKind);
        }
        Assert.Equal(primaryBefore, JsonSerializer.Serialize(Attached.PrimaryEvidence, Json));
    }
    [Theory]
    [InlineData("original")]
    [InlineData("pin")]
    [InlineData("stageToken")]
    [InlineData("policyApproval")]
    [InlineData("supplementRevision")]
    [InlineData("uploaderId")]
    public void Public_create_body_rejects_server_owned_or_unknown_fields(string name)
    {
        var json = CreateJson[..^1] + ",\"" + name + "\":\"forbidden\"}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PaymentSupplementCreateDto>(json, Json));
    }
    [Theory]
    [InlineData("submissionId")]
    [InlineData("primaryEvidence")]
    [InlineData("reason")]
    public void Create_requires_all_explicit_fields(string field)
    {
        var row = System.Text.Json.Nodes.JsonNode.Parse(CreateJson)!.AsObject(); row.Remove(field);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PaymentSupplementCreateDto>(row.ToJsonString(), Json));
    }
    [Theory]
    [InlineData("9007199254740997")]
    [InlineData("\"01\"")]
    [InlineData("\"1e2\"")]
    public void Create_rejects_lossy_numeric_or_noncanonical_IDs(string invalid)
    {
        var json = CreateJson.Replace("\"submissionId\":\"9007199254740997\"", "\"submissionId\":" + invalid);
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<PaymentSupplementCreateDto>(json, Json));
    }
    [Fact]
    public void Null_reference_reserved_ID_invalid_hash_and_primary_reuse_are_rejected()
    {
        Assert.Throws<DomainValidationException>(() => PaymentSupplementDtos.Reference((PaymentEvidenceReferenceDto?)null));
        Assert.Throws<DomainValidationException>(() => PaymentSupplementDtos.Reference(new PaymentEvidenceReferenceDto(new(9007199254740999), "1", "invalid")));
        Assert.Throws<DomainValidationException>(() => PaymentSupplementDtos.View(Draft with {SupplementId = long.MaxValue - 1}));
        Assert.Throws<DomainValidationException>(() => PaymentSupplementDtos.View(Attached with {PdfEvidence = Image}));
    }
    [Fact]
    public void State_revision_attachment_fields_and_reason_must_form_one_valid_snapshot()
    {
        foreach (var row in new[] {Draft with {State = "attached"}, Draft with {SupplementRevision = 2}, Draft with {ReservationId = 41},
            Draft with {PdfEvidence = Pdf}, Draft with {AttachedAt = DateTimeOffset.UtcNow}, Attached with {ReservationId = null},
            Attached with {PdfEvidence = null}, Attached with {AttachedAt = null}, Draft with {Reason = null!}, Draft with {Reason = "unsafe\u0000"},
            Draft with {Reason = "unsafe\u200b"}, Draft with {Reason = "\uD800"}, Draft with {Reason = new string('x', 1001)}})
            Assert.Throws<DomainValidationException>(() => PaymentSupplementDtos.View(row));
    }
    [Fact]
    public void Existing_primary_image_submit_DTO_remains_byte_compatible_and_has_no_media_discriminator()
    {
        var body = new PaymentSubmissionDto(new(9007199254741003), new(new(Image.Id), "1", Image.Sha256), "Foto asli wajib");
        using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(body, Json));
        Assert.Equal(new[] {"reservationId", "evidence", "reason"}, parsed.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("9007199254741003", parsed.RootElement.GetProperty("reservationId").GetString());
        Assert.Equal(Image.Sha256, parsed.RootElement.GetProperty("evidence").GetProperty("sha256").GetString());
    }
}
