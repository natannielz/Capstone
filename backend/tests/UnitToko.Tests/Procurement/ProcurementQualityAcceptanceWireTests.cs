using System.Text.Json;
using UnitToko.Api.Procurement;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Procurement;

namespace UnitToko.Tests.Procurement;

public sealed class ProcurementQualityAcceptanceWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Json = """{"location":{"organizationId":"100","shopId":"101","warehouseId":"102","channelId":"103"},"receiptId":"9007199254740993","receiptRevision":"1","acceptedPackages":1,"policyId":"7","policyRevision":"1","observedAt":"2026-10-04T01:02:03.000Z","qualityReference":"QC-0001","putawayReference":"HELD-01","segregationReference":"SUBSET-001","acceptedCondition":"sealed_undamaged","lotReference":"000012","expiresOn":"2026-11-01","nonExpiringReason":null,"reason":"Explicit physical observation"}""";

    [Fact]
    public void Exact_input_requires_lossless_ids_strict_count_explicit_expiry_and_no_extra_release_fields()
    {
        var input = JsonSerializer.Deserialize<ProcurementQualityAcceptanceInputDto>(Json, Web)!.Domain();
        Assert.Equal(9007199254740993L, input.ReceiptId); Assert.Equal("000012", input.LotReference);
        Assert.Equal(input, ProcurementQualityAcceptanceBson.Input(ProcurementQualityAcceptanceBson.Input(input)));
        foreach (var bad in new[] { Json.Replace("\"receiptId\":\"9007199254740993\"", "\"receiptId\":9007199254740993"),
            Json.Replace("\"acceptedPackages\":1", "\"acceptedPackages\":\"1\""), Json.Replace("\"acceptedPackages\":1", "\"acceptedPackages\":1.1"),
            Json.Replace("\"nonExpiringReason\":null,", ""), Json.Replace("\"reason\":", "\"released\":true,\"reason\":") })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProcurementQualityAcceptanceInputDto>(bad, Web));
        var dto = JsonSerializer.Deserialize<ProcurementQualityAcceptanceInputDto>(Json, Web)!;
        Assert.Throws<DomainValidationException>(() => (dto with { ExpiresOn = "2026-11-1" }).Domain());
        Assert.Throws<DomainValidationException>(() => (dto with { Location = null! }).Domain());
    }

    [Fact]
    public void Explicit_nonexpiring_reason_and_independent_observation_have_exact_domain_bounds()
    {
        var input = JsonSerializer.Deserialize<ProcurementQualityAcceptanceInputDto>(Json, Web)!.Domain();
        foreach (var wrong in new[] { input with { AcceptedPackages = 0 }, input with { AcceptedPackages = 1000001 },
            input with { ReceiptRevision = 2 }, input with { ObservedAt = input.ObservedAt.ToOffset(TimeSpan.FromHours(7)) }, input with { ObservedAt = input.ObservedAt.AddTicks(1) },
            input with { QualityReference = " padded" }, input with { PutawayReference = "line\nbreak" },
            input with { SegregationReference = new string('x',257) }, input with { AcceptedCondition = "healthy" },
            input with { ExpiresOn = null }, input with { NonExpiringReason = "Not necessary for dated expiry" },
            input with { ExpiresOn = new DateOnly(1999,12,31) } })
            Assert.Throws<DomainValidationException>(() => ProcurementQualityAcceptanceRules.Copy(wrong));
        var nonexpiring = input with { ExpiresOn = null, NonExpiringReason = "Explicit package-specific procedure", AcceptedCondition = "intact_as_defined" };
        Assert.Equal(nonexpiring, ProcurementQualityAcceptanceRules.Copy(nonexpiring));
        var observation = new ProcurementQualityAcceptanceObservation(input.ObservedAt, 1, "INDEPENDENT-0002", input.PutawayReference,
            input.SegregationReference, input.AcceptedCondition, input.LotReference, input.ExpiresOn, null);
        Assert.Equal(observation, ProcurementQualityAcceptanceBson.Observation(ProcurementQualityAcceptanceBson.Observation(observation)));
        var json = JsonSerializer.Serialize(ProcurementQualityAcceptanceDtos.Observation(ProcurementQualityAcceptanceBson.Observation(observation)), Web);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProcurementQualityAcceptanceObservationDto>(json.Replace("\"acceptedPackages\":1", "\"acceptedPackages\":\"1\""), Web));
    }

    [Fact]
    public void Approval_recovery_contains_only_quality_allocation_and_preserves_decimal_ids()
    {
        var at = new DateTimeOffset(2026,10,4,1,2,3,TimeSpan.Zero);
        var result = JsonSerializer.SerializeToElement(new ProcurementQualityAcceptanceRecoveryDto("confirmed",
            new(new(9007199254740993),new(2),new(3),"1","approved_held",at)), Web).GetProperty("result");
        Assert.Equal("9007199254740993", result.GetProperty("proposalId").GetString()); Assert.Equal("3", result.GetProperty("allocationId").GetString());
        Assert.Equal(new[] { "allocationId", "decisionId", "proposalId", "recordedAt", "status", "version" }, result.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
    }
}
