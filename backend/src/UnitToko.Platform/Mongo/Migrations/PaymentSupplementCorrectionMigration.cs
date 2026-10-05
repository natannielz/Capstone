using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Mongo.Migrations;

// Additive version-only successor. No original, approval, financial owner,
// primary-image command body, existing UUID, counter or index is rewritten.
internal sealed class PaymentSupplementCorrectionMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 88;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("Payment supplementary correction migration found incompatible retained storage");
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version ||
            history.Take(Version - 1).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Invalid();
        if (database.Client is not MongoClient client) throw Invalid();
        var preserved = new Dictionary<string, BsonValue>(StringComparer.Ordinal);
        try {
            await PaymentSupplementStorage.RequireAsync(database, ct);
            await DocumentProcessingStorage.RequireAsync(database, ct);
            await PaymentVerificationStorage.RequireAsync(database, ct);
            foreach (var name in PaymentCorrectionSchemas.Collections) await PaymentSupplementCorrectionStorage.RequireAsync(database, name, ct);
            foreach (var name in PaymentSupplementSchemas.Collections.Concat(PdfDocumentExtractionSchemas.Collections)
                .Concat(PaymentSupplementEvidenceSchemas.Collections).Concat(PaymentCorrectionSchemas.Collections).Distinct(StringComparer.Ordinal)) {
                await lease.RenewAsync(ct);
                var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
                if (!definition.TryGetValue("info", out var info) || !info.IsBsonDocument || !info.AsBsonDocument.TryGetValue("uuid", out var uuid) || !uuid.IsBsonBinaryData)
                    throw Invalid();
                preserved.Add(name, uuid.DeepClone());
                if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { definition["options"]["validator"] })).Limit(1).AnyAsync(ct)) throw Invalid();
            }
            // A reviewed interrupted installation may leave the successor validator,
            // but no pre-88 writer/backfill may have produced annotated history.
            if (await Rows(PaymentCorrectionBson.Versions).Find(new BsonDocument("$or", new BsonArray {
                new BsonDocument("schemaVersion", new BsonDocument("$ne", 1)), new BsonDocument("supplementaryObservations", new BsonDocument("$exists", true)) })).Limit(1).AnyAsync(ct) ||
                await Rows("auditEvents").Find(new BsonDocument("supplementaryObservations", new BsonDocument("$exists", true))).Limit(1).AnyAsync(ct)) throw Invalid();
            await RequireCountersAsync(lease, ct);
            await RequireHistoriesAsync(client, lease, ct);
        } catch (Exception error) when (error is DomainValidationException or PersistenceConflictException or InvalidOperationException) { throw Invalid(); }
        var definitionBefore = await AccountContactStorage.DefinitionAsync(database, PaymentCorrectionBson.Versions, ct) ?? throw Invalid();
        var before = definitionBefore["options"]["validator"].AsBsonDocument;
        var after = PaymentSupplementCorrectionSchemas.Extend(before);
        if (await Rows(PaymentCorrectionBson.Versions).Find(new BsonDocument("$nor", new BsonArray { before })).Limit(1).AnyAsync(ct) ||
            await Rows(PaymentCorrectionBson.Versions).Find(new BsonDocument("$nor", new BsonArray { after })).Limit(1).AnyAsync(ct)) throw Invalid();
        // Every predecessor shape, actual immutable history, allocator and required
        // audit/receipt/result relation is checked before the only DDL operation.
        await lease.RenewAsync(ct);
        var current = await AccountContactStorage.DefinitionAsync(database, PaymentCorrectionBson.Versions, ct) ?? throw Invalid();
        if (current["options"]["validator"] != before || current["info"]["uuid"] != preserved[PaymentCorrectionBson.Versions]) throw Invalid();
        if (after != before) await database.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "collMod", PaymentCorrectionBson.Versions }, { "validator", after }, { "validationLevel", "strict" }, { "validationAction", "error" },
            { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        foreach (var pair in preserved) {
            var installed = await AccountContactStorage.DefinitionAsync(database, pair.Key, ct) ?? throw Invalid();
            if (installed["info"]["uuid"] != pair.Value || pair.Key == PaymentCorrectionBson.Versions && installed["options"]["validator"] != after) throw Invalid();
        }
        await PaymentSupplementCorrectionStorage.RequireAsync(database, PaymentCorrectionBson.Versions, ct, requireAnnotated: true);
    }
    private async Task RequireCountersAsync(MigrationLease lease, CancellationToken ct)
    {
        await EvidenceMigration.EachAsync(Rows("idCounters"), new BsonDocument(), row => {
            if (!row.TryGetValue("_id", out var id) || !id.IsString || string.IsNullOrWhiteSpace(id.AsString) ||
                !row.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue) throw Invalid();
            return Task.CompletedTask;
        }, lease, ct);
        foreach (var name in new[] { PaymentCorrectionBson.Versions, PaymentSupplementBson.Heads, PaymentSupplementBson.Versions,
            PaymentSupplementBson.Policies, "auditEvents" }.Concat(PdfDocumentExtractionSchemas.Collections)) {
            await lease.RenewAsync(ct);
            var last = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
            var maximum = last is null ? 0L : last["_id"].IsInt64 && last["_id"].AsInt64 is > 0 and < long.MaxValue ? last["_id"].AsInt64 : throw Invalid();
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is null ? maximum > 0 : !counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 < maximum || value.AsInt64 is < 0 or long.MaxValue)
                throw Invalid();
        }
    }
    private async Task RequireHistoriesAsync(MongoClient client, MigrationLease lease, CancellationToken ct)
    {
        var historical = new PaymentSupplementEvidenceHistory(database,
            new EvidenceService(database, new MongoUnitOfWork(client), new IdAllocator(database), new SystemClock()));
        var supplements = new PaymentSupplementRecords(database, historical); var corrections = new PaymentCorrectionRecords(database);
        async Task Each(string name, BsonDocument filter, Func<IClientSessionHandle, BsonDocument, Task> check) =>
            await EvidenceMigration.EachAsync(Rows(name), filter, async row => {
                using var tx = await database.Client.StartSessionAsync(cancellationToken: ct);
                tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
                await check(tx, row); await tx.CommitTransactionAsync(ct);
            }, lease, ct);
        await Each(PaymentSupplementBson.Heads, new(), async (tx, row) => { _ = await supplements.HistoryAsync(tx, row["_id"].AsInt64, ct); });
        await Each(PaymentSupplementBson.Versions, new(), async (tx, row) => {
            var owner = await supplements.HistoryAsync(tx, row["supplementId"].AsInt64, ct);
            if (!owner.Versions.Any(version => version.Equals(row))) throw Invalid();
        });
        await Each(PaymentSupplementBson.Attachments, new(), async (tx, row) => {
            var owner = await supplements.HistoryAsync(tx, row["_id"].AsInt64, ct);
            if (owner.Attachment is null || !owner.Attachment.Equals(row)) throw Invalid();
        });
        await Each("outboxEvents", new("kind", PaymentSupplementBson.JobKind), async (tx, row) => {
            _ = await new PaymentSupplementOcrSource(database, historical).RequireAsync(tx, row, ct);
        });
        async Task Correction(IClientSessionHandle tx, long id, UnitToko.Modules.Billing.PaymentCorrectionBinding binding) {
            var submission = await new PaymentSubmissionRecords(database).ReadAsync(tx, binding.SubmissionId, ct);
            if (submission["declarationId"] != id || submission["declarationRevision"] != binding.DeclarationRevision ||
                !submission["evidence"].Equals(PaymentSubmissionBson.Reference(binding.Evidence))) throw Invalid();
            var job = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", PaymentSubmissionBson.OcrKey(submission))).SingleOrDefaultAsync(ct) ?? throw Invalid();
            _ = await new PaymentOcrSource(database).RequireAsync(tx, job, ct);
            var result = await new ExtractionRecords(database).ResultAsync(tx, job, ct) ?? throw Invalid();
            if (result["_id"] != binding.ExtractionId) throw Invalid();
            var output = ExtractionRecords.Extract(result);
            if (output.Evidence != binding.Evidence) throw Invalid();
            _ = await corrections.HistoryAsync(tx, id, binding, PaymentCorrectionBson.Fields(output.Fields.Select(field => new UnitToko.Modules.Billing.PaymentReviewField(field.Field, field.Value))), ct);
        }
        await Each(PaymentCorrectionBson.Heads, new(), (tx, row) => Correction(tx, row["_id"].AsInt64, PaymentCorrectionBson.Binding(row["binding"].AsBsonDocument)));
        await Each(PaymentCorrectionBson.Versions, new(), async (tx, row) => {
            _ = await corrections.VersionAsync(tx, row["_id"].AsInt64, ct);
            await Correction(tx, row["declarationId"].AsInt64, PaymentCorrectionBson.Binding(row["binding"].AsBsonDocument));
        });
        foreach (var (table, kind, result) in new[] { ("idempotencyRecords", "operation", "result"), ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload") }) {
            var orphan = await Rows(table).Aggregate<BsonDocument>(new BsonDocument[] {
                new("$match", new BsonDocument(kind, PaymentCorrectionBson.Operation)),
                new("$lookup", new BsonDocument { { "from", PaymentCorrectionBson.Versions }, { "localField", result + ".correctionId" }, { "foreignField", "_id" }, { "as", "owner" } }),
                new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1)
            }).ToListAsync(ct);
            if (orphan.Count != 0) throw Invalid();
        }
    }
}
