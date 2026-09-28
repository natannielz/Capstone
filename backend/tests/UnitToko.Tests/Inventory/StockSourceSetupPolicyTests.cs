using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Tests.Inventory;

public sealed class StockSourceSetupPolicyTests
{
    private static StockSourceSetupInput Input() => new(new(new(1), new(2), new(3), new(4)), "pool-1", "Pool one",
        StockAuthorityMode.WebLedger, StockPoolControl.ExclusiveWebAllocation, 1, 0, null, null, "owner", "handoff");

    [Theory]
    [InlineData("unconfirmed")] [InlineData("mode")] [InlineData("control")] [InlineData("age-zero")]
    [InlineData("age-overflow")] [InlineData("negative-buffer")] [InlineData("missing-owner")]
    [InlineData("missing-handoff")] [InlineData("invalid-location")] [InlineData("uppercase-key")]
    [InlineData("space-key")] [InlineData("newline-key")] [InlineData("oversized-key")]
    [InlineData("web-external-namespace")] [InlineData("web-protocol")] [InlineData("external-without-namespace")]
    [InlineData("external-without-protocol")] [InlineData("external-web-control")]
    public void Incomplete_or_ambiguous_setups_fail_closed(string fault)
    {
        var input = Input();
        input = fault switch
        {
            "unconfirmed" => input with { Mode = StockAuthorityMode.Unconfirmed },
            "mode" => input with { Mode = (StockAuthorityMode)100 },
            "control" => input with { Control = StockPoolControl.Unconfirmed },
            "age-zero" => input with { MaximumAgeMilliseconds = 0 },
            "age-overflow" => input with { MaximumAgeMilliseconds = long.MaxValue },
            "negative-buffer" => input with { SafetyBufferBaseUnits = -1 },
            "missing-owner" => input with { DiscrepancyOwnerReference = "" },
            "missing-handoff" => input with { HandoffReference = "" },
            "invalid-location" => input with { Location = input.Location with { WarehouseId = default } },
            "uppercase-key" => input with { PhysicalKey = "POOL" },
            "space-key" => input with { PhysicalKey = "pool " },
            "newline-key" => input with { PhysicalKey = "pool\n" },
            "oversized-key" => input with { PhysicalKey = new string('a', 65) },
            "web-external-namespace" => input with { ExternalNamespace = "pos" },
            "web-protocol" => input with { ReservationProtocolReference = "protocol" },
            "external-without-namespace" => input with { Mode = StockAuthorityMode.ExternalLedger, Control = StockPoolControl.ExternalHold, ReservationProtocolReference = "protocol" },
            "external-without-protocol" => input with { Mode = StockAuthorityMode.ExternalLedger, Control = StockPoolControl.ExternalHold, ExternalNamespace = "pos" },
            "external-web-control" => input with { Mode = StockAuthorityMode.ExternalLedger, Control = StockPoolControl.SharedWebLedger, ExternalNamespace = "pos", ReservationProtocolReference = "protocol" },
            _ => throw new ArgumentException()
        };
        Assert.Throws<DomainValidationException>(() => StockSourceSetupPolicy.Validate(input));
    }

    [Theory]
    [InlineData(StockPoolControl.ExclusiveWebAllocation)]
    [InlineData(StockPoolControl.SharedWebLedger)]
    public void Web_setup_preserves_exact_int64_buffer_and_millisecond_age(StockPoolControl control)
    {
        var input = Input() with { PhysicalKey = new string('a', 64), Control = control,
            SafetyBufferBaseUnits = long.MaxValue, MaximumAgeMilliseconds = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond };
        StockSourceSetupPolicy.Validate(input);
    }
}
