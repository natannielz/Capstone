using System.Collections.Immutable;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Inventory;

public enum PackConversionRuleDecision { Approve, Reject }
public enum PackConversionChoiceRole { Source, Destination }
public sealed record PackConversionRuleInput(GrantScope Location, long SourceSkuId, long SourceSkuRevision, long SourceDefinitionId,
    long DestinationSkuId, long DestinationSkuRevision, long DestinationDefinitionId, int SourcePackagesPerStep, int DestinationPackagesPerStep);
public sealed record PackConversionAdmissionInput(long RuleId, long RuleVersion, long PoolId, long PoolRevision,
    long SourceBalanceId, long SourceBalanceRevision, int Steps);
/// <summary>Preparation only. Quantity arithmetic is not an accepted stock writer or a transferable cost layer.</summary>
public sealed record PackConversionAdmission(long RuleId, long RuleVersion, string State, ConversionQuantities Quantities,
    long AvailableSourcePackages, ImmutableArray<string> BlockingReasons, bool PhysicalConversionAvailable = false);
public static class PackConversionRulePolicy
{
    public static PackConversionRuleInput Copy(PackConversionRuleInput input) {
        if (input is null) throw Invalid(); StockCountPolicy.Location(input.Location);
        foreach (var id in new[] { input.SourceSkuId, input.SourceSkuRevision, input.SourceDefinitionId, input.DestinationSkuId,
            input.DestinationSkuRevision, input.DestinationDefinitionId }) if (id is <= 0 or >= long.MaxValue - 1) throw Invalid();
        if (input.SourceSkuId == input.DestinationSkuId || input.SourceDefinitionId == input.DestinationDefinitionId ||
            input.SourcePackagesPerStep is < 1 or > DomainValues.MaximumLineQuantity || input.DestinationPackagesPerStep is < 1 or > DomainValues.MaximumLineQuantity) throw Invalid();
        return input with { Location = input.Location with { } };
    }
    public static PackConversionAdmissionInput Copy(PackConversionAdmissionInput input) {
        if (input is null || new[] { input.RuleId, input.RuleVersion, input.PoolId, input.PoolRevision, input.SourceBalanceId, input.SourceBalanceRevision }
            .Any(value => value is <= 0 or >= long.MaxValue - 1)) throw Invalid();
        _ = DomainValues.ParseQuantity(input.Steps); return input with { };
    }
    public static DomainValidationException Invalid() => new("invalid_pack_conversion_rule", "Choose exact sealed and loose catalogue identities and an explicit conserved direction");
}
