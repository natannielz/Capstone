using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Deployment-only capture of retained definitions. No data row is rewritten;
/// the new unique reservation key is installed before the old constraint is removed.</summary>
internal sealed class OrderLineageMigrationCapture(string name, BsonDocument definition, BsonDocument[] indexes, BsonDocument validator)
{
    internal const string PreviousReservationIndex = "ux_order_reservation_balance";
    internal const string ReservationIndex = "ux_order_reservation_line_balance";
    private static InvalidOperationException Invalid() => new("Order line migration requires exact retained validators, indexes and unchanged collections");
    internal static async Task<OrderLineageMigrationCapture> ReadAsync(IMongoDatabase database, string name,
        BsonDocument previous, BsonDocument next, CancellationToken ct) {
        var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
        var indexes = (await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
        if (!AccountContactStorage.SafeDefinition(definition, previous) && !AccountContactStorage.SafeDefinition(definition, next)) throw Invalid();
        RequireIndexes(name, indexes);
        var validator = OrderLineageSchemas.UpgradeRetained(definition["options"]["validator"].AsBsonDocument, previous, next);
        // Even when resuming after DDL, no amended business row may predate applied114.
        if (await database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument("$nor", new BsonArray { previous })).Limit(1).AnyAsync(ct)) throw Invalid();
        return new(name, definition.DeepClone().AsBsonDocument, indexes.Select(row => row.DeepClone().AsBsonDocument).ToArray(), validator);
    }
    internal async Task ApplyAsync(IMongoDatabase database, MigrationLease lease, CancellationToken ct) {
        var rows = database.GetCollection<BsonDocument>(name).WithWriteConcern(WriteConcern.WMajority);
        var before = await AccountContactStorage.DefinitionAsync(database, name, ct);
        var beforeIndexes = await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
        if (before != definition || !SameIndexes(indexes, beforeIndexes)) throw Invalid();
        await lease.RenewAsync(ct);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", validator },
            { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        if (name == "reservations") {
            if (!indexes.Any(row => row["name"] == ReservationIndex)) {
                await lease.RenewAsync(ct);
                await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(OrderLineageSchemas.OrderKey(name),
                    new CreateIndexOptions { Name = ReservationIndex, Unique = true }), cancellationToken: ct);
            }
            if (indexes.Any(row => row["name"] == PreviousReservationIndex)) {
                await lease.RenewAsync(ct);
                await rows.Indexes.DropOneAsync(PreviousReservationIndex, ct);
            }
        }
        var after = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
        var expected = definition.DeepClone().AsBsonDocument; expected["options"]["validator"] = validator;
        var afterIndexes = await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
        if (after != expected) throw Invalid();
        RequireIndexes(name, afterIndexes);
        var retainedBefore = indexes.Where(row => name != "reservations" || row["name"] != PreviousReservationIndex && row["name"] != ReservationIndex).ToArray();
        var retainedAfter = afterIndexes.Where(row => name != "reservations" || row["name"] != ReservationIndex).ToArray();
        if (!SameIndexes(retainedBefore, retainedAfter) || name == "reservations" &&
            (!afterIndexes.Any(row => row["name"] == ReservationIndex) || afterIndexes.Any(row => row["name"] == PreviousReservationIndex))) throw Invalid();
    }
    private static bool SameIndexes(IReadOnlyCollection<BsonDocument> first, IReadOnlyCollection<BsonDocument> second) =>
        first.Count == second.Count && first.All(row => second.Any(other => row == other));
    private static void RequireIndexes(string name, IReadOnlyCollection<BsonDocument> indexes) {
        var keys = name switch {
            "reservations" => new[] { OrderSubmissionSchemas.Key(name), OrderLineageSchemas.OrderKey(name) },
            "orderDecisions" => [OrderSubmissionSchemas.Key(name)],
            "shipmentPreparationVersions" => [ShipmentPreparationSchemas.Key(name)],
            "shipments" => [ShipmentDispatchSchemas.Key(), ShipmentDispatchSchemas.NumberKey()],
            "shipmentReceipts" => [ShipmentReceiptSchemas.Key(name)],
            "invoices" => [InvoiceSchemas.NumberKey(), InvoiceSchemas.CommandKey()],
            "invoiceSourceAllocations" => [InvoiceSchemas.AllocationKey()],
            "orderingStockSourceTransitions" => OrderStockSourceSchemas.Keys(name).Where(key => key.Unique).Select(key => key.Key).ToArray(),
            _ => throw Invalid()
        };
        bool Match(BsonDocument row, BsonDocument key) => row["key"] == key && row.GetValue("unique", false).AsBoolean && !row.Contains("partialFilterExpression");
        bool Foundation(BsonDocument row) => name == "invoices" && (InvoiceStorage.Prototype(row) || InvoiceStorage.FoundationNumber(row)) ||
            name == "shipments" && ShipmentDispatchMigration.FoundationNumberIndex(row);
        if (!indexes.Any(row => row["name"] == "_id_" && row["key"] == new BsonDocument("_id", 1)) ||
            indexes.Any(row => InvoiceStorage.Unsafe(row) || row.Contains("partialFilterExpression") && !Foundation(row) ||
                row.GetValue("unique", false).AsBoolean && row["name"] != "_id_" && !keys.Any(key => Match(row, key)) && !Foundation(row))) throw Invalid();
        if (name == "reservations") {
            if (!indexes.Any(row => row["name"] == PreviousReservationIndex && Match(row, keys[0]) || row["name"] == ReservationIndex && Match(row, keys[1])) ||
                indexes.Any(row => row["name"] == PreviousReservationIndex && !Match(row, keys[0]) || row["name"] == ReservationIndex && !Match(row, keys[1]) ||
                    row.GetValue("unique", false).AsBoolean && row["name"] != "_id_" && row["name"] != PreviousReservationIndex && row["name"] != ReservationIndex)) throw Invalid();
        } else if (keys.Any(key => !indexes.Any(row => Match(row, key)))) throw Invalid();
    }
}
