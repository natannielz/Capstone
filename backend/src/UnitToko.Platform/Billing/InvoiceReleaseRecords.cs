using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.InvoiceReleaseBson;

namespace UnitToko.Platform.Billing;

internal sealed record InvoiceReleaseHistory(BsonDocument Head, ImmutableArray<BsonDocument> Events,
    ImmutableArray<BsonDocument> Audits, ImmutableArray<BsonDocument> Receipts, ImmutableArray<BsonDocument> Outboxes)
{
    internal BsonDocument Latest => Events[^1];
    internal BsonDocument Proposal => Latest["action"] == "propose" ? Latest : Events[^2];
}

internal sealed partial class InvoiceReleaseRecords(IMongoDatabase database)
{
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static void Equal(BsonValue actual, BsonValue expected) { if (!CatalogSchemas.SemanticEquals(actual, expected)) throw B.Broken(); }
    internal static async Task FenceFrozenAsync(IMongoDatabase db, IClientSessionHandle tx, string collection, BsonDocument expected, CancellationToken ct)
    {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, db.Client)) throw B.Broken();
        var rows = db.GetCollection<BsonDocument>(collection);
        if ((await rows.DeleteOneAsync(tx, expected, cancellationToken: ct)).DeletedCount != 1) throw B.Changed();
        await rows.InsertOneAsync(tx, expected, cancellationToken: ct);
    }
    internal static async Task RequireStorageAsync(IMongoDatabase db, CancellationToken ct)
    {
        await RequireEffectStorageAsync(db, ct);
        foreach (var name in InvoiceReleaseSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(db, name, ct) ?? throw B.Broken();
            var keys = InvoiceReleaseSchemas.Keys(name);
            var indexes = await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (!AccountContactStorage.SafeDefinition(definition, InvoiceReleaseSchemas.Validator(name)) ||
                !AccountContactStorage.SafeIndexes(indexes, keys) || keys.Any(key => !indexes.Any(index => AccountContactStorage.Matches(index, key)))) throw B.Broken();
        }
    }
    internal static async Task RequireEffectStorageAsync(IMongoDatabase db, CancellationToken ct) {
        foreach (var name in new[] { "auditEvents", "idempotencyRecords", "outboxEvents" }) {
            var definition = await AccountContactStorage.DefinitionAsync(db, name, ct) ?? throw B.Broken();
            var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") ||
                options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") || options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw B.Broken();
            PaymentEvidenceMigration.RequireIndexes(name, await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct));
        }
    }
    private async Task<BsonDocument> ExactAsync(IClientSessionHandle tx, string table, BsonValue id, CancellationToken ct) =>
        await Rows(table).Find(tx, new BsonDocument { { "_id", id }, { "$and", new BsonArray { InvoiceReleaseSchemas.Validator(table) } } })
            .SingleOrDefaultAsync(ct) ?? throw B.Broken();

    internal async Task<InvoiceReleaseHistory?> SelectionAsync(IClientSessionHandle tx, IEnumerable<InvoiceReceiptSelection> selected, CancellationToken ct)
    {
        var key = B.SelectionKey(selected);
        var head = await Rows(B.Heads).Find(tx, new BsonDocument("selectionKey", key)).SingleOrDefaultAsync(ct);
        if (head is not null) { Equal(head["receipts"], B.Selected(selected)); return await ReadAsync(tx, head["_id"].AsInt64, ct); }
        foreach (var (table, field) in new[] { (B.Events, "selectionKey"), ("auditEvents", "result.selectionKey"),
            ("idempotencyRecords", "result.selectionKey"), ("outboxEvents", "payload.selectionKey") })
            if (await Rows(table).Find(tx, EffectsFilter(table, field, key)).AnyAsync(ct)) throw B.Broken();
        return null;
    }

    internal async Task<InvoiceReleaseHistory> ReadAsync(IClientSessionHandle tx, long releaseId, CancellationToken ct)
    {
        var head = await ExactAsync(tx, B.Heads, new BsonInt64(releaseId), ct);
        Equal(head["selectionKey"], B.SelectionKey(B.Selected(head["receipts"])));
        var events = ImmutableArray.CreateBuilder<BsonDocument>(); var audits = ImmutableArray.CreateBuilder<BsonDocument>();
        var receipts = ImmutableArray.CreateBuilder<BsonDocument>(); var outboxes = ImmutableArray.CreateBuilder<BsonDocument>();
        BsonDocument? previous = null; BsonDocument? proposal = null; long revision = 0;
        using var cursor = await Rows(B.Events).Find(tx, new BsonDocument("releaseId", releaseId)).Sort(new BsonDocument("revision", 1)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var stored in cursor.Current) {
            var row = await ExactAsync(tx, B.Events, stored["_id"], ct); var input = B.Input(row["input"]);
            Equal(row["recordHash"], B.Hash(row)); Equal(row["input"], B.Input(input)); Equal(row["selectionKey"], head["selectionKey"]);
            Equal(B.Selected(input.Receipts), head["receipts"]); Equal(row["scopes"], head["scopes"]);
            Equal(row["revision"], ++revision); Equal(row["previousRevision"], revision - 1);
            Equal(row["previousHash"], previous is null ? BsonNull.Value : previous["recordHash"]);
            if (previous is not null && row["recordedAt"].CompareTo(previous["recordedAt"]) < 0) throw B.Broken();
            var action = row["action"].AsString; Equal(row["operation"], B.Prefix + action);
            var people = row["people"].AsBsonArray.Select(B.Person).ToArray();
            if (people.Any(person => person.Verification != PersonVerification.Verified || person.PersonId is not > 0 || person.Version is not > 0) ||
                people.Select(person => person.UserId).Distinct().Count() != people.Length) throw B.Broken();
            if (action == "propose") {
                if (previous?["action"] == "propose" || people.Length != 1 || people[0].UserId != row["actorId"].AsInt64) throw B.Broken();
                Equal(row["proposalId"], row["_id"]); Equal(input.ExpectedReleaseRevision, row["previousRevision"]);
                Equal(row["binding"], B.ProposalBinding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, input, row["source"].AsBsonDocument));
                proposal = row;
            } else {
                if (action is not ("approve" or "reject") || previous is null || previous["action"] != "propose" || proposal is null) throw B.Broken();
                Equal(row["proposalId"], proposal["_id"]); Equal(row["input"], proposal["input"]);
                Equal(row["binding"], B.DecisionBinding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, proposal,
                    action == "approve" ? InvoiceReleaseDecision.Approve : InvoiceReleaseDecision.Reject, row["source"].AsBsonDocument));
                if (action == "approve") Equal(row["source"], proposal["source"]);
                var original = B.Person(proposal["people"][0]); var maker = people.SingleOrDefault(person => person.UserId == original.UserId);
                var checker = people.SingleOrDefault(person => person.UserId == row["actorId"].AsInt64);
                if (people.Length != 2 || maker is null || checker is null || !PersonIndependencePolicy.AreIndependent(checker, maker, original)) throw B.Broken();
            }
            Equal(row["bodyHash"], AuthorizationStore.CommandHash(B.Body(row), row["reason"].AsString));
            var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            Equal(audit, B.Audit(row));
            var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument {
                { "actorId", row["actorId"] }, { "operation", row["operation"] }, { "key", row["requestKey"] }
            }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            string[] fields = ["_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result"];
            if (action != "propose") {
                fields = [.. fields, PersonApprovalReceipt.Field];
                if (PersonApprovalReceipt.Read(receipt, B.ApprovePermission) != new ApprovalRequest(row["previousRevision"].AsInt64,
                    row["previousRevision"].AsInt64, ProposedBy: proposal!["actorId"].AsInt64)) throw B.Broken();
            }
            if (receipt.ElementCount != fields.Length || fields.Any(field => !receipt.Contains(field)) || !receipt["_id"].IsObjectId ||
                receipt["schemaVersion"] != 1 || !receipt["createdAt"].IsBsonDateTime || receipt["createdAt"].CompareTo(row["recordedAt"]) > 0) throw B.Broken();
            Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row));
            var outbox = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.SourceKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            Equal(outbox["kind"], row["operation"]); Equal(outbox["actorId"], row["actorId"]); Equal(outbox["requestKey"], row["requestKey"]);
            Equal(outbox["createdAt"], row["recordedAt"]); Equal(outbox["payload"], B.Result(row));
            events.Add(row); audits.Add(audit); receipts.Add(receipt); outboxes.Add(outbox); previous = row;
        }
        if (previous is null) throw B.Broken();
        Equal(head["revision"], revision); Equal(head["eventId"], previous["_id"]); Equal(head["eventHash"], previous["recordHash"]);
        Equal(head["state"], previous["action"]); Equal(head["updatedAt"], previous["recordedAt"]); Equal(head["createdAt"], events[0]["recordedAt"]);
        foreach (var (table, field) in new[] { ("auditEvents", "result.releaseId"), ("idempotencyRecords", "result.releaseId"), ("outboxEvents", "payload.releaseId") })
            if (await Rows(table).CountDocumentsAsync(tx, EffectsFilter(table, field, releaseId), cancellationToken: ct) != revision) throw B.Broken();
        return new(head, events.ToImmutable(), audits.ToImmutable(), receipts.ToImmutable(), outboxes.ToImmutable());
    }
    private static BsonDocument EffectsFilter(string table, string field, BsonValue value) {
        var filter = new BsonDocument(field, value);
        if (table != B.Events) filter.Add(table == "idempotencyRecords" ? "operation" : "kind", new BsonDocument("$in",
            new BsonArray { B.Prefix + "propose", B.Prefix + "approve", B.Prefix + "reject" }));
        return filter;
    }

    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string operation, string key, CancellationToken ct)
    {
        var row = await Rows(B.Events).Find(tx, new BsonDocument { { "actorId", actor }, { "operation", operation }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is not null) {
            var history = await ReadAsync(tx, row["releaseId"].AsInt64, ct);
            return history.Events.SingleOrDefault(value => value["_id"] == row["_id"]) ?? throw B.Broken();
        }
        foreach (var (table, filter) in new (string, BsonDocument)[] {
            ("idempotencyRecords", new() { { "actorId", actor }, { "operation", operation }, { "key", key } }),
            ("auditEvents", new() { { "actorId", actor }, { "kind", operation }, { "requestKey", key } }),
            ("outboxEvents", new() { { "actorId", actor }, { "kind", operation }, { "requestKey", key } }) })
            if (await Rows(table).Find(tx, filter).AnyAsync(ct)) throw B.Broken();
        return null;
    }

    // Caller first authenticates the source through its owning record graph. Hold
    // those exact original effects through the remaining financial awaits, too.
    internal static async Task FenceOriginalCommandAsync(IMongoDatabase database, IClientSessionHandle tx, BsonDocument source, CancellationToken ct) {
        foreach (var (name, filter) in new (string, BsonDocument)[] {
            ("auditEvents", new("_id", source["auditId"])),
            ("idempotencyRecords", new() { { "actorId", source["actorId"] }, { "operation", source["operation"] }, { "key", source["requestKey"] } }),
            ("outboxEvents", new() { { "actorId", source["actorId"] }, { "kind", source["operation"] }, { "requestKey", source["requestKey"] } }) }) {
            var row = await database.GetCollection<BsonDocument>(name).Find(tx, filter).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            if (name == "idempotencyRecords") Equal(row["bodyHash"], source["bodyHash"]);
            await FenceFrozenAsync(database, tx, name, row, ct);
        }
    }
    internal async Task FenceAsync(IClientSessionHandle tx, InvoiceReleaseHistory history, CancellationToken ct)
    {
        var actual = await ReadAsync(tx, history.Head["_id"].AsInt64, ct);
        // A monotonic owner fence is the only mutable field ignored by the captured
        // history comparison. Any new proposal or decision invalidates the capture.
        Equal(B.Hash(actual.Head), B.Hash(history.Head));
        foreach (var (name, captured) in new[] { (B.Events, history.Events), ("auditEvents", history.Audits),
            ("idempotencyRecords", history.Receipts), ("outboxEvents", history.Outboxes) })
            foreach (var row in captured) await FenceFrozenAsync(database, tx, name, row, ct);
        if (actual.Head["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(B.Heads).UpdateOneAsync(tx, actual.Head,
            Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
    }
}
