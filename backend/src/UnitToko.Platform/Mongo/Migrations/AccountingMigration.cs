using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo.Migrations;
// Explicit deployment migration only. No chart, period, account, source or operator is invented.
internal sealed class AccountingMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 12;

    private sealed record Index(string Name, BsonDocument Key, bool Unique = false, BsonDocument? Partial = null);
    private sealed record Target(string Name, bool Exists, BsonDocument Validator, List<Index> Indexes, long Maximum);
    private static Index[] RequiredIndexes(string name) => name switch
    {
        "accountingBooks" => [new("ux_accounting_book", new("canonicalKey", 1), true)],
        "accountingChartVersions" => [new("ux_accounting_chart_version", new() { { "bookId", 1 }, { "version", 1 } }, true)],
        "journalEntries" => [new("uq_sourceEvent", new("sourceEvent", 1), true), new("ux_accounting_reversal", new("reversalOf", 1), true, new("reversalOf", new BsonDocument("$type", "long"))), new("ix_book_date_id", new() { { "bookId", 1 }, { "businessDate", 1 }, { "_id", 1 } })],
        "periods" => [new("ux_book_month", new() { { "bookId", 1 }, { "month", 1 } }, true)],
        "accountingPeriodHistory" => [new("ux_period_version", new() { { "periodId", 1 }, { "version", 1 } }, true)],
        "accountingAuditEvents" => [new("ux_accounting_event", new("eventKey", 1), true)],
        _ => throw new ArgumentOutOfRangeException(nameof(name))};
    // Read-only admission for actual journal owners and later deployment preflights.
    // Reuse the installed contract, including the exact historical source-key partial index.
    internal static async Task RequireInstalledStorageAsync(IMongoDatabase database, CancellationToken ct) {
        foreach (var name in AccountingSchemas.Collections) {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition is null || definition.GetValue("type", "") != "collection" || options is null ||
                options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.GetValue("validationLevel", "strict") != "strict" || options.GetValue("validationAction", "error") != "error" ||
                options.TryGetValue("collation", out var collation) && !Simple(collation) ||
                !options.TryGetValue("validator", out var validator) || !validator.IsBsonDocument || !Contains(validator.AsBsonDocument, AccountingSchemas.Validator(name))) throw Unavailable();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var required = RequiredIndexes(name);
            bool Compatible(Index expected, BsonDocument actual) {
                if (actual["key"] != expected.Key || actual.GetValue("unique", false).AsBoolean != expected.Unique) return false;
                var copy = actual.DeepClone().AsBsonDocument;
                if (copy.TryGetValue("partialFilterExpression", out var partial)) {
                    var legacy = new BsonDocument("$and", new BsonArray { new BsonDocument("sourceEvent", new BsonDocument("$exists", true)) });
                    if (expected.Partial is not null && CatalogSchemas.SemanticEquals(partial, expected.Partial) ||
                        name == "journalEntries" && expected.Name == "uq_sourceEvent" && CatalogSchemas.SemanticEquals(partial, legacy)) copy.Remove("partialFilterExpression");
                } else if (expected.Partial is not null) return false;
                return !Unsafe(copy);
            }
            if (!indexes.Any(index => index["name"] == "_id_" && index["key"] == new BsonDocument("_id", 1) && !Unsafe(index))) throw Unavailable();
            foreach (var index in indexes.Where(index => index["name"] != "_id_")) {
                var known = required.Where(expected => index["key"] == expected.Key || index["name"] == expected.Name).ToArray();
                if (known.Length > 0 ? known.Any(expected => !Compatible(expected, index)) : Unsafe(index) || index.GetValue("unique", false).AsBoolean) throw Unavailable();
            }
            foreach (var expected in required) if (!indexes.Any(index => Compatible(expected, index))) throw Unavailable();
        }
        static UnitToko.Modules.Contracts.DomainValidationException Unavailable() => new("accounting_storage_unavailable", "The complete installed accounting storage and journal uniqueness are required");
    }
    public async Task ApplyAsync(MigrationLease lease, CancellationToken token)
    {
        var targets = new List<Target>();
        var records = new Dictionary<string, List<BsonDocument>>();
        foreach (var name in AccountingSchemas.Collections)
        {
            await lease.RenewAsync(token);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, token)).SingleOrDefaultAsync(token);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "collection") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.TryGetValue("collation", out var collation) && !Simple(collation))
                throw Invalid(name, "collection options");
            var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var required = AccountingSchemas.Validator(name);
            var validator = previous.ElementCount == 0 ? required : Contains(previous, required) ? previous : new BsonDocument("$and", new BsonArray { previous, required });
            var collection = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            if (await collection.Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(token))
                throw Invalid(name, "records");
            var rows = await collection.Find(new BsonDocument()).ToListAsync(token);
            records[name] = rows;
            var existing = definition is null ? [] : await (await collection.Indexes.ListAsync(token)).ToListAsync(token);
            if (existing.Any(index => index.Contains("expireAfterSeconds")) || definition is not null && !existing.Any(index => index["name"] == "_id_" && index["key"].AsBsonDocument.Equals(new BsonDocument("_id", 1)) && !Unsafe(index)))
                throw Invalid(name, "identity or TTL index");
            var create = new List<Index>();
            foreach (var index in RequiredIndexes(name))
            {
                var found = false;
                foreach (var old in existing)
                {
                    var same = old["key"].AsBsonDocument.Equals(index.Key);
                    if (old["name"] != index.Name && !same)
                        continue;
                    var copy = old.DeepClone().AsBsonDocument;
                    if (copy.TryGetValue("partialFilterExpression", out var partial))
                    {
                        var legacy = new BsonDocument("$and", new BsonArray { new BsonDocument("sourceEvent", new BsonDocument("$exists", true)) });
                        if (name == "journalEntries" && index.Name == "uq_sourceEvent" && CatalogSchemas.SemanticEquals(partial, legacy) || index.Partial is not null && CatalogSchemas.SemanticEquals(partial, index.Partial))
                            copy.Remove("partialFilterExpression");
                    }

                    if (!same || old.GetValue("unique", false).AsBoolean != index.Unique || Unsafe(copy) || index.Partial is not null && !old.Contains("partialFilterExpression"))
                        throw Invalid(name, "required index");
                    found = true;
                }

                if (index.Unique)
                {
                    var indexed = rows.Where(row => index.Partial is null || row["reversalOf"].IsInt64);
                    if (indexed.GroupBy(row => new BsonDocument(index.Key.Names.Select(field => new BsonElement(field, row[field])))).Any(group => group.Count() > 1))
                        throw Invalid(name, "duplicate keys");
                }

                if (!found)
                    create.Add(index);
            }

            var maximum = rows.Count == 0 ? 0 : rows.Max(row => row["_id"].AsInt64);
            var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(token);
            if (maximum == long.MaxValue || counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 < 0 || value.AsInt64 == long.MaxValue))
                throw Invalid(name, "allocator high-water mark");
            targets.Add(new(name, definition is not null, validator, create, maximum));
        }

        AccountingMigrationGraph.Validate(records);
        // No target DDL or sequence write occurs until every target and relationship passed.
        foreach (var target in targets)
        {
            await lease.RenewAsync(token);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { target.Exists ? "collMod" : "create", target.Name }, { "validator", target.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: token);
        }

        foreach (var target in targets)
        {
            if (target.Maximum > 0)
            {
                await lease.RenewAsync(token);
                await database.GetCollection<BsonDocument>("idCounters").WithWriteConcern(WriteConcern.WMajority).UpdateOneAsync(new BsonDocument("_id", target.Name), Builders<BsonDocument>.Update.Max("value", target.Maximum), new UpdateOptions { IsUpsert = true }, token);
            }

            foreach (var index in target.Indexes)
            {
                await lease.RenewAsync(token);
                await database.GetCollection<BsonDocument>(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(index.Key, new CreateIndexOptions<BsonDocument> { Name = index.Name, Unique = index.Unique, PartialFilterExpression = index.Partial }), cancellationToken: token);
            }
        }
    }

    private static bool Simple(BsonValue collation) => collation.IsBsonDocument && collation.AsBsonDocument.GetValue("locale", "") == "simple";
    private static bool Unsafe(BsonDocument index) => index.GetValue("sparse", false).AsBoolean || index.GetValue("hidden", false).AsBoolean || index.Contains("partialFilterExpression") || index.Contains("expireAfterSeconds") || index.TryGetValue("collation", out var collation) && !Simple(collation);
    private static bool Contains(BsonDocument validator, BsonDocument required) => CatalogSchemas.SemanticEquals(validator, required) || validator.TryGetValue("$and", out var value) && value.IsBsonArray && value.AsBsonArray.Any(item => item.IsBsonDocument && Contains(item.AsBsonDocument, required));
    internal static InvalidOperationException Invalid(string name, string reason) => new($"Accounting migration found incompatible {reason} in {name}; review the data before retrying");
}
