using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;

namespace UnitToko.Platform.Ordering;

internal static class OrderLineBson
{
    internal static long Revision(BsonValue row) {
        var document = row.AsBsonDocument;
        if (!document.TryGetValue("lineRevision", out var revision)) return 0;
        if (!revision.IsInt64 || revision.AsInt64 is < 2 or > 103) throw Invalid();
        return revision.AsInt64;
    }
    internal static OrderLineIdentity Identity(BsonValue row) {
        var document = row.AsBsonDocument;
        return new OrderLineIdentity(new(document.Contains("skuId") ? document["skuId"].AsInt64 : document["definition"]["skuId"].AsInt64), Revision(row)).Validate();
    }
    internal static BsonDocument With(BsonDocument row, long revision) {
        if (revision != 0) {
            if (revision is < 2 or > 103 || row.Contains("lineRevision")) throw Invalid();
            row.Add("lineRevision", revision);
        }
        return row;
    }
    internal static bool Same(BsonValue first, BsonValue second) => Identity(first) == Identity(second);
    private static DomainValidationException Invalid() => new("invalid_order_line_identity", "The accepted order line identity is unavailable");
}
