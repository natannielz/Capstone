using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;

namespace UnitToko.Platform.Ordering;

public sealed partial class OrderSubmissionService
{
    private async Task<BsonArray> PrepareReleasesAsync(IClientSessionHandle tx, BsonDocument order, long? suborderId, CancellationToken ct) {
        var filter = F.Eq("orderId", order["_id"]) & F.Gt("remaining", 0L);
        if (suborderId.HasValue) filter &= F.Eq("suborderId", suborderId.Value);
        var rows = await Rows(B.Reservations).Find(tx, filter).Sort(new BsonDocument("_id", 1)).Limit(101).ToListAsync(ct);
        if (rows.Count is < 1 or > 100) throw new PersistenceConflictException("order_remainder_resolved", "No outstanding reservation remains to release");
        var result = new BsonArray();
        foreach (var row in rows) {
            var original = B.Allocation(row["allocation"].AsBsonDocument);
            var balanceRow = await Rows("stockBalances").Find(tx, F.Eq("_id", original.Change.Id)).SingleOrDefaultAsync(ct) ?? throw Invalid();
            var balance = InventoryBson.ReadBalance(balanceRow);
            var pool = await Rows("inventoryPools").Find(tx, F.Eq("_id", original.Pool.Id)).SingleOrDefaultAsync(ct) ?? throw Invalid();
            InventoryGraph.RequirePool(pool);
            if (balance.PhysicalPoolId != original.Pool.Id || balance.Balance.Key != original.Change.Key || InventoryGraph.Location(pool["location"].AsBsonDocument) != original.Pool.Location) throw Invalid();
            var allocation = original with { Pool = original.Pool with { Revision = pool["revision"].AsInt64 },
                Change = original.Change with { ExpectedRevision = balance.Balance.Revision, Packages = row["remaining"].AsInt64 } };
            result.Add(new BsonDocument { { "reservationId", row["_id"] }, { "allocation", B.Allocation(allocation) },
                { "before", new BsonDocument(new[] { "revision", "remaining", "released", "dispatched" }.Select(field => new BsonElement(field, row[field]))) } });
        }
        return result;
    }
    private async Task ReleaseOrderStockAsync(IClientSessionHandle tx, ActorContext actor, BsonDocument decision, AuthorizationCommandMetadata metadata,
        CancellationToken ct, Func<CancellationToken, Task> finalAuthority) {
        var releases = decision["releases"].AsBsonArray;
        if (releases.Count == 0) return;
        foreach (var value in releases) {
            var release = value.AsBsonDocument; var before = release["before"].AsBsonDocument;
            var row = await Rows(B.Reservations).Find(tx, F.Eq("_id", release["reservationId"]) & F.Eq("orderId", decision["orderId"]) & OrderSubmissionSchemas.Validator(B.Reservations)).SingleOrDefaultAsync(ct) ?? throw Invalid();
            OrderReleaseFacts.Require(row, release, false);
            if (row["revision"].AsInt64 == long.MaxValue || row["fence"].AsInt64 >= long.MaxValue - 1) throw Invalid();
            if ((await Rows(B.Reservations).UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Set("remaining", 0L)
                .Set("released", checked(before["released"].AsInt64 + before["remaining"].AsInt64)).Inc("revision", 1L).Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw Invalid();
        }
        var ledger = new InventoryLedger(database, clock, [new OrderReleaseSourceReader(database, finalAuthority)]);
        foreach (var pool in releases.Select(value => value["allocation"]["poolId"].AsInt64).Distinct().Order())
            await ledger.ApplyAsync(tx, actor, new("ordering.decision", decision["_id"].AsInt64, "release", pool), decision["revision"].AsInt64,
                new(metadata.Reason, metadata.CorrelationId), ct);
    }
    private async Task VerifyReleaseHistoryAsync(IClientSessionHandle tx, BsonDocument decision, Dictionary<long, BsonDocument> expected,
        OrderReviewAction action, long? suborderId, CancellationToken ct) {
        var releases = decision["releases"].AsBsonArray.Select(value => value.AsBsonDocument).ToArray();
        var affected = action == OrderReviewAction.Approve ? [] : expected.Values.Where(row => row["remaining"].AsInt64 > 0 &&
            (!suborderId.HasValue || row["suborderId"] == suborderId.Value)).ToArray();
        if (releases.Length != affected.Length || releases.Select(value => value["reservationId"].AsInt64).Distinct().Count() != releases.Length ||
            action != OrderReviewAction.Approve && releases.Length == 0) throw Invalid();
        foreach (var release in releases) {
            var reservation = affected.SingleOrDefault(row => row["_id"] == release["reservationId"]) ?? throw Invalid();
            OrderReleaseFacts.Require(reservation, release, false);
            reservation["released"] = checked(reservation["released"].AsInt64 + reservation["remaining"].AsInt64);
            reservation["remaining"] = 0L; reservation["revision"] = checked(reservation["revision"].AsInt64 + 1);
        }
        foreach (var pool in releases.GroupBy(row => row["allocation"]["poolId"].AsInt64)) {
            var source = new InventorySource("ordering.decision", decision["_id"].AsInt64, "release", pool.Key);
            var movement = await Rows("movements").Find(tx, F.Eq("sourceEvent", source.EventKey) & InventoryMovementSchemas.Validator()).SingleOrDefaultAsync(ct) ?? throw Invalid();
            if (movement["actorId"] != decision["decidedBy"] || movement["actorGrantVersion"] != decision["actorGrantVersion"] ||
                movement["sourceRevision"] != decision["revision"] || movement["physicalPoolId"] != pool.Key || movement["poolRevision"] != pool.First()["allocation"]["poolRevision"] ||
                movement["source"]["namespace"] != source.Namespace || movement["source"]["documentId"] != source.DocumentId || movement["source"]["eventKind"] != source.EventKind ||
                movement["source"]["occurrence"] != source.Occurrence || movement["legs"].AsBsonArray.Count != pool.Count()) throw Invalid();
            foreach (var release in pool) {
                var allocation = B.Allocation(release["allocation"].AsBsonDocument);
                var leg = movement["legs"].AsBsonArray.SingleOrDefault(value => value["balanceId"].AsInt64 == allocation.Change.Id) ?? throw Invalid();
                var before = InventoryBson.ReadBalance(leg["before"].AsBsonDocument); var after = InventoryBson.ReadBalance(leg["after"].AsBsonDocument);
                if (leg["action"] != "Release" || leg["packages"].AsInt64 != allocation.Change.Packages || before.Id != allocation.Change.Id ||
                    before.PhysicalPoolId != allocation.Pool.Id || before.Balance.Key != allocation.Change.Key || before.Balance.Revision != allocation.Change.ExpectedRevision ||
                    after != before with { Balance = StockBalancePolicy.Release(before.Balance, allocation.Change.ExpectedRevision, allocation.Change.Packages) }) throw Invalid();
            }
        }
    }
}

internal static class OrderReleaseFacts
{
    internal static void Require(BsonDocument reservation, BsonDocument release, bool applied) {
        var original = B.Allocation(reservation["allocation"].AsBsonDocument); var allocation = B.Allocation(release["allocation"].AsBsonDocument); var before = release["before"].AsBsonDocument;
        if (release["reservationId"] != reservation["_id"] || allocation.SourceAuthority != original.SourceAuthority || allocation.Pool.Id != original.Pool.Id ||
            allocation.Pool.Location != original.Pool.Location || allocation.SkuId != original.SkuId || allocation.Change.Id != original.Change.Id ||
            allocation.Change.PhysicalPoolId != original.Change.PhysicalPoolId || allocation.Change.Key != original.Change.Key ||
            allocation.Change.Packages != before["remaining"].AsInt64 || before["remaining"].AsInt64 <= 0 ||
            before["remaining"].AsInt64 + before["released"].AsInt64 + before["dispatched"].AsInt64 != original.Change.Packages ||
            reservation["revision"].AsInt64 != before["revision"].AsInt64 + (applied ? 1 : 0) ||
            reservation["remaining"].AsInt64 != (applied ? 0 : before["remaining"].AsInt64) || reservation["dispatched"] != before["dispatched"] ||
            reservation["released"].AsInt64 != before["released"].AsInt64 + (applied ? before["remaining"].AsInt64 : 0))
            throw new AuthorizationDeniedException("order_unavailable");
    }
}

/// <summary>Trusted decision source reads the immutable release facts and conditionally fences the
/// actual order and changed reservations in their owning transaction. No browser stock legs are accepted.</summary>
internal sealed class OrderReleaseSourceReader(IMongoDatabase database, Func<CancellationToken, Task> finalAuthority) : IInventorySourceReader
{
    public string Namespace => "ordering.decision";
    public IMongoDatabase Database => database;
    public async Task<InventorySourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, InventorySource source, long expectedRevision, CancellationToken ct) {
        var f = Builders<BsonDocument>.Filter; IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
        if (source.Namespace != Namespace || source.EventKind != "release") throw OrderAdmissionPolicy.Invalid();
        var decision = await Rows("orderDecisions").Find(tx, f.Eq("_id", source.DocumentId) & f.Eq("decidedBy", actor.UserId) &
            f.Eq("revision", expectedRevision) & OrderSubmissionSchemas.Validator("orderDecisions")).SingleOrDefaultAsync(ct) ?? throw OrderAdmissionPolicy.Invalid();
        if (decision["action"] != "Reject" && decision["action"] != "CancelRemainder") throw OrderAdmissionPolicy.Invalid();
        var order = await Rows(B.Orders).Find(tx, f.Eq("_id", decision["orderId"]) & OrderSubmissionSchemas.Validator(B.Orders)).SingleOrDefaultAsync(ct) ?? throw OrderAdmissionPolicy.Invalid();
        if (order["snapshotHash"] != decision["orderSnapshot"] || B.SnapshotHash(order) != order["snapshotHash"].AsString || order["fence"].AsInt64 >= long.MaxValue - 1) throw OrderAdmissionPolicy.Invalid();
        var releases = decision["releases"].AsBsonArray.Select(value => value.AsBsonDocument).Where(value => value["allocation"]["poolId"].AsInt64 == source.Occurrence).ToArray();
        if (releases.Length == 0) throw OrderAdmissionPolicy.Invalid();
        foreach (var release in releases) {
            var row = await Rows(B.Reservations).Find(tx, f.Eq("_id", release["reservationId"]) & f.Eq("orderId", order["_id"]) & OrderSubmissionSchemas.Validator(B.Reservations)).SingleOrDefaultAsync(ct) ?? throw OrderAdmissionPolicy.Invalid();
            OrderReleaseFacts.Require(row, release, true);
            if (!decision["suborderId"].IsBsonNull && decision["suborderId"] != row["suborderId"] || row["fence"].AsInt64 >= long.MaxValue - 1 ||
                (await Rows(B.Reservations).UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw OrderAdmissionPolicy.Invalid();
        }
        if ((await Rows(B.Orders).UpdateOneAsync(tx, order, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw OrderAdmissionPolicy.Invalid();
        var facts = releases.Select(value => B.Allocation(value["allocation"].AsBsonDocument)).ToArray();
        if (facts.Select(value => value.Pool).Distinct().Count() != 1) throw OrderAdmissionPolicy.Invalid();
        await finalAuthority(ct);
        return new(source, expectedRevision, facts[0].Pool, [.. facts.Select(value => value.Change with { Action = InventoryBalanceAction.Release })]);
    }
}
