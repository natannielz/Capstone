using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Procurement;

namespace UnitToko.Platform.Mongo.Migrations;
/// <summary>Proposed M95 after the separately owned M94; not registered in actual source. No old producer backfill.
/// Retained owners, UUIDs, options, indexes and counters survive; only safe empty new owners qualify.</summary>
internal sealed class AfterSalesStockSourceMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 95;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("AfterSales source migration requires exact safe retained history and empty new owners");
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying" || database.Client is not MongoClient client || clock.UtcNow == default || clock.UtcNow.Offset != TimeSpan.Zero) throw Invalid();
        await AfterSalesStockSourceStorage.RequireRetainedAsync(database, ct, ShipmentDispatchSchemas.LegacyValidator());
        var retained = await (await database.ListCollectionsAsync(cancellationToken: ct)).ToListAsync(ct);
        var indexes = new Dictionary<string, BsonDocument[]>();
        foreach (var definition in retained.Where(row => row.GetValue("type", "") == "collection"))
            indexes.Add(definition["name"].AsString, (await (await Rows(definition["name"].AsString).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray());
        var counters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        foreach (var counter in counters)
            if (counter.ElementCount != 2 || !counter.GetValue("_id", BsonNull.Value).IsString || string.IsNullOrWhiteSpace(counter["_id"].AsString) ||
                !counter.GetValue("value", BsonNull.Value).IsInt64 || counter["value"].AsInt64 is < 0 or >= long.MaxValue - 1) throw Invalid();
        // Decisions/inspection use their genuine case/receipt identity; they have no own allocator.
        // Check actual independent retained allocators without manufacturing or advancing counters.
        foreach (var name in StockSourceSetupSchemas.Collections.Where(name => name != "inventoryPoolRegistrations")
            .Concat([AfterSalesCaseRecords.Collection, ReturnQuarantineLedger.Collection, OrderStockSourceRecords.Transitions,
                ReconciliationStockSourceRecords.Transitions, FulfillmentStockSourceRecords.Transitions, ProcurementStockSourceRecords.Transitions,
                "stockBalances", "lots", "movements", "auditEvents"])) {
            await lease.RenewAsync(ct);
            var maximum = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
            var counter = counters.SingleOrDefault(row => row["_id"] == name);
            if (maximum is not null && (!maximum["_id"].IsInt64 || maximum["_id"].AsInt64 is <= 0 or >= long.MaxValue - 1 ||
                counter is null || counter["value"].AsInt64 < maximum["_id"].AsInt64)) throw Invalid();
        }
        var candidates = new List<(string Name, bool Exists, BsonDocument Validator, (string Name, BsonDocument Key, bool Unique)[] Missing)>();
        foreach (var name in AfterSalesStockSourceSchemas.Collections.Append(ReturnedLotVerificationRecords.Collection)) {
            await lease.RenewAsync(ct);
            var validator = name == ReturnedLotVerificationRecords.Collection ? ReturnedLotVerificationSchemas.Validator() : AfterSalesStockSourceSchemas.Validator(name);
            (string Name, BsonDocument Key, bool Unique)[] required = name == ReturnedLotVerificationRecords.Collection ?
                [("ux_returned_lot_verification_request", ReturnedLotVerificationSchemas.Key(), true)] : AfterSalesStockSourceSchemas.Keys(name);
            var definition = retained.SingleOrDefault(row => row.GetValue("name", "") == name);
            if (definition is not null && !AccountContactStorage.SafeDefinition(definition, validator) || await Rows(name).Find(new BsonDocument()).AnyAsync(ct)) throw Invalid();
            var counter = counters.SingleOrDefault(row => row["_id"] == name); if (counter is not null && counter["value"] != 0L) throw Invalid();
            BsonDocument[] existing = definition is null ? [] : indexes[name];
            if (definition is not null && !OrderStockSourceStorage.SafeIndexes(existing, required)) throw Invalid();
            candidates.Add((name, definition is not null, definition is null ? validator : definition["options"]["validator"].AsBsonDocument,
                required.Where(key => !existing.Any(row => AccountContactStorage.Matches(row, key))).ToArray()));
        }
        foreach (var (name, validator) in new[] {
            (AfterSalesCaseRecords.Collection, AfterSalesCaseSchemas.Validator()), (AfterSalesDecisionRecords.Collection, AfterSalesDecisionSchemas.Validator()),
            (UnitToko.Platform.Inventory.ReturnQuarantineLedger.Collection, ReturnQuarantineSchemas.Validator()),
            (UnitToko.Platform.Inventory.ReturnInspectionRecords.Collection, ReturnInspectionSchemas.Validator()), (ReturnResolutionRecords.Collection, ReturnResolutionSchemas.Validator()) }) {
            await lease.RenewAsync(ct);
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { validator })).AnyAsync(ct) ||
                await Rows(name).CountDocumentsAsync(new BsonDocument(), cancellationToken: ct) > AfterSalesStockSourceRecords.MaximumRows) throw Invalid();
        }
        using (var tx = await client.StartSessionAsync(cancellationToken: ct)) {
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
            var owner = new AfterSalesStockSourceEvents(database, new(database), clock); await owner.OrphansAsync(tx, ct);
            var cases = await Rows(AfterSalesCaseRecords.Collection).Find(tx, new BsonDocument()).ToListAsync(ct);
            foreach (var candidate in cases) {
                await lease.RenewAsync(ct);
                var root = await new AfterSalesCaseRecords(database).ReadAsync(tx, candidate["_id"].AsInt64, ct);
                _ = await new AfterSalesDecisionRecords(database).ReadAsync(tx, root, ct);
            }
            var returns = await Rows(UnitToko.Platform.Inventory.ReturnQuarantineLedger.Collection).Find(tx, new BsonDocument()).ToListAsync(ct);
            foreach (var row in returns) {
                await lease.RenewAsync(ct); _ = await owner.ReadAsync(tx, "receive", row["_id"].AsInt64, true, ct);
                var inspection = await new UnitToko.Platform.Inventory.ReturnInspectionRecords(database, clock).ReadAsync(tx, row, ct);
                if (inspection is not null) _ = await new ReturnResolutionRecords(database, clock).ReadAsync(tx, inspection, ct);
            }
            await tx.CommitTransactionAsync(ct);
        }
        foreach (var candidate in candidates) {
            await lease.RenewAsync(ct);
            if (!candidate.Exists) await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", candidate.Name }, { "validator", candidate.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in candidate.Missing) await Rows(candidate.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        }
        foreach (var before in retained.Where(row => row.GetValue("type", "") == "collection")) {
            var name = before["name"].AsString; var after = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            if (before["info"]["uuid"] != after["info"]["uuid"] || before.GetValue("options", new BsonDocument()) != after.GetValue("options", new BsonDocument())) throw Invalid();
            var actual = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct); var added = candidates.SingleOrDefault(candidate => candidate.Name == name);
            if (actual.Count != indexes[name].Length + (added.Name is null ? 0 : added.Missing.Length) || indexes[name].Any(row => !actual.Any(other => other == row))) throw Invalid();
        }
        var actualCounters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (!counters.SequenceEqual(actualCounters)) throw Invalid();
        await AfterSalesStockSourceStorage.RequireAsync(database, ct, ShipmentDispatchSchemas.LegacyValidator());
    }
}
