using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Tests.Inventory;

public sealed class PackConversionExecutionContractTests
{
    [Fact]
    public void Execution_intent_is_explicit_bounded_and_copies_scope_without_supplying_stock_authority() {
        var input = new PackConversionInput(InventoryQuantityPolicyTests.Location.ToScope(), 500, 1, 600, 3);
        var copied = PackConversionExecutionPolicy.Copy(input);
        Assert.Equal(input, copied); Assert.NotSame(input, copied); Assert.NotSame(input.Location, copied.Location);
        foreach (var invalid in new[] { input with { RuleId = 0 }, input with { RuleVersion = 0 }, input with { SourceBalanceId = 0 },
            input with { RuleId = long.MaxValue }, input with { SourceBalanceId = long.MaxValue - 2 } })
            Assert.Equal("invalid_pack_conversion", Assert.Throws<DomainValidationException>(() => PackConversionExecutionPolicy.Copy(invalid)).Code);
        foreach (var steps in new[] { 0, -1, DomainValues.MaximumLineQuantity + 1 })
            Assert.Throws<DomainValidationException>(() => PackConversionExecutionPolicy.Copy(input with { Steps = steps }));
        var expected = new PackConversionExpectation(7, 2, 0, 1);
        Assert.Equal(expected, PackConversionExecutionPolicy.Copy(expected));
        foreach (var invalid in new[] { expected with { SourceBalanceRevision = 0 }, expected with { PoolRevision = 0 },
            expected with { LotValueRevision = 0 }, expected with { PoolFence = -1 }, expected with { PoolFence = long.MaxValue } })
            Assert.Equal("invalid_pack_conversion", Assert.Throws<DomainValidationException>(() => PackConversionExecutionPolicy.Copy(invalid)).Code);
    }
}
