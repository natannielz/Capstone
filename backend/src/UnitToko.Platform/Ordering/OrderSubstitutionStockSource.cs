using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;
using S = UnitToko.Platform.Ordering.OrderSubstitutionBson;

namespace UnitToko.Platform.Ordering;

public sealed partial class OrderSubmissionService
{
    private static async Task<BsonDocument[]> SubstitutionOriginsAsync(IMongoDatabase database, IClientSessionHandle tx, BsonDocument order,
        IClock? clock, CancellationToken ct, long maximumRevision, long? pendingId = null) {
        var rows = await database.GetCollection<BsonDocument>(S.Decisions).Find(tx, F.Eq("orderId", order["_id"]) & F.Eq("decision", "Accept") & F.Lte("revision", maximumRevision))
            .Sort(new BsonDocument("revision", 1)).Limit(103).ToListAsync(ct);
        if (rows.Count > 102) throw S.Unavailable();
        if (rows.Count == 0) return [];
        var result = new List<BsonDocument>(); var records = new OrderSubstitutionRecords(database, clock ?? throw S.Unavailable());
        foreach (var row in rows) result.Add(await records.ReadAsync(tx, S.Decisions, row["_id"].AsInt64, order, ct, requireReceipt: row["_id"].AsInt64 != pendingId));
        return result.ToArray();
    }
    private static async Task<OrderStockSourceEvent> ReadSubstitutionStockSourceAsync(IMongoDatabase database, IClientSessionHandle tx,
        long id, bool requireReceipt, IClock clock, CancellationToken ct) {
        var row = await database.GetCollection<BsonDocument>(S.Decisions).Find(tx, F.Eq("_id", id) & OrderSubstitutionSchemas.Validator(S.Decisions)).SingleOrDefaultAsync(ct) ?? throw S.Unavailable();
        var order = await database.GetCollection<BsonDocument>(B.Orders).Find(tx, F.Eq("_id", row["orderId"]) & OrderLineageSchemas.OrderValidator(B.Orders)).SingleOrDefaultAsync(ct) ?? throw S.Unavailable();
        if (B.SnapshotHash(order) != order["snapshotHash"].AsString) throw S.Unavailable();
        row = await new OrderSubstitutionRecords(database, clock).ReadAsync(tx, S.Decisions, id, order, ct, requireReceipt);
        var origins = await SubstitutionOriginsAsync(database, tx, order, clock, ct, row["revision"].AsInt64, requireReceipt ? null : id);
        var allocations = order["allocations"].AsBsonArray.Concat(origins.Where(value => value["revision"].AsInt64 <= row["revision"].AsInt64)
            .SelectMany(value => value["allocations"].AsBsonArray)).ToArray();
        var pools = allocations.Select(value => value["poolId"].AsInt64).Distinct().Order().ToArray();
        var effects = new BsonArray();
        if (row["decision"] == "Decline") {
            if (row["releases"].AsBsonArray.Count != 0 || row["allocations"].AsBsonArray.Count != 0 || row["reservationIds"].AsBsonArray.Count != 0) throw S.Unavailable();
        } else {
            foreach (var release in row["releases"].AsBsonArray) {
                var actual = await database.GetCollection<BsonDocument>(B.Reservations).Find(tx, F.Eq("_id", release["reservationId"]) &
                    OrderLineageSchemas.OrderValidator(B.Reservations)).SingleOrDefaultAsync(ct) ?? throw S.Unavailable();
                if (actual["orderId"] != order["_id"] || actual["suborderId"] != row["suborderId"] ||
                    allocations.Count(value => value == actual["allocation"]) != 1) throw S.Unavailable();
                var before = release["before"].DeepClone().AsBsonDocument; var after = before.DeepClone().AsBsonDocument;
                var count = release["allocation"]["packages"].AsInt64;
                if (count <= 0 || count > before["remaining"].AsInt64) throw S.Unavailable();
                after["revision"] = checked(before["revision"].AsInt64 + 1); after["remaining"] = checked(before["remaining"].AsInt64 - count);
                after["released"] = checked(before["released"].AsInt64 + count);
                var expected = actual.DeepClone().AsBsonDocument; foreach (var field in before) expected[field.Name] = field.Value;
                OrderSubstitutionReservationFacts.RequireRelease(expected, release.AsBsonDocument, false);
                if (!requireReceipt && OrderStockSourceRecords.State(actual) != after) throw S.Unavailable();
                effects.Add(new BsonDocument { { "reservationId", actual["_id"] }, { "orderId", order["_id"] }, { "suborderId", row["suborderId"] },
                    { "allocation", actual["allocation"].DeepClone() }, { "before", before }, { "after", after } });
            }
            var ids = row["reservationIds"].AsBsonArray; var introduced = row["allocations"].AsBsonArray;
            if (ids.Count == 0 || ids.Count != introduced.Count || ids.Distinct().Count() != ids.Count) throw S.Unavailable();
            for (var index = 0; index < ids.Count; index++) {
                var actual = await database.GetCollection<BsonDocument>(B.Reservations).Find(tx, F.Eq("_id", ids[index]) & OrderLineageSchemas.OrderValidator(B.Reservations)).SingleOrDefaultAsync(ct) ?? throw S.Unavailable();
                var after = new BsonDocument { { "revision", 1L }, { "remaining", introduced[index]["packages"] }, { "released", 0L }, { "dispatched", 0L } };
                if (actual["orderId"] != order["_id"] || actual["suborderId"] != row["suborderId"] || actual["allocation"] != introduced[index] ||
                    !requireReceipt && OrderStockSourceRecords.State(actual) != after) throw S.Unavailable();
                effects.Add(new BsonDocument { { "reservationId", ids[index] }, { "orderId", order["_id"] }, { "suborderId", row["suborderId"] },
                    { "allocation", introduced[index].DeepClone() }, { "before", BsonNull.Value }, { "after", after } });
            }
        }
        return new("substitution", id, row, pools, S.Audit(row, false), row["recordedAt"], effects);
    }
}
