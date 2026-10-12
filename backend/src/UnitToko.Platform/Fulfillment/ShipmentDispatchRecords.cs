using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using B = UnitToko.Platform.Fulfillment.ShipmentDispatchBson;

namespace UnitToko.Platform.Fulfillment;

/// <summary>Fulfillment's immutable accepted-dispatch projection for its own reads and Ordering's
/// reservation-history verifier. It does not authorize disclosure or write another owner's data.</summary>
internal sealed class ShipmentDispatchRecords(IMongoDatabase database, IClock? clock = null, ShipmentDispatchValueRecords.HistoricalRead? valueHistory = null,
    ShipmentDispatchService.LiveValueRead? liveRead = null, Action<ShipmentDispatchFiniteSource.AuthenticatedRelease>? releaseAuthenticated = null,
    PackConversionService.FiniteHistoryRead.HeadRead? headRead = null)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal async IAsyncEnumerable<BsonDocument> ReadOrderAsync(IClientSessionHandle tx, long orderId, [EnumeratorCancellation] CancellationToken ct) {
        var query = Rows(B.Collection).Find(tx, F.Eq("orderId", orderId)).Sort(new BsonDocument("orderSequence", 1));
        query.Options.BatchSize = 50;
        using var cursor = await query.ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current) yield return await VerifyAsync(tx, row, ct);
    }
    internal async Task<BsonDocument> ReadAsync(IClientSessionHandle tx, long id, CancellationToken ct) =>
        await VerifyAsync(tx, await Rows(B.Collection).Find(tx, F.Eq("_id", id)).SingleOrDefaultAsync(ct) ?? throw Invalid(), ct);
    private async Task<BsonDocument> VerifyAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        valueHistory?.Require(database, tx);
        liveRead?.Require(database, clock ?? throw Invalid(), tx);
        if (!await Rows(B.Collection).Find(tx, F.Eq("_id", row["_id"]) & OrderLineageSchemas.DispatchValidator()).AnyAsync(ct)) throw Invalid();
        var preparation = await ShipmentPreparationService.ReadVersionAsync(database, tx, row["preparationVersionId"].AsInt64, ct);
        if (preparation["preparationId"] != row["preparationId"] || preparation["revision"] != row["preparationRevision"] ||
            !Equal(preparation["source"], row["source"]) || !Equal(preparation["scope"], row["scope"]) || !Equal(preparation["lines"], row["lines"]) ||
            OrderDraftBson.Time(row["recordedAt"]) < OrderDraftBson.Time(preparation["recordedAt"])) throw Invalid();
        var selected = B.Lots(row["pickedLots"].AsBsonArray);
        if (row["bodyHash"] != AuthorizationStore.CommandHash(B.Body(row["preparationId"].AsInt64, row["preparationRevision"].AsInt64,
                row["courierId"].AsInt64, row["review"].AsString, selected), row["reason"].AsString) ||
            row["review"] != B.Review(preparation, row["courierId"].AsInt64, row["courierGrantVersion"].AsInt64, row["courierProfileVersion"].AsInt64,
                row["courierName"].AsString, row["pickedLots"].AsBsonArray, row["stockEffects"].AsBsonArray, row["admission"].AsBsonDocument)) throw Invalid();
        var effects = row["stockEffects"].AsBsonArray;
        if (effects.Count != selected.Length || effects.Select(effect => effect["reservationId"].AsInt64).Distinct().Count() != selected.Length) throw Invalid();
        var admission = B.Admission(row["admission"].AsBsonDocument);
        if (!Equal(B.Admission(admission), row["admission"]) || admission.Lots.Length != selected.Length ||
            admission.Lots.Select(lot => lot.ReservationId).Distinct().Count() != selected.Length) throw Invalid();
        var totalCost = new Money(0);
        foreach (var chosen in selected) {
            var effect = effects.SingleOrDefault(effect => effect["reservationId"] == chosen.ReservationId.Value) ?? throw Invalid();
            var lot = admission.Lots.SingleOrDefault(lot => lot.ReservationId == chosen.ReservationId.Value) ?? throw Invalid();
            if (effect["allocation"]["packages"] != (long)chosen.Packages || lot.SourceAuthority != OrderSubmissionBson.Reference(effect["allocation"]["authority"])) throw Invalid();
            if (admission.FiniteValue is null) totalCost = Money.Add(totalCost, Money.Multiply(lot.PackageCost, chosen.Packages));
        }
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", row["dispatchedBy"]) & F.Eq("operation", B.Operation) & F.Eq("key", row["requestKey"])).SingleOrDefaultAsync(ct);
        if (audit is null || !Equal(audit, B.Audit(row)) || receipt is null || receipt.GetValue("bodyHash", BsonNull.Value) != row["bodyHash"] ||
            !Equal(receipt.GetValue("result", BsonNull.Value), B.Result(row))) throw Invalid();
        if (row["schemaVersion"] == 2) {
            if (liveRead is null || !await liveRead.VerifyAsync(database, clock ?? throw Invalid(), tx, row, ct, valueHistory))
                await ShipmentDispatchValueRecords.VerifyAsync(database, clock ?? new SystemClock(), tx, row, ct, valueHistory, releaseAuthenticated, headRead);
            var events = await Rows("outboxEvents").Find(tx, F.Eq("sourceKey", "fulfillment.shipment.dispatched:" + row["_id"].AsInt64)).Limit(2).ToListAsync(ct);
            if (events.Count != 1 || events[0]["kind"] != "fulfillment.shipment.dispatched" || !Equal(events[0]["payload"], B.Result(row)) || events[0]["createdAt"] != row["recordedAt"]) throw Invalid();
        }
        liveRead?.Require(database, clock ?? throw Invalid(), tx);
        valueHistory?.Require(database, tx);
        return row;
    }
    private static bool Equal(BsonValue left, BsonValue right) => CatalogSchemas.SemanticEquals(left, right);
    internal static AuthorizationDeniedException Invalid() => new("shipment_unavailable");
}
