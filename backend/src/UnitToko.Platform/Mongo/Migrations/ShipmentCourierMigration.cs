using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Ordering;
using R = UnitToko.Platform.Fulfillment.ShipmentCourierRecords;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Actual119 must be applied. This migration anchors the existing dispatch
/// courier and exact legacy evidence membership; it never invents a handover.</summary>
internal sealed class ShipmentCourierMigration(IMongoDatabase database, MongoClient client, IClock clock) : IMigrationStep
{
    public int Version => 120;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority).WithWriteConcern(WriteConcern.WMajority);
    private static InvalidOperationException Invalid() => new("Courier migration requires actual119, complete retained shipment evidence and exact resumable120 anchors");
    private async Task<List<(string Kind, BsonDocument Row)>> ChildrenAsync(BsonDocument shipment, CancellationToken ct) {
        var result = new List<(string, BsonDocument)>();
        foreach (var kind in new[] { "attempt", "challenge", "receipt" }) {
            var name = R.SourceTable(kind); var validator = kind == "attempt" ? DeliveryAttemptSchemas.Validator(name) : OrderLineageSchemas.ReceiptValidator(name);
            FilterDefinition<BsonDocument> filter = new BsonDocument("shipmentId", shipment["_id"]);
            if (await Rows(name).Find(filter & new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Invalid();
            foreach (var row in await Rows(name).Find(filter).ToListAsync(ct)) {
                if (row["courierId"] != shipment["courierId"] || R.Time(row["recordedAt"]) < R.Time(shipment["recordedAt"])) throw Invalid();
                result.Add((kind, row));
            }
        }
        return [.. result.OrderBy(value => R.BindingKey(value.Item1, value.Item2), StringComparer.Ordinal)];
    }
    private static BsonDocument Anchor(BsonDocument shipment, IReadOnlyList<(string Kind, BsonDocument Row)> children) =>
        R.Anchor(shipment, "migration120", new BsonArray(children.Select(item => R.LegacySeal(item.Kind, item.Row))));
    private async Task RetainedEffectsAsync(BsonDocument shipment, IReadOnlyList<(string Kind, BsonDocument Row)> children, CancellationToken ct) {
        using var tx = await client.StartSessionAsync(cancellationToken: ct); tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
        var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", shipment["auditId"])).SingleOrDefaultAsync(ct);
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", shipment["dispatchedBy"] }, { "operation", ShipmentDispatchBson.Operation }, { "key", shipment["requestKey"] } }).SingleOrDefaultAsync(ct);
        if (audit != ShipmentDispatchBson.Audit(shipment) || receipt is null || receipt["bodyHash"] != shipment["bodyHash"] || receipt["result"] != ShipmentDispatchBson.Result(shipment)) throw Invalid();
        foreach (var (kind, row) in children) {
            if (kind != "attempt") await new ShipmentReceiptRecords(database).VerifyEffectsAsync(tx, row, kind == "challenge" ? ShipmentReceiptRecords.Issue : ShipmentReceiptRecords.Confirm, ct);
            else {
                var actual = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
                var original = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", DeliveryAttemptRecords.Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
                if (actual != DeliveryAttemptRecords.Audit(row) || original is null || original["bodyHash"] != row["bodyHash"] || original["result"] != DeliveryAttemptRecords.Result(row)) throw Invalid();
            }
        }
        await tx.CommitTransactionAsync(ct);
    }
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(119).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Invalid();
        var operations = new BsonArray { R.Operation, R.BindingOperation("attempt"), R.BindingOperation("challenge"), R.BindingOperation("receipt") };
        foreach (var (name, field) in new[] { ("auditEvents", "operation"), ("auditEvents", "kind"), ("idempotencyRecords", "operation"), ("outboxEvents", "kind") })
            if (await Rows(name).Find(new BsonDocument(field, new BsonDocument("$in", operations))).Limit(1).AnyAsync(ct)) throw Invalid();
        foreach (var operation in operations.Select(value => value.AsString))
            if (await Rows("outboxEvents").Find(new BsonDocument("sourceKey", new BsonRegularExpression("^" + System.Text.RegularExpressions.Regex.Escape(operation + ":")))).Limit(1).AnyAsync(ct)) throw Invalid();
        var definitions = new Dictionary<string, (BsonDocument? Definition, BsonDocument[] Indexes)>();
        foreach (var name in ShipmentCourierSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
            var indexes = definition is null ? [] : (await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            if (definition is not null && (!AccountContactStorage.SafeDefinition(definition, ShipmentCourierSchemas.Validator(name)) ||
                !AccountContactStorage.SafeIndexes(indexes, ShipmentCourierSchemas.Keys(name)))) throw Invalid();
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (counter.ElementCount != 2 || counter["_id"] != name || !counter.GetValue("value", BsonNull.Value).IsInt64 || counter["value"] != 0L)) throw Invalid();
            definitions.Add(name, (definition, indexes));
        }
        if (await Rows(R.Events).Find(new BsonDocument()).Limit(1).AnyAsync(ct) ||
            await Rows("shipments").Find(new BsonDocument("$nor", new BsonArray { OrderLineageSchemas.DispatchValidator() })).Limit(1).AnyAsync(ct)) throw Invalid();
        var expected = new Dictionary<long, string>(); long bindingCount = 0;
        // Preflight all original source hashes, retained effects and every already staged
        // anchor before creating/indexing anything. Partial prior backfills are resumable.
        await EvidenceMigration.EachAsync(Rows("shipments"), new BsonDocument(), async shipment => {
            var children = await ChildrenAsync(shipment, ct); var anchor = Anchor(shipment, children); expected.Add(shipment["_id"].AsInt64, R.Hash(anchor)); bindingCount += children.Count;
            await RetainedEffectsAsync(shipment, children, ct);
            foreach (var (name, row) in new[] { (R.Anchors, anchor), (R.Heads, R.InitialHead(anchor)) }) {
                var prior = await Rows(name).Find(new BsonDocument("_id", row["_id"])).SingleOrDefaultAsync(ct); if (prior is not null && prior != row) throw Invalid();
            }
            foreach (var (kind, row) in children) {
                var binding = R.Binding(kind, row, anchor, 1, R.Hash(anchor), null);
                var prior = await Rows(R.Bindings).Find(new BsonDocument("_id", binding["_id"])).SingleOrDefaultAsync(ct);
                if (prior is not null && prior != binding) throw Invalid();
            }
        }, lease, ct);
        foreach (var name in new[] { R.Anchors, R.Heads })
            if (await Rows(name).Find(new BsonDocument("_id", new BsonDocument("$nin", new BsonArray(expected.Keys)))).Limit(1).AnyAsync(ct)) throw Invalid();
        await EvidenceMigration.EachAsync(Rows(R.Bindings), new BsonDocument(), async binding => {
            if (!expected.ContainsKey(binding["shipmentId"].AsInt64) || !binding["auditId"].IsBsonNull) throw Invalid();
            var row = await Rows(R.SourceTable(binding["kind"].AsString)).Find(new BsonDocument("_id", binding["recordId"])).SingleOrDefaultAsync(ct);
            if (row is null || row["shipmentId"] != binding["shipmentId"] || binding["sourceHash"] != R.Hash(R.StableSource(binding["kind"].AsString, row))) throw Invalid();
        }, lease, ct);
        foreach (var kind in new[] { "attempt", "challenge", "receipt" })
            if (await Rows(R.SourceTable(kind)).Find(new BsonDocument("shipmentId", new BsonDocument("$nin", new BsonArray(expected.Keys)))).Limit(1).AnyAsync(ct)) throw Invalid();
        foreach (var (kind, operation, idField) in new[] { ("attempt", DeliveryAttemptRecords.Operation, "attemptId"),
            ("challenge", ShipmentReceiptRecords.Issue, "challengeId"), ("receipt", ShipmentReceiptRecords.Confirm, "receiptId") })
            foreach (var (name, operationField, resultField) in new[] { ("auditEvents", "operation", "result"), ("idempotencyRecords", "operation", "result"), ("outboxEvents", "kind", "payload") })
                await EvidenceMigration.EachAsync(Rows(name), new BsonDocument(operationField, operation), async effect => {
                    var result = effect.GetValue(resultField, BsonNull.Value);
                    if (!result.IsBsonDocument || !result.AsBsonDocument.GetValue("shipmentId", BsonNull.Value).IsInt64 ||
                        !result.AsBsonDocument.GetValue(idField, BsonNull.Value).IsInt64 || !expected.ContainsKey(result["shipmentId"].AsInt64)) throw Invalid();
                    var root = await Rows(R.SourceTable(kind)).Find(new BsonDocument { { "_id", result[idField] }, { "shipmentId", result["shipmentId"] } }).SingleOrDefaultAsync(ct);
                    if (root is null || result != (kind == "attempt" ? DeliveryAttemptRecords.Result(root) : ShipmentReceiptRecords.Result(root, operation))) throw Invalid();
                    if (name == "outboxEvents" && effect["createdAt"] != root["recordedAt"]) throw Invalid();
                }, lease, ct);
        foreach (var name in ShipmentCourierSchemas.Collections) {
            await lease.RenewAsync(ct);
            if (definitions[name].Definition is null) await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", name }, { "validator", ShipmentCourierSchemas.Validator(name) },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            var indexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            foreach (var key in ShipmentCourierSchemas.Keys(name).Where(key => !indexes.Any(index => AccountContactStorage.Matches(index, key))))
                await Rows(name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        }
        await EvidenceMigration.EachAsync(Rows("shipments"), new BsonDocument(), async shipment => {
            var children = await ChildrenAsync(shipment, ct); var anchor = Anchor(shipment, children);
            if (!expected.TryGetValue(shipment["_id"].AsInt64, out var hash) || hash != R.Hash(anchor)) throw Invalid();
            var unit = new MongoUnitOfWork(client);
            await unit.ExecuteAsync(async (tx, token) => {
                async Task Insert(string name, BsonDocument row) {
                    var prior = await Rows(name).Find(tx, new BsonDocument("_id", row["_id"])).SingleOrDefaultAsync(token);
                    if (prior is null) await Rows(name).InsertOneAsync(tx, row, cancellationToken: token); else if (prior != row) throw Invalid();
                }
                await Insert(R.Anchors, anchor); await Insert(R.Heads, R.InitialHead(anchor));
                foreach (var (kind, row) in children) await Insert(R.Bindings, R.Binding(kind, row, anchor, 1, R.Hash(anchor), null));
                return true;
            }, ct);
        }, lease, ct);
        if (await Rows(R.Anchors).CountDocumentsAsync(new BsonDocument(), cancellationToken: ct) != expected.Count ||
            await Rows(R.Heads).CountDocumentsAsync(new BsonDocument(), cancellationToken: ct) != expected.Count ||
            await Rows(R.Bindings).CountDocumentsAsync(new BsonDocument(), cancellationToken: ct) != bindingCount) throw Invalid();
        var ownerUnit = new MongoUnitOfWork(client); var orders = new OrderSubmissionService(database, ownerUnit, new(database), clock);
        var preparations = new ShipmentPreparationService(database, ownerUnit, new(database), clock, orders);
        var queries = new ShipmentQueryService(database, ownerUnit, clock, orders, preparations);
        var documents = new ShipmentDocumentService(database, ownerUnit, new(database), clock, queries);
        var receipts = new ShipmentReceiptService(database, ownerUnit, new(database), clock, queries, documents);
        // Only after all roots exist can cross-shipment physical provenance be replayed.
        // Full original owners verify dispatch/COGS, document, attempt chain, QR, consent
        // and receipt effects before 120 may become applied. A failure leaves 120 applying.
        await EvidenceMigration.EachAsync(Rows("shipments"), new BsonDocument(), async shipment => {
            await ownerUnit.ExecuteAsync(async (tx, token) => {
                var source = await queries.FulfillmentStockSourceDocumentAsync(tx, shipment["_id"].AsInt64, token);
                await receipts.ValidateCourierMigrationHistoryAsync(tx, source, token);
                _ = await new R(database, clock).ReadAsync(tx, shipment, token); return true;
            }, ct);
        }, lease, ct);
        foreach (var name in ShipmentCourierSchemas.Collections) if (definitions[name].Definition is { } before) {
            var after = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            var indexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (before["info"]["uuid"] != after["info"]["uuid"] || before["options"] != after["options"] ||
                definitions[name].Indexes.Any(index => !indexes.Any(current => current == index))) throw Invalid();
        }
        await R.RequireStorageAsync(database, ct);
    }
}
