using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using B = UnitToko.Platform.Catalog.OperationalScanCommandBindings;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Classifies only the four existing closed command owners as pre-binding/manual.
/// No historical scan is inferred, and no business record, receipt, audit or grant is rewritten.</summary>
internal sealed class OperationalScanBindingMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 111;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
        async Task<BsonDocument?> One(string name, BsonDocument filter) => await Rows(name).Find(filter).SingleOrDefaultAsync(ct);
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying") throw Invalid();
        await lease.RenewAsync(ct);
        var definition = await AccountContactStorage.DefinitionAsync(database, B.Collection, ct);
        var indexes = definition is null ? [] : (await (await Rows(B.Collection).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
        if (definition is not null && (!AccountContactStorage.SafeDefinition(definition, OperationalScanBindingSchemas.Validator()) ||
            !B.Same(definition["options"]["validator"], OperationalScanBindingSchemas.Validator()) ||
            !AccountContactStorage.SafeIndexes(indexes, OperationalScanBindingSchemas.Keys))) throw Invalid();
        // Complete receipt/owner/audit and orphan preflight happens before the first DDL or insert.
        var expected = new Dictionary<long, BsonDocument>(); var expectedOwners = new Dictionary<long, BsonDocument>();
        var commandKeys = new HashSet<string>(StringComparer.Ordinal);
        var receiptIds = new HashSet<ObjectId>();
        var receipts = await Rows("idempotencyRecords").Find(new BsonDocument("operation", new BsonDocument("$in", new BsonArray(B.Operations)))).ToListAsync(ct);
        foreach (var receipt in receipts) {
            await lease.RenewAsync(ct); B.RequireReceipt(receipt);
            var owner = await B.OriginalAsync(receipt, One);
            var commandKey = B.Digest(new BsonDocument { { "actorId", receipt["actorId"] }, { "operation", receipt["operation"] }, { "key", receipt["key"] } });
            if (!commandKeys.Add(commandKey) || !receiptIds.Add(receipt["_id"].AsObjectId)) throw Invalid();
            var binding = B.CreateLegacy(receipt, owner.Audit); B.RequireRecord(binding);
            if (!expected.TryAdd(binding["_id"].AsInt64, binding)) throw Invalid();
            expectedOwners.Add(binding["_id"].AsInt64, owner.Owner);
        }
        foreach (var operation in B.Operations) {
            var descriptor = B.OwnerDescriptor(operation); await lease.RenewAsync(ct);
            foreach (var owner in await Rows(descriptor.Collection).Find(new BsonDocument()).ToListAsync(ct)) {
                if (!owner.TryGetValue("auditId", out var audit) || !audit.IsInt64 || !expected.TryGetValue(audit.AsInt64, out var binding) ||
                    binding["operation"] != operation || binding["actorId"] != owner[descriptor.ActorField] || binding["requestKey"] != owner["requestKey"] ||
                    !B.Same(owner, expectedOwners[audit.AsInt64])) throw Invalid();
            }
            foreach (var audit in await Rows("auditEvents").Find(new BsonDocument("$or", new BsonArray {
                new BsonDocument("kind", descriptor.AuditKind), new BsonDocument("operation", operation) })).ToListAsync(ct)) {
                if (!audit.TryGetValue("_id", out var id) || !id.IsInt64 || !expected.TryGetValue(id.AsInt64, out var binding) ||
                    binding["operation"] != operation || binding["ownerAuditHash"] != B.Digest(audit)) throw Invalid();
            }
        }
        var retained = await Rows(B.Collection).Find(new BsonDocument()).ToListAsync(ct);
        var retainedIds = new HashSet<long>();
        foreach (var row in retained) {
            B.RequireRecord(row);
            if (!retainedIds.Add(row["_id"].AsInt64) || !expected.TryGetValue(row["_id"].AsInt64, out var wanted) || !B.Same(row, wanted)) throw Invalid();
        }
        // This owner reuses audit IDs. A prospective private allocator is never required or mutated.
        if (await Rows("idCounters").Find(new BsonDocument("_id", B.Collection)).AnyAsync(ct)) throw Invalid();
        if (definition is null) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", B.Collection }, { "validator", OperationalScanBindingSchemas.Validator() },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        }
        foreach (var key in OperationalScanBindingSchemas.Keys.Where(key => !indexes.Any(index => AccountContactStorage.Matches(index, key)))) {
            await lease.RenewAsync(ct);
            await database.GetCollection<BsonDocument>(B.Collection).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        }
        foreach (var row in expected.Values.OrderBy(row => row["_id"].AsInt64)) {
            await lease.RenewAsync(ct);
            var current = await One(B.Collection, new("_id", row["_id"]));
            if (current is null) await database.GetCollection<BsonDocument>(B.Collection).WithWriteConcern(WriteConcern.WMajority).InsertOneAsync(row, cancellationToken: ct);
            else if (!B.Same(current, row)) throw Invalid();
        }
        var final = await Rows(B.Collection).Find(new BsonDocument()).ToListAsync(ct);
        if (final.Count != expected.Count || final.Any(row => !expected.TryGetValue(row["_id"].AsInt64, out var wanted) || !B.Same(row, wanted))) throw Invalid();
        await new B(database, new(database), new SystemClock()).RequireStorageAsync(ct);
    }
    private static InvalidOperationException Invalid() => new("Operational scan migration requires complete closed original receipts, matching owner audits and exact resumable pre-binding classifications; reconcile ambiguity before retrying");
}
