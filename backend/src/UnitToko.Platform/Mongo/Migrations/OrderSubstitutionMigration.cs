using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.RegularExpressions;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Version114 adds empty consent stores, optional immutable line identity and a forward source-event discriminant.
/// It never invents proposals, consent, reservations or commercial amendments for old orders.</summary>
internal sealed class OrderSubstitutionMigration(IMongoDatabase database, MongoClient client, IClock clock) : IMigrationStep
{
    public int Version => 114;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
        static InvalidOperationException Invalid() => new("Substitution migration requires exact predecessor history, unchanged retained source rows and empty prospective consent stores");
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Invalid();
        var operations = new BsonArray { OrderSubstitutionBson.ProposeOperation, OrderSubstitutionBson.AcceptOperation, OrderSubstitutionBson.DeclineOperation };
        var kinds = new BsonArray { "ordering.substitution.proposed", "ordering.substitution.decided" };
        var sourceKey = new BsonRegularExpression("^(?:" + string.Join("|", operations.Select(value => Regex.Escape(value.AsString))) + "):");
        // These exact operations have no valid producer before applied114. Retained effects
        // cannot be legitimized by creating empty owner collections around an orphan.
        foreach (var (name, filter) in new (string, BsonDocument)[] {
            ("auditEvents", new("$or", new BsonArray { new BsonDocument("operation", new BsonDocument("$in", operations)), new BsonDocument("kind", new BsonDocument("$in", kinds)) })),
            ("idempotencyRecords", new("operation", new BsonDocument("$in", operations))),
            ("outboxEvents", new("$or", new BsonArray { new BsonDocument("kind", new BsonDocument("$in", operations)), new BsonDocument("sourceKey", sourceKey) }))
        }) if (await Rows(name).Find(filter).Limit(1).AnyAsync(ct)) throw Invalid();
        foreach (var name in OrderSubstitutionSchemas.Collections) {
            await lease.RenewAsync(ct); var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
            var indexes = definition is null ? [] : (await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            if (definition is not null && (!AccountContactStorage.SafeDefinition(definition, OrderSubstitutionSchemas.Validator(name)) ||
                !AccountContactStorage.SafeIndexes(indexes, OrderSubstitutionSchemas.Keys(name))) ||
                await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (counter.ElementCount != 2 || !counter.GetValue("_id", BsonNull.Value).IsString || counter["_id"] != name ||
                !counter.GetValue("value", BsonNull.Value).IsInt64 || counter["value"] != 0L)) throw Invalid();
        }
        var sourceName = OrderStockSourceRecords.Transitions;
        var sourceDefinition = await AccountContactStorage.DefinitionAsync(database, sourceName, ct) ?? throw Invalid();
        var sourceIndexes = (await (await Rows(sourceName).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
        var previousValidator = OrderStockSourceSchemas.Validator(sourceName); var nextValidator = OrderSubstitutionSchemas.StockSourceValidator(sourceName);
        if ((!AccountContactStorage.SafeDefinition(sourceDefinition, previousValidator) && !AccountContactStorage.SafeDefinition(sourceDefinition, nextValidator)) ||
            !AccountContactStorage.SafeIndexes(sourceIndexes, OrderStockSourceSchemas.Keys(sourceName)) ||
            OrderStockSourceSchemas.Keys(sourceName).Any(key => !sourceIndexes.Any(index => AccountContactStorage.Matches(index, key))) ||
            await Rows(sourceName).Find(new BsonDocument("$nor", new BsonArray { previousValidator })).Limit(1).AnyAsync(ct) ||
            await Rows("movements").Find(new BsonDocument("source.namespace", "ordering.substitution")).Limit(1).AnyAsync(ct)) throw Invalid();
        var retained = new List<OrderLineageMigrationCapture> {
            await OrderLineageMigrationCapture.ReadAsync(database, sourceName, previousValidator, nextValidator, ct)
        };
        foreach (var name in OrderLineageSchemas.ChangedCollections) {
            await lease.RenewAsync(ct);
            var previous = OrderLineageSchemas.Previous(name);
            retained.Add(await OrderLineageMigrationCapture.ReadAsync(database, name, previous, OrderLineageSchemas.Expand(previous), ct));
        }
        // Expanding an enum cannot legitimize a malformed retained transition or a new owner
        // row that appeared before its migration. Replay genuine retained owners before DDL.
        await EvidenceMigration.EachAsync(Rows(sourceName), new BsonDocument(), row => {
            if (row["transitionHash"] != OrderStockSourceRecords.Hash(row)) throw Invalid();
            return Task.CompletedTask;
        }, lease, ct);
        var owner = new OrderSubmissionService(database, new(client), new(database), clock);
        await EvidenceMigration.EachAsync(Rows("orders"), new BsonDocument(), async row => {
            using var tx = await client.StartSessionAsync(cancellationToken: ct);
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
            _ = await owner.ReadStockSourceGraphAsync(tx, row["_id"].AsInt64, ct);
            await tx.CommitTransactionAsync(ct);
        }, lease, ct);
        var sourceRecords = new OrderStockSourceRecords(database);
        await EvidenceMigration.EachAsync(Rows(OrderStockSourceRecords.Heads), new BsonDocument(), async row => {
            using var tx = await client.StartSessionAsync(cancellationToken: ct);
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
            _ = await sourceRecords.HistoryAsync(tx, row["_id"].AsInt64, ct, clock: clock);
            await tx.CommitTransactionAsync(ct);
        }, lease, ct);
        foreach (var name in OrderSubstitutionSchemas.Collections) {
            await lease.RenewAsync(ct);
            if (await AccountContactStorage.DefinitionAsync(database, name, ct) is null)
                await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", name }, { "validator", OrderSubstitutionSchemas.Validator(name) },
                    { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            var indexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            foreach (var key in OrderSubstitutionSchemas.Keys(name).Where(key => !indexes.Any(index => AccountContactStorage.Matches(index, key))))
                await Rows(name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        }
        foreach (var capture in retained) await capture.ApplyAsync(database, lease, ct);
        var finalDefinition = await AccountContactStorage.DefinitionAsync(database, sourceName, ct) ?? throw Invalid();
        var finalIndexes = await (await Rows(sourceName).Indexes.ListAsync(ct)).ToListAsync(ct);
        if (!AccountContactStorage.SafeDefinition(finalDefinition, nextValidator) || finalDefinition["info"]["uuid"] != sourceDefinition["info"]["uuid"] ||
            finalIndexes.Count != sourceIndexes.Length || sourceIndexes.Any(before => !finalIndexes.Any(after => after == before))) throw Invalid();
        await OrderSubstitutionRecords.RequireStorageAsync(database, ct);
    }
}
