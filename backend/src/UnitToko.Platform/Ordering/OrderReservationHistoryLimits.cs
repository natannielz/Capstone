using MongoDB.Bson;
using UnitToko.Modules.Contracts;

namespace UnitToko.Platform.Ordering;

internal static class OrderReservationHistoryLimits
{
    internal const int MaximumActive = 100, MaximumProposals = 100;
    // Every proposal can be accepted once and adds at most one command's100 allocations.
    // The independent decision-revision limit can stop new amendments earlier.
    internal const int MaximumRetained = MaximumActive * (MaximumProposals + 1);
    internal static void Require(IEnumerable<BsonDocument> source) {
        var rows = source.ToArray();
        if (rows.Length is < 1 or > MaximumRetained || rows.Count(row => row["remaining"].AsInt64 > 0) > MaximumActive)
            throw Invalid();
    }
    internal static void RequireReplacement(IReadOnlyCollection<BsonDocument> rows, BsonArray releases, int introduced) {
        Require(rows);
        if (introduced is < 1 or > MaximumActive || rows.Count + introduced > MaximumRetained ||
            releases.Count is < 1 or > MaximumActive || releases.Select(row => row["reservationId"]).Distinct().Count() != releases.Count) throw Invalid();
        var retired = 0;
        foreach (var release in releases) {
            var row = rows.SingleOrDefault(row => row["_id"] == release["reservationId"]) ?? throw Invalid();
            var quantity = release["allocation"]["packages"].AsInt64;
            if (quantity <= 0 || quantity > row["remaining"].AsInt64) throw Invalid();
            if (quantity == row["remaining"].AsInt64) retired++;
        }
        if (rows.Count(row => row["remaining"].AsInt64 > 0) - retired + introduced > MaximumActive) throw Invalid();
    }
    private static DomainValidationException Invalid() => new("order_substitution_history_limit", "The active or retained reservation limit was reached");
}
