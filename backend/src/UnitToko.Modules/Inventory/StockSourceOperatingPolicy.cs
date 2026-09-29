using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Inventory;

public enum StockMovementWriter { UnitToko, ExternalSystem }

/// <summary>Reviewed procedure definition. It is not proof of executed handoff, reconciliation,
/// feed freshness, external hold support, open obligations or authority to activate a pool.</summary>
public sealed record StockSourceOperatingPolicyInput(StockMovementWriter Writer, string? ExternalTenant,
    long FeedIntervalMilliseconds, string ReconciliationProcedureReference, string OutageProcedureReference);

public sealed record StockSourcePolicyExpectation(long PoolRevision, long SuspensionRevision,
    long PolicyRevision, string ConfigurationHash)
{
    public void Validate()
    {
        if (PoolRevision <= 0 || SuspensionRevision < 0 || PolicyRevision < 0 || PolicyRevision == long.MaxValue ||
            ConfigurationHash is not { Length: 64 } || ConfigurationHash.Any(c => c is not (>= 'a' and <= 'f') and not (>= '0' and <= '9')))
            throw StockSourceOperatingPolicyRules.Invalid();
    }
}

public static class StockSourceOperatingPolicyRules
{
    public static void Validate(StockSourceOperatingPolicyInput input, StockSourceSetupInput source)
    {
        StockSourceSetupPolicy.Validate(source);
        if (input is null || !Enum.IsDefined(input.Writer) ||
            !StockSourceSetupPolicy.Text(input.ReconciliationProcedureReference, 160) ||
            !StockSourceSetupPolicy.Text(input.OutageProcedureReference, 160)) throw Invalid();
        if (source.Mode == StockAuthorityMode.WebLedger)
        {
            if (input.Writer != StockMovementWriter.UnitToko || input.ExternalTenant is not null ||
                input.FeedIntervalMilliseconds != 0) throw Invalid();
        }
        else if (input.Writer != StockMovementWriter.ExternalSystem ||
            !StockSourceSetupPolicy.Text(input.ExternalTenant, 128) || input.FeedIntervalMilliseconds <= 0 ||
            input.FeedIntervalMilliseconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond) throw Invalid();
    }
    internal static DomainValidationException Invalid() =>
        new("invalid_stock_source_operating_policy", "An operating policy requires explicit canonical writer, tenant and procedure facts");
}
