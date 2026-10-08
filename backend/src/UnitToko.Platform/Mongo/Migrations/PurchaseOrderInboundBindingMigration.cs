using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Procurement.PurchaseOrderBson;

namespace UnitToko.Platform.Mongo.Migrations;

// Additive M94 follows the preserved M93 checkpoint.
// No schema of an old PO, financial source, receiving record or evidence owner changes.
internal sealed class PurchaseOrderInboundBindingMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 94;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("Inbound binding migration requires authentic retained owners and genuinely empty new owners");
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying" || database.Client is not MongoClient client || clock.UtcNow == default || clock.UtcNow.Offset != TimeSpan.Zero) throw Invalid();
        var retained = await (await database.ListCollectionsAsync(cancellationToken: ct)).ToListAsync(ct);
        var indexes = new Dictionary<string, BsonDocument[]>();
        foreach (var definition in retained.Where(row => row.GetValue("type", "") == "collection"))
            indexes.Add(definition["name"].AsString, (await (await Rows(definition["name"].AsString).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray());
        var counters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        foreach (var counter in counters)
            if (!counter.TryGetValue("_id", out var key) || !key.IsString || string.IsNullOrWhiteSpace(key.AsString) ||
                !counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or >= long.MaxValue - 1) throw Invalid();
        var targets = new List<(string Name, bool Exists, BsonDocument Validator, BsonDocument[] Missing)>();
        foreach (var name in PurchaseOrderInboundBindingSchemas.Collections) {
            await lease.RenewAsync(ct); var definition = retained.SingleOrDefault(row => row.GetValue("name", "") == name);
            var required = PurchaseOrderInboundBindingSchemas.Validator(name); var keys = PurchaseOrderInboundBindingSchemas.Keys(name);
            if (definition is not null && !AccountContactStorage.SafeDefinition(definition, required) || await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
            var currentIndexes = definition is null ? [] : indexes[name];
            if (currentIndexes.Any(row => InvoiceStorage.Unsafe(row) || row.Contains("partialFilterExpression") ||
                row.GetValue("unique", false).AsBoolean && row["key"] != new BsonDocument("_id", 1) && !keys.Any(key => key.Equals(row["key"])))) throw Invalid();
            if (definition is not null && !currentIndexes.Any(row => row["name"] == "_id_" && row["key"] == new BsonDocument("_id", 1))) throw Invalid();
            for (var index = 0; index < keys.Length; index++)
                if (currentIndexes.Any(row => (row["key"] == keys[index] || row["name"] == "ux_" + name + "_" + index) &&
                    (row["key"] != keys[index] || !row.GetValue("unique", false).AsBoolean))) throw Invalid();
            // IDs belong to the actual PO proposal/decision allocator. New binding
            // owners cannot have independent spent counters or retrospective rows.
            var counter = counters.SingleOrDefault(row => row["_id"] == name); if (counter is not null && counter["value"] != 0L) throw Invalid();
            targets.Add((name, definition is not null, definition is null ? required : definition["options"]["validator"].AsBsonDocument,
                keys.Where(key => !currentIndexes.Any(row => row["key"] == key && row.GetValue("unique", false).AsBoolean)).ToArray()));
        }
        await ProcurementStockSourceStorage.RequireAsync(database, ct, ShipmentDispatchSchemas.LegacyValidator());
        foreach (var (names, validator, keys) in new[] {
            (PurchaseOrderSchemas.Collections, (Func<string, BsonDocument>)PurchaseOrderSchemas.Validator, (Func<string, BsonDocument[]>)PurchaseOrderSchemas.Keys),
            (ProcurementReceivingSchemas.Collections, (Func<string, BsonDocument>)ProcurementReceivingSchemas.Validator, (Func<string, BsonDocument[]>)ProcurementReceivingSchemas.Keys),
            (ProcurementInspectionSchemas.Collections, (Func<string, BsonDocument>)ProcurementInspectionSchemas.Validator, (Func<string, BsonDocument[]>)ProcurementInspectionSchemas.Keys) })
        foreach (var name in names) {
            await lease.RenewAsync(ct); await InventoryRepository.RequireStorageAsync(database, name, validator(name), keys(name), ct);
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { validator(name) })).Limit(1).AnyAsync(ct) ||
                indexes[name].Any(row => InvoiceStorage.Unsafe(row) || row.Contains("partialFilterExpression"))) throw Invalid();
        }
        foreach (var name in new[] { B.Proposals, B.Decisions, B.Orders, "auditEvents" }) {
            var maximum = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
            var counter = counters.SingleOrDefault(row => row["_id"] == name);
            if (maximum is not null && (!maximum["_id"].IsInt64 || maximum["_id"].AsInt64 is <= 0 or >= long.MaxValue - 1 ||
                counter is null || counter["value"].AsInt64 < maximum["_id"].AsInt64)) throw Invalid();
        }
        var unit = new MongoUnitOfWork(client); var catalog = new CatalogAuthoringService(database, unit, new(database), clock);
        var requests = new ProcurementRequestService(database, unit, new(database), clock, catalog);
        var suppliers = new SupplierService(database, unit, new(database), clock, catalog);
        var orders = new PurchaseOrderService(database, unit, new(database), clock, catalog, requests, suppliers);
        var proof = new PurchaseOrderRecords(database, catalog, clock);
        await EvidenceMigration.EachAsync(Rows(B.Proposals), new BsonDocument(), async raw => {
            using var tx = await client.StartSessionAsync(cancellationToken: ct); tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
            var proposal = await proof.ProposalAsync(tx, raw["_id"].AsInt64, ct); _ = await proof.DecisionForAsync(tx, proposal, ct); await tx.CommitTransactionAsync(ct);
        }, lease, ct);
        foreach (var name in new[] { B.Decisions, B.Orders }) {
            var orphans = await Rows(name).Aggregate<BsonDocument>(new BsonDocument[] { new("$lookup", new BsonDocument {
                { "from", B.Proposals }, { "localField", "proposalId" }, { "foreignField", "_id" }, { "as", "owner" } }),
                new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1) }).ToListAsync(ct);
            if (orphans.Count != 0) throw Invalid();
        }
        // Existing receiving and private inspection owners remain semantic authorities.
        await EvidenceMigration.EachAsync(Rows(ProcurementReceivingBson.Notices), new BsonDocument(), async raw => {
            using var tx = await client.StartSessionAsync(cancellationToken: ct); tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
            var receiving = new ProcurementReceivingRecords(database, clock, catalog, orders);
            var notice = await receiving.NoticeAsync(tx, raw["_id"].AsInt64, ct); var custody = await receiving.HistoryAsync(tx, notice, ct);
            foreach (var intake in custody.Intakes)
                _ = await new ProcurementInspectionRecords(database, unit, clock, catalog, orders).HistoryAsync(tx, notice["_id"].AsInt64, intake["_id"].AsInt64, ct);
            await tx.CommitTransactionAsync(ct);
        }, lease, ct);
        foreach (var target in targets) {
            await lease.RenewAsync(ct);
            if (!target.Exists) await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", target.Name }, { "validator", target.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            var keys = PurchaseOrderInboundBindingSchemas.Keys(target.Name);
            foreach (var key in target.Missing) await Rows(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Name = "ux_" + target.Name + "_" + Array.FindIndex(keys, value => value == key), Unique = true }), cancellationToken: ct);
        }
        foreach (var before in retained.Where(row => row.GetValue("type", "") == "collection")) {
            var name = before["name"].AsString; var after = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            if (before["info"]["uuid"] != after["info"]["uuid"] || before.GetValue("options", new BsonDocument()) != after.GetValue("options", new BsonDocument())) throw Invalid();
            var actual = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct); var target = targets.SingleOrDefault(row => row.Name == name);
            if (actual.Count != indexes[name].Length + (target.Name is null ? 0 : target.Missing.Length) || indexes[name].Any(row => !actual.Any(value => value.Equals(row)))) throw Invalid();
        }
        var afterCounters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (counters.Count != afterCounters.Count || counters.Where((row, index) => !row.Equals(afterCounters[index])).Any()) throw Invalid();
        await PurchaseOrderInboundBindingStorage.RequireAsync(database, ct);
    }
}
