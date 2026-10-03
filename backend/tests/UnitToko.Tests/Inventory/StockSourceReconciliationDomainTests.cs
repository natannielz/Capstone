using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Tests.Inventory;

public sealed class StockSourceReconciliationDomainTests
{
    [Fact]
    public void Observation_requires_an_explicit_unique_nonnegative_lossless_declaration_and_canonical_reference()
    {
        var observedAt = new DateTimeOffset(2026, 10, 3, 1, 2, 3, TimeSpan.Zero);
        var observation = new StockSourceReconciliationObservation(observedAt, "SYNTHETIC-COUNT-0001", [new(9007199254740993, 9007199254740993)]);
        var input = new StockSourceReconciliationInput(9007199254740993, 1, 0, 2, 1, new string('a', 64), observation);
        StockSourceReconciliationRules.Validate(input);
        Assert.Equal(9007199254740993, input.Observation.Entries[0].Packages);
        // Completeness is checked against the real pool, so explicit empty and zero observations remain representable.
        StockSourceReconciliationRules.Validate(observation with { Entries = [] });
        StockSourceReconciliationRules.Validate(observation with { Entries = [new(1, 0)] });
        foreach (var invalid in new[] {
            observation with { Entries = default }, observation with { Entries = [new(1, 2), new(1, 3)] },
            observation with { Entries = [new(1, -1)] }, observation with { Entries = [new(0, 0)] },
            observation with { SourceReference = "" }, observation with { SourceReference = "reference\nwith-control" },
            observation with { ObservedAt = observedAt.ToOffset(TimeSpan.FromHours(7)) } })
            Assert.Equal("invalid_stock_source_reconciliation", Assert.Throws<DomainValidationException>(() => StockSourceReconciliationRules.Validate(invalid)).Code);
        foreach (var invalid in new[] { input with { PoolRevision = 0 }, input with { PoolFence = -1 }, input with { PolicyRevision = 0 }, input with { ConfigurationHash = new string('A', 64) } })
            Assert.Throws<DomainValidationException>(() => StockSourceReconciliationRules.Validate(invalid));
    }
}
