using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo.Migrations;

// Explicit deployment migration only; no policy, storage root, quota or scanner is seeded.
internal sealed class EvidenceMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 13;
    private sealed record Index(string Name, BsonDocument Key, bool Unique = false, BsonDocument? Partial = null);
    private sealed record Plan(string Name, bool Exists, BsonDocument Validator, List<Index> Create);
    private static BsonDocument Exists(params string[] names) => new("$and", new BsonArray(names.Select(n => new BsonDocument(n, new BsonDocument("$exists", true)))));
    private static Index[] Required(string name) => name switch
    {
        "evidenceStorePolicies" => [new("ux_evidence_store", new("storeId", 1), true)],
        "evidenceLineages" => [],
        "documentVersions" => [new("uq_documentId_version", new() { { "documentId", 1 }, { "version", 1 } }, true, Exists("documentId", "version")),
            new("ux_evidence_lineage_version", new() { { "lineageId", 1 }, { "contentVersion", 1 } }, true), new("ux_evidence_version_reservation", new("reservationId", 1), true)],
        "evidenceUploadReservations" => [new("ux_evidence_reservation_attempt", new("attemptId", 1), true),
            new("ux_evidence_reservation_version", new("versionRowId", 1), true), new("ux_evidence_reservation_lineage", new("lineageId", 1), true),
            new("ix_evidence_uploader_created", new() { { "uploader.userId", 1 }, { "createdAt", 1 }, { "_id", 1 } }),
            new("ix_evidence_target_created", new() { { "target.kind", 1 }, { "target.id", 1 }, { "createdAt", 1 }, { "_id", 1 } }),
            new("ix_evidence_policy", new() { { "policyId", 1 }, { "_id", 1 } })],
        "evidenceUploadAttempts" => [new("ux_evidence_attempt_reservation", new("reservationId", 1), true),
            new("ux_evidence_stage_token", new() { { "storeId", 1 }, { "stageToken", 1 } }, true),
            new("ux_evidence_generation", new() { { "storeId", 1 }, { "generation", 1 } }, true),
            new("ix_evidence_attempt_claim", new() { { "state", 1 }, { "claimUntil", 1 }, { "_id", 1 } })],
        "auditEvents" => [new("ix_evidence_audit_reservation", new() { { "evidence.reservationId", 1 }, { "createdAt", 1 }, { "_id", 1 } })],
        "idempotencyRecords" => [new("uq_actorId_operation_key", new() { { "actorId", 1 }, { "operation", 1 }, { "key", 1 } }, true, Exists("actorId", "operation", "key"))],
        "outboxEvents" => [new("uq_sourceKey", new("sourceKey", 1), true, Exists("sourceKey"))],
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var plans = new List<Plan>();
        foreach (var name in EvidenceSchemas.Collections)
        {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "collection") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !Simple(collation)) throw Invalid(name, "collection options");
            var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var required = EvidenceSchemas.Validator(name);
            var validator = previous.ElementCount == 0 ? required : Contains(previous, required) ? previous : new BsonDocument("$and", new BsonArray { previous, required });
            var rows = Rows(name);
            if (await rows.Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Invalid(name, "records or stronger validator");
            await EachAsync(rows, new BsonDocument(), async row =>
            {
                if (!EvidenceSchemas.IsValid(name, row)) throw Invalid(name, "records");
                await Task.CompletedTask;
            }, lease, ct);
            var existing = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            if (definition is not null && !existing.Any(index => index["name"] == "_id_" && index["key"].AsBsonDocument.Equals(new BsonDocument("_id", 1)) && !Unsafe(index, null))) throw Invalid(name, "identity index");
            var planned = Required(name); var create = new List<Index>();
            foreach (var old in existing)
            {
                if (old["name"] == "_id_") continue;
                var match = planned.FirstOrDefault(i => i.Name == old["name"] || i.Key.Equals(old["key"].AsBsonDocument));
                if (match is not null)
                {
                    if (!match.Key.Equals(old["key"].AsBsonDocument) || old.GetValue("unique", false).AsBoolean != match.Unique ||
                        Unsafe(old, match.Partial) || (match.Partial is not null && !old.Contains("partialFilterExpression")))
                        throw Invalid(name, "required index");
                }
                else if (Unsafe(old, null) || old.GetValue("unique", false).AsBoolean ||
                    old["key"].AsBsonDocument.Any(pair => !pair.Value.IsNumeric || pair.Value.ToInt32() is not (1 or -1)))
                    throw Invalid(name, "unsupported additional index");
            }
            foreach (var index in planned)
            {
                if (!existing.Any(old => index.Key.Equals(old["key"].AsBsonDocument))) create.Add(index);
                if (index.Unique)
                {
                    var key = new BsonDocument(index.Key.Names.Select((field, n) => new BsonElement("k" + n, "$" + field)));
                    var pipeline = new List<BsonDocument>();
                    if (index.Partial is not null) pipeline.Add(new("$match", index.Partial));
                    pipeline.Add(new("$group", new BsonDocument { { "_id", key }, { "count", new BsonDocument("$sum", 1) } }));
                    pipeline.Add(new("$match", new BsonDocument("count", new BsonDocument("$gt", 1))));
                    pipeline.Add(new("$limit", 1));
                    if (await (await rows.AggregateAsync<BsonDocument>(pipeline, new AggregateOptions { AllowDiskUse = true, BatchSize = 1 }, ct)).AnyAsync(ct)) throw Invalid(name, "duplicate keys");
                }
            }
            plans.Add(new(name, definition is not null, validator, create));
        }

        await EvidenceMigrationGraph.ValidateAsync(database, lease, ct);
        var maxima = new Dictionary<string, long> { ["evidenceIds"] = 0, ["evidenceStorePolicies"] = 0, ["auditEvents"] = 0 };
        foreach (var name in new[] { "evidenceLineages", "documentVersions", "evidenceUploadReservations", "evidenceUploadAttempts", "evidenceStorePolicies", "auditEvents" })
        {
            var counter = name is "evidenceStorePolicies" or "auditEvents" ? name : "evidenceIds";
            await EachAsync(Rows(name), new BsonDocument(), row =>
            {
                if (!row.TryGetValue("_id", out var id) || !id.IsInt64 || id.AsInt64 is <= 0 or long.MaxValue) throw Invalid(name, "allocator identity");
                maxima[counter] = Math.Max(maxima[counter], id.AsInt64); return Task.CompletedTask;
            }, lease, ct);
        }
        foreach (var pair in maxima)
        {
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", pair.Key)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue))
                throw Invalid("idCounters", "allocator high-water mark");
        }

        // All records, relations, options, indices and counters passed before the first target mutation.
        foreach (var plan in plans)
        {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument {
                { plan.Exists ? "collMod" : "create", plan.Name }, { "validator", plan.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        }
        foreach (var plan in plans)
            foreach (var index in plan.Create)
            {
                await lease.RenewAsync(ct);
                await Rows(plan.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(index.Key,
                    new CreateIndexOptions<BsonDocument> { Name = index.Name, Unique = index.Unique, PartialFilterExpression = index.Partial }), cancellationToken: ct);
            }
        foreach (var pair in maxima.Where(pair => pair.Value > 0))
        {
            await lease.RenewAsync(ct);
            await Rows("idCounters").WithWriteConcern(WriteConcern.WMajority).UpdateOneAsync(new BsonDocument("_id", pair.Key),
                Builders<BsonDocument>.Update.Max("value", pair.Value), new UpdateOptions { IsUpsert = true }, ct);
        }
    }

    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    internal static async Task EachAsync(IMongoCollection<BsonDocument> rows, FilterDefinition<BsonDocument> filter,
        Func<BsonDocument, Task> action, MigrationLease lease, CancellationToken ct, BsonDocument? sort = null)
    {
        using var cursor = await rows.FindAsync(filter, new FindOptions<BsonDocument> { BatchSize = 128, Sort = sort }, ct);
        while (await cursor.MoveNextAsync(ct))
        {
            await lease.RenewAsync(ct);
            foreach (var row in cursor.Current) { ct.ThrowIfCancellationRequested(); await action(row); }
        }
    }
    private static bool Contains(BsonDocument existing, BsonDocument required) => CatalogSchemas.SemanticEquals(existing, required) ||
        existing.TryGetValue("$and", out var list) && list.IsBsonArray && list.AsBsonArray.Any(value => value.IsBsonDocument && Contains(value.AsBsonDocument, required));
    private static bool Simple(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.ElementCount == 1 && value["locale"] == "simple";
    private static bool Unsafe(BsonDocument index, BsonDocument? partial) =>
        index.Contains("expireAfterSeconds") || index.GetValue("sparse", false).AsBoolean || index.GetValue("hidden", false).AsBoolean ||
        index.TryGetValue("collation", out var collation) && !Simple(collation) ||
        index.TryGetValue("partialFilterExpression", out var actual) && (partial is null || !CatalogSchemas.SemanticEquals(actual, partial));
    internal static InvalidOperationException Invalid(string name, string reason) =>
        new($"Evidence migration found incompatible {reason} in {name}; review the data before retrying");
}
