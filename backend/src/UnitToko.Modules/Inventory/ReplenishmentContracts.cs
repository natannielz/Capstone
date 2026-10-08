using System.Collections.Immutable;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Inventory;

public sealed record ReplenishmentReviewInput(GrantScope Location, long PoolId, long ExpectedPoolRevision,
    long SkuId, long ExpectedSkuRevision, long DefinitionId, ReplenishmentInputs Inputs, string InputReference);
public sealed record ReplenishmentPoolChoice(long Id, long Revision, string Label, string PhysicalKey, bool Active);
public sealed record ReplenishmentEvaluation(long Id, ReplenishmentReviewInput Input, StockCountDefinitionSnapshot Definition,
    ReplenishmentStock Stock, ReplenishmentAdvice Advice, ImmutableArray<ReplenishmentSupply> Supplies,
    string SourceHash, string Reason, long RecordedBy);

public static class ReplenishmentReviewPolicy
{
    public static void Validate(ReplenishmentReviewInput input)
    {
        if (input is null || input.PoolId <= 0 || input.ExpectedPoolRevision <= 0 || input.SkuId <= 0 ||
            input.ExpectedSkuRevision <= 0 || input.DefinitionId <= 0 || !StockCountPolicy.CanonicalText(input.InputReference, 500))
            throw new DomainValidationException("invalid_replenishment_review", "Choose exact source and packaging, and identify the explicit planning inputs");
        StockCountPolicy.Location(input.Location);
        _ = ReplenishmentPolicy.Evaluate(input.Inputs, new(false, null, null, null), false, [], DateTimeOffset.UnixEpoch);
    }
}
