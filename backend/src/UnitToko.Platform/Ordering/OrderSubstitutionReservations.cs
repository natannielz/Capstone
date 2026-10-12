using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;
using S = UnitToko.Platform.Ordering.OrderSubstitutionBson;

namespace UnitToko.Platform.Ordering;

/// <summary>Command-local release credit produced only after Ordering authenticated the exact
/// old reservations. It changes no stock during review and cannot outlive its actor/transaction.</summary>
internal sealed class OrderSubstitutionReleaseCapture
{
    private readonly IMongoDatabase database;
    private readonly ActorContext actor;
    private readonly IClientSessionHandle tx;
    private readonly IClock clock;
    private readonly long transaction;
    private readonly DateTimeOffset observedAt;
    private readonly BsonArray rows;
    private readonly Func<bool, CancellationToken, Task> fence;
    internal OrderSubstitutionReleaseCapture(IMongoDatabase database, ActorContext actor, IClientSessionHandle tx, IClock clock,
        BsonArray rows, Func<bool, CancellationToken, Task> fence) {
        this.database = database; this.actor = actor; this.tx = tx; this.clock = clock; this.fence = fence;
        new InventoryRepository(database).RequireSession(tx);
        transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; observedAt = clock.UtcNow;
        this.rows = rows.DeepClone().AsBsonArray;
        if (rows.Count is < 1 or > 100 || rows.Select(row => row["allocation"]["balanceId"]).Distinct().Count() != rows.Count) throw S.Unavailable();
    }
    internal BsonArray Rows => rows.DeepClone().AsBsonArray;
    internal void Require(IMongoDatabase target, IClock targetClock, ActorContext caller, IClientSessionHandle session) {
        new InventoryRepository(database).RequireSession(tx);
        if (!ReferenceEquals(database.Client, target.Client) || !database.DatabaseNamespace.Equals(target.DatabaseNamespace) ||
            !ReferenceEquals(targetClock, clock) || caller != actor || !ReferenceEquals(session, tx) || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction || clock.UtcNow < observedAt)
            throw S.Unavailable();
    }
    internal InventoryBalanceDocument BeforeReserve(InventoryBalanceDocument actual) {
        Require(database, clock, actor, tx);
        var row = rows.SingleOrDefault(value => value["allocation"]["balanceId"] == actual.Id);
        if (row is null) return actual;
        var release = B.Allocation(row["allocation"].AsBsonDocument);
        if (actual.PhysicalPoolId != release.Pool.Id || actual.Balance.Key != release.Change.Key || actual.Balance.Revision != release.Change.ExpectedRevision)
            throw S.Unavailable();
        return actual with { Balance = StockBalancePolicy.Release(actual.Balance, release.Change.ExpectedRevision, release.Change.Packages) };
    }
    internal async Task FenceAsync(bool written, CancellationToken ct) {
        Require(database, clock, actor, tx); await fence(written, ct); Require(database, clock, actor, tx);
    }
}

public sealed partial class OrderSubmissionService
{
    private async Task<BsonArray> PrepareSubstitutionReleasesAsync(SubstitutionContext context, long skuId, long lineRevision, int packages, CancellationToken ct) {
        var remaining = (long)packages; var result = new BsonArray();
        foreach (var row in context.Reservations.Where(row => row["allocation"]["skuId"] == skuId && OrderLineBson.Revision(row["allocation"]) == lineRevision && row["remaining"].AsInt64 > 0)) {
            if (remaining == 0) break;
            var original = B.Allocation(row["allocation"].AsBsonDocument);
            var balance = await new InventoryRepository(database).ReadAsync(context.Tx, original.Change.Id, ct);
            var pool = await Rows("inventoryPools").Find(context.Tx, F.Eq("_id", original.Pool.Id)).SingleOrDefaultAsync(ct) ?? throw S.Unavailable();
            InventoryGraph.RequirePool(pool);
            if (balance.PhysicalPoolId != original.Pool.Id || balance.Balance.Key != original.Change.Key ||
                balance.Balance.Reserved < row["remaining"].AsInt64 || InventoryGraph.Location(pool["location"].AsBsonDocument) != original.Pool.Location) throw S.Unavailable();
            var take = Math.Min(remaining, row["remaining"].AsInt64);
            var allocation = original with { Pool = original.Pool with { Revision = pool["revision"].AsInt64 },
                Change = original.Change with { ExpectedRevision = balance.Balance.Revision, Packages = take } };
            result.Add(new BsonDocument { { "reservationId", row["_id"] }, { "allocation", B.Allocation(allocation) },
                { "before", OrderStockSourceRecords.State(row) } }); remaining -= take;
        }
        if (remaining != 0 || result.Count == 0) throw S.Unavailable();
        return result;
    }
    private async Task ApplySubstitutionReservationsAsync(SubstitutionContext context, BsonDocument decision, AuthorizationCommandMetadata metadata, CancellationToken ct) {
        foreach (var value in decision["releases"].AsBsonArray) {
            var release = value.AsBsonDocument;
            var actual = await Rows(B.Reservations).Find(context.Tx, F.Eq("_id", release["reservationId"]) & OrderLineageSchemas.OrderValidator(B.Reservations)).SingleOrDefaultAsync(ct) ?? throw S.Unavailable();
            OrderSubstitutionReservationFacts.RequireRelease(actual, release, false);
            if (actual["orderId"] != decision["orderId"] || actual["suborderId"] != decision["suborderId"] || actual["revision"].AsInt64 == long.MaxValue || actual["fence"].AsInt64 >= long.MaxValue - 1)
                throw S.Unavailable();
            var packages = release["allocation"]["packages"].AsInt64;
            if ((await Rows(B.Reservations).UpdateOneAsync(context.Tx, actual, Builders<BsonDocument>.Update.Inc("remaining", -packages)
                .Inc("released", packages).Inc("revision", 1L).Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw S.Unavailable();
        }
        var allocations = decision["allocations"].AsBsonArray;
        if (allocations.Count == 0 || allocations.Count != decision["reservationIds"].AsBsonArray.Count) throw S.Unavailable();
        for (var index = 0; index < allocations.Count; index++) {
            await Rows(B.Reservations).InsertOneAsync(context.Tx, new BsonDocument { { "_id", decision["reservationIds"][index] }, { "schemaVersion", 1 },
                { "kind", "ordering.reservation" }, { "orderId", decision["orderId"] }, { "suborderId", decision["suborderId"] },
                { "allocation", allocations[index].DeepClone() }, { "remaining", allocations[index]["packages"] }, { "dispatched", 0L }, { "released", 0L },
                { "revision", 1L }, { "fence", 0L } }, cancellationToken: ct);
        }
        var ledger = new InventoryLedger(database, clock, [new OrderSubstitutionSourceReader(database, clock, context.Order, token => FenceSubstitutionAsync(context, token, requireUnchanged: false))]);
        foreach (var (eventKind, facts) in new[] { ("release", decision["releases"].AsBsonArray.Select(value => value["allocation"])),
            ("reserve", allocations.AsEnumerable()) })
            foreach (var poolId in facts.Select(value => value["poolId"].AsInt64).Distinct().Order())
                await ledger.ApplyAsync(context.Tx, context.Actor, new("ordering.substitution", decision["_id"].AsInt64, eventKind, poolId),
                    decision["revision"].AsInt64, new(metadata.Reason, metadata.CorrelationId), ct);
    }
}

internal static class OrderSubstitutionReservationFacts
{
    internal static void RequireRelease(BsonDocument reservation, BsonDocument release, bool applied) {
        var original = B.Allocation(reservation["allocation"].AsBsonDocument); var allocation = B.Allocation(release["allocation"].AsBsonDocument);
        var before = release["before"].AsBsonDocument; var packages = allocation.Change.Packages;
        if (release["reservationId"] != reservation["_id"] || allocation.SourceAuthority != original.SourceAuthority || allocation.Pool.Id != original.Pool.Id ||
            allocation.Pool.Location != original.Pool.Location || allocation.SkuId != original.SkuId || allocation.LineRevision != original.LineRevision || allocation.Change.Id != original.Change.Id ||
            allocation.Change.Key != original.Change.Key || packages <= 0 || packages > before["remaining"].AsInt64 ||
            checked(before["remaining"].AsInt64 + before["released"].AsInt64 + before["dispatched"].AsInt64) != original.Change.Packages ||
            reservation["revision"].AsInt64 != checked(before["revision"].AsInt64 + (applied ? 1 : 0)) || reservation["dispatched"] != before["dispatched"] ||
            reservation["remaining"].AsInt64 != before["remaining"].AsInt64 - (applied ? packages : 0) ||
            reservation["released"].AsInt64 != checked(before["released"].AsInt64 + (applied ? packages : 0))) throw S.Unavailable();
    }
}

internal sealed class OrderSubstitutionSourceReader(IMongoDatabase database, IClock clock, BsonDocument order, Func<CancellationToken, Task> finalAuthority) : IInventorySourceReader
{
    public string Namespace => "ordering.substitution";
    public IMongoDatabase Database => database;
    public async Task<InventorySourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, InventorySource source, long expectedRevision, CancellationToken ct) {
        if (source.Namespace != Namespace || source.EventKind is not ("reserve" or "release")) throw S.Unavailable();
        var f = Builders<BsonDocument>.Filter;
        var row = await database.GetCollection<BsonDocument>(S.Decisions).Find(tx, f.Eq("_id", source.DocumentId) & OrderSubstitutionSchemas.Validator(S.Decisions)).SingleOrDefaultAsync(ct) ?? throw S.Unavailable();
        if (row["recordHash"] != S.Hash(row) || row["decision"] != "Accept" || row["actorId"] != actor.UserId || row["actorGrantVersion"] != actor.GrantVersion ||
            row["revision"] != expectedRevision) throw S.Unavailable();
        var receipt = await database.GetCollection<BsonDocument>("idempotencyRecords").Find(tx, new BsonDocument {
            { "actorId", actor.UserId }, { "operation", S.AcceptOperation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
        if (receipt is null || receipt["bodyHash"] != row["bodyHash"] || receipt["result"] != new BsonDocument() || row["orderId"] != order["_id"]) throw S.Unavailable();
        // Consent receipts carry exact person evidence; re-derive it from the persisted proposal instead of trusting the receipt.
        var proposal = await new OrderSubstitutionRecords(database, clock).ReadAsync(tx, S.Proposals, row["proposalId"].AsInt64, order, ct);
        var evidence = new ApprovalRequest(1, 1, RequestedBy: order["createdBy"].AsInt64, ProposedBy: proposal["actorId"].AsInt64);
        if (row["proposalHash"] != proposal["recordHash"] || PersonApprovalReceipt.Read(receipt, Permissions.SubstitutionAccept) != evidence ||
            !ApprovalPolicy.Evaluate(actor.UserId, Permissions.SubstitutionAccept, evidence).Allowed) throw S.Unavailable();
        var facts = source.EventKind == "reserve" ? row["allocations"].AsBsonArray.Where(value => value["poolId"].AsInt64 == source.Occurrence).Select(value => value.AsBsonDocument).ToArray() :
            row["releases"].AsBsonArray.Where(value => value["allocation"]["poolId"].AsInt64 == source.Occurrence).Select(value => value["allocation"].AsBsonDocument).ToArray();
        if (facts.Length is < 1 or > 100) throw S.Unavailable();
        var allocations = facts.Select(B.Allocation).ToArray();
        if (allocations.Select(value => value.Pool).Distinct().Count() != 1) throw S.Unavailable();
        foreach (var allocation in allocations) {
            var reservations = database.GetCollection<BsonDocument>(B.Reservations);
            var originalFact = B.Allocation(allocation);
            var reservationId = source.EventKind == "release"
                ? row["releases"].AsBsonArray.Single(value => value["allocation"] == originalFact)["reservationId"]
                : row["reservationIds"][row["allocations"].AsBsonArray.IndexOf(originalFact)];
            var actual = await reservations.Find(tx, f.Eq("_id", reservationId) & f.Eq("orderId", row["orderId"]) & f.Eq("allocation.balanceId", allocation.Change.Id) &
                OrderLineageSchemas.OrderValidator(B.Reservations)).SingleOrDefaultAsync(ct) ?? throw S.Unavailable();
            if (actual["suborderId"] != row["suborderId"] || actual["fence"].AsInt64 >= long.MaxValue - 1) throw S.Unavailable();
            if (source.EventKind == "release") {
                var release = row["releases"].AsBsonArray.Single(value => value["reservationId"] == actual["_id"]).AsBsonDocument;
                OrderSubstitutionReservationFacts.RequireRelease(actual, release, true);
            } else if (!row["reservationIds"].AsBsonArray.Contains(actual["_id"]) || actual["allocation"] != B.Allocation(allocation) ||
                actual["revision"] != 1L || actual["remaining"] != allocation.Change.Packages || actual["released"] != 0L || actual["dispatched"] != 0L) throw S.Unavailable();
            if ((await reservations.UpdateOneAsync(tx, actual, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw S.Unavailable();
        }
        await finalAuthority(ct);
        return new(source, expectedRevision, allocations[0].Pool, [.. allocations.Select(value => value.Change with {
            Action = source.EventKind == "reserve" ? InventoryBalanceAction.Reserve : InventoryBalanceAction.Release })]);
    }
}
