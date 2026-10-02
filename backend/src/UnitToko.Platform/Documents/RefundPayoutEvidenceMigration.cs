using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Documents;

internal sealed record RefundPayoutEvidenceReplacement(string Name, BsonDocument Previous, BsonDocument Validator);
internal static class RefundPayoutEvidenceMigration
{
    private static readonly string[] Sources = [.. EvidenceSchemas.Collections, .. EvidenceAccessSchemas.Collections,
        .. EvidenceValidationSchemas.Collections, .. PaymentSubmissionSchemas.Collections];
    internal static async Task<IReadOnlyList<RefundPayoutEvidenceReplacement>> PreflightAsync(IMongoDatabase database, MigrationLease lease, CancellationToken ct) {
        var replacements = new List<RefundPayoutEvidenceReplacement>();
        foreach (var name in Sources) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition is null || definition.GetValue("type", "") != "collection" || options is null ||
                options.GetValue("validationLevel", "") != "strict" || options.GetValue("validationAction", "") != "error" ||
                options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad(name);
            var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var required = Previous(name);
            var next = RefundPayoutEvidenceSchemas.Collections.Contains(name) ? RefundPayoutEvidenceSchemas.Extend(name, current) : current;
            if (!RefundPayoutEvidenceSchemas.Supports(name, current, required)) throw Bad(name);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            // Even a resumed definition cannot admit pre-cutover outgoing evidence or pins.
            foreach (var validator in new[] { current, required })
                if (await rows.Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Bad(name);
            RequireIndexes(name, await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct));
            if (RefundPayoutEvidenceSchemas.Collections.Contains(name)) replacements.Add(new(name, current.DeepClone().AsBsonDocument, next));
        }
        await RefundPayoutEvidenceAdmissionGraph.ValidateAsync(database, lease, ct);
        await RequireHistoriesAsync(database, lease, ct);
        await RequireCountersAsync(database, lease, ct);
        return replacements;
    }
    internal static async Task ApplyAsync(IMongoDatabase database, IReadOnlyList<RefundPayoutEvidenceReplacement> replacements, MigrationLease lease, CancellationToken ct) {
        if (replacements.Count != RefundPayoutEvidenceSchemas.Collections.Length || replacements.Select(value => value.Name).Distinct().Count() != replacements.Count ||
            replacements.Any(value => !RefundPayoutEvidenceSchemas.Collections.Contains(value.Name) ||
                !CatalogSchemas.SemanticEquals(value.Validator, RefundPayoutEvidenceSchemas.Extend(value.Name, value.Previous)))) throw Bad("replacement plan");
        foreach (var item in replacements) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", item.Name) }, ct)).SingleAsync(ct);
            if (!CatalogSchemas.SemanticEquals(definition["options"]["validator"], item.Previous)) throw Bad(item.Name);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", item.Name }, { "validator", item.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        }
    }
    private static BsonDocument Previous(string name) => RefundPayoutEvidenceSchemas.Collections.Contains(name) ? RefundPayoutEvidenceSchemas.PreviousValidator(name) :
        EvidenceSchemas.Collections.Contains(name) ? EvidenceSchemas.Validator(name) : EvidenceAccessSchemas.Collections.Contains(name) ? EvidenceAccessSchemas.Validator(name) :
        EvidenceValidationSchemas.Collections.Contains(name) ? EvidenceValidationSchemas.Validator(name) : PaymentSubmissionSchemas.Validator(name);
    private static void RequireIndexes(string name, IReadOnlyList<BsonDocument> indexes) {
        if (EvidenceSchemas.Collections.Contains(name)) { PaymentEvidenceMigration.RequireIndexes(name, indexes); return; }
        var key = EvidenceAccessSchemas.Collections.Contains(name) ? EvidenceAccessSchemas.Key(name) :
            EvidenceValidationSchemas.Collections.Contains(name) ? EvidenceValidationSchemas.Key(name) : PaymentSubmissionSchemas.Key(name);
        if (!indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1))) ||
            !indexes.Any(index => index["key"].Equals(key) && index.GetValue("unique", false).AsBoolean) ||
            indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !index["key"].Equals(key))) throw Bad(name);
    }
    private static async Task RequireHistoriesAsync(IMongoDatabase database, MigrationLease lease, CancellationToken ct) {
        var client = database.Client as MongoClient ?? throw Bad("unsupported native database client");
        var documents = new EvidenceService(database, new(client), new(database), new SystemClock());
        async Task Each(string name, Func<IClientSessionHandle, BsonDocument, Task> check) =>
            await EvidenceMigration.EachAsync(database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority), new BsonDocument(), async row => {
                using var tx = await database.Client.StartSessionAsync(cancellationToken: ct);
                tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
                await check(tx, row); await tx.CommitTransactionAsync(ct);
            }, lease, ct);
        await Each("evidenceUploadReservations", (tx, row) => documents.ValidateHistoricalAdmissionAsync(tx, row["_id"].AsInt64, ct));
        await Each("evidenceReadLeases", (tx, row) => documents.ValidateHistoricalLeaseAsync(tx, row, ct));
        await Each("evidenceLifecycles", (tx, row) => documents.ValidateHistoricalLifecycleAsync(tx, row, ct));
        await Each("evidencePins", async (tx, row) => {
            if (row["associationKind"] != "PaymentSubmission") throw Bad("outgoing pin before acceptance");
            var pin = await EvidencePinRecords.ReadAsync(database, tx, row["_id"].AsInt64, ct);
            await documents.ValidateHistoricalPinAsync(tx, pin, ct);
            var submitted = await new PaymentSubmissionRecords(database).ReadAsync(tx, row["associationId"].AsInt64, ct);
            if (!pin.Equals(row) || submitted["pinId"] != row["_id"]) throw Bad("pin source");
        });
        await Each("paymentSubmissions", async (tx, row) => { _ = await new PaymentSubmissionRecords(database).ReadAsync(tx, row["_id"].AsInt64, ct); });
        foreach (var (name, parent, ownField, parentField) in new[] {
            ("evidencePinAudits", "evidencePins", "pinId", "_id"), ("evidenceReadAudits", "evidenceReadLeases", "leaseId", "_id"),
            ("evidenceLifecycleAudits", "evidenceLifecycles", "versionRowId", "_id"), ("evidenceValidationAudits", "evidenceValidationResults", "versionRowId", "_id") })
            await Each(name, async (tx, row) => {
                if (!await database.GetCollection<BsonDocument>(parent).Find(tx, new BsonDocument(parentField, row[ownField])).AnyAsync(ct)) throw Bad(name);
            });
        await Each("evidenceValidationResults", async (tx, row) => {
            await documents.ValidateHistoricalAdmissionAsync(tx, row["source"]["reservationId"].AsInt64, ct);
            var reservation = await database.GetCollection<BsonDocument>("evidenceUploadReservations").Find(tx, new BsonDocument("_id", row["source"]["reservationId"])).SingleAsync(ct);
            if (reservation["versionRowId"] != row["_id"]) throw Bad("validation source");
        });
    }
    private static async Task RequireCountersAsync(IMongoDatabase database, MigrationLease lease, CancellationToken ct) {
        var counters = new List<(string Name, string[] Collections)> { ("evidenceIds", ["evidenceLineages", "documentVersions", "evidenceUploadReservations", "evidenceUploadAttempts"]) };
        foreach (var name in new[] { "evidenceStorePolicies", "auditEvents", "evidenceReadPolicies", "evidenceReadLeases", "evidenceReadAudits", "evidenceLifecycleAudits",
            "evidenceValidationPolicies", "evidenceValidationAudits", "paymentSubmissions", "evidencePins", "evidencePinAudits" }) counters.Add((name, [name]));
        foreach (var item in counters) {
            await lease.RenewAsync(ct); long maximum = 0;
            foreach (var name in item.Collections) {
                var row = await database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Limit(1).SingleOrDefaultAsync(ct);
                if (row is not null) { if (!row["_id"].IsInt64 || row["_id"].AsInt64 is <= 0 or long.MaxValue) throw Bad(name); maximum = Math.Max(maximum, row["_id"].AsInt64); }
            }
            var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", item.Name)).SingleOrDefaultAsync(ct);
            if (counter is null ? maximum > 0 : !counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 < maximum || value.AsInt64 is < 0 or long.MaxValue) throw Bad(item.Name);
        }
    }
    private static InvalidOperationException Bad(string name) => new("Refund payout evidence migration preflight failed: " + name);
}
