using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Inventory;
using B = UnitToko.Platform.Billing.BankReceiptBson;

namespace UnitToko.Platform.Mongo.Migrations;

// Deployment holds the one-writer pause throughout preflight, DDL and resumable
// backfill. Application admission requires this forward schema; it never repairs it.
internal sealed class BankTransactionClaimMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 56;
    internal static async Task PreflightInstalledAsync(IMongoDatabase database, IClientSessionHandle tx, MigrationLease lease, CancellationToken ct) {
        await BankTransactionClaimSchemas.RequireStorageAsync(database, ct);
        var ids = await PreflightAsync(database, tx, lease, ct);
        var claims = database.GetCollection<BsonDocument>(BankTransactionClaims.Collection);
        if (await claims.CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct) != ids.LongLength) throw Bad();
        foreach (var id in ids) {
            await lease.RenewAsync(ct);
            var receipt = await database.GetCollection<BsonDocument>(B.Receipts).Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct) ?? throw Bad();
            await new BankTransactionClaims(database).RequireCreditAsync(tx, receipt, ct);
        }
    }
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        await lease.RenewAsync(ct);
        var rows = database.GetCollection<BsonDocument>(BankTransactionClaims.Collection).WithWriteConcern(WriteConcern.WMajority);
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", BankTransactionClaims.Collection) }, ct)).SingleOrDefaultAsync(ct);
        var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
        if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
            options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
            options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad();
        var keys = BankTransactionClaimSchemas.Keys; var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
        if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !keys.Any(key => index["key"] == key)) ||
            definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"] == new BsonDocument("_id", 1))) throw Bad();
        var missing = new List<BsonDocument>();
        for (var i = 0; i < keys.Length; i++) {
            var matching = indexes.Where(index => index["key"] == keys[i] || index["name"] == "ux_" + BankTransactionClaims.Collection + "_" + i).ToArray();
            if (matching.Any(index => index["key"] != keys[i] || !index.GetValue("unique", false).AsBoolean)) throw Bad();
            if (matching.Length == 0) missing.Add(keys[i]);
        }
        var old = options.GetValue("validator", new BsonDocument()).AsBsonDocument; var required = BankTransactionClaimSchemas.Validator();
        var validator = old.ElementCount == 0 ? required : InventorySchemas.Contains(old, required) ? old : new BsonDocument("$and", new BsonArray { old, required });
        long[] receiptIds;
        using (var tx = await database.Client.StartSessionAsync(cancellationToken: ct)) {
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
            receiptIds = await PreflightAsync(database, tx, lease, ct, validator);
            await tx.AbortTransactionAsync(ct);
        }
        // No DDL or derived claims precedes the complete owning/reverse proof.
        await lease.RenewAsync(ct);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument { { definition is null ? "create" : "collMod", BankTransactionClaims.Collection },
            { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        foreach (var key in missing) await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions {
            Unique = true, Name = "ux_" + BankTransactionClaims.Collection + "_" + Array.FindIndex(keys, expected => expected == key)
        }), cancellationToken: ct);
        await BankTransactionClaimSchemas.RequireStorageAsync(database, ct);
        foreach (var id in receiptIds) {
            await lease.RenewAsync(ct);
            using var tx = await database.Client.StartSessionAsync(cancellationToken: ct);
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
            var receipt = await BankReceiptRecords.ReadReceiptHistoryAsync(database, tx, id, ct); var expected = BankTransactionClaims.Credit(receipt);
            var existing = await rows.Find(tx, new BsonDocument("_id", expected["_id"])).SingleOrDefaultAsync(ct);
            if (existing is null) await rows.InsertOneAsync(tx, expected, cancellationToken: ct);
            else BankReceiptRecords.Equal(existing, expected);
            await tx.CommitTransactionAsync(ct);
        }
        // Interrupted work resumes only from exact complete derived claims, never from a
        // weaker owner, an orphan or a rewritten predecessor receipt.
        using (var tx = await database.Client.StartSessionAsync(cancellationToken: ct)) {
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
            if (await rows.CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct) != receiptIds.LongLength) throw Bad();
            foreach (var id in receiptIds) {
                await lease.RenewAsync(ct);
                var receipt = await BankReceiptRecords.ReadReceiptHistoryAsync(database, tx, id, ct);
                await new BankTransactionClaims(database).RequireCreditAsync(tx, receipt, ct);
            }
            await tx.AbortTransactionAsync(ct);
        }
    }
    internal static async Task<long[]> PreflightAsync(IMongoDatabase database, IClientSessionHandle tx, MigrationLease lease, CancellationToken ct, BsonDocument? targetValidator = null) {
        BankTransactionClaims.RequireTransaction(database, tx);
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
        foreach (var table in BankReceiptSchemas.Collections) {
            await InventoryRepository.RequireStorageAsync(database, table, BankReceiptSchemas.Validator(table), BankReceiptSchemas.Keys(table), ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) }, ct)).SingleAsync(ct);
            if (definition.GetValue("options", new BsonDocument()).AsBsonDocument.Contains("encryptedFields")) throw Bad();
        }
        if (await Rows(BankTransactionClaims.Settlements).Find(tx, new BsonDocument()).AnyAsync(ct)) throw Bad();
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("journalEntries", "source.namespace") })
            if (await Rows(table).Find(tx, new BsonDocument(field, new BsonRegularExpression(@"^billing\.refund\.payout\.verify(?:[.:]|$)"))).AnyAsync(ct)) throw Bad();
        var receipts = new Dictionary<long, BsonDocument>(); var canonical = new HashSet<string>(StringComparer.Ordinal); var journals = new HashSet<long>();
        using (var cursor = await Rows(B.Receipts).Find(tx, new BsonDocument()).ToCursorAsync(ct))
            while (await cursor.MoveNextAsync(ct)) foreach (var candidate in cursor.Current) {
                await lease.RenewAsync(ct); var row = await BankReceiptRecords.ReadReceiptHistoryAsync(database, tx, candidate["_id"].AsInt64, ct);
                if (!canonical.Add(row["canonicalIdentity"].AsString) || !journals.Add(row["journalId"].AsInt64)) throw Bad(); receipts.Add(row["_id"].AsInt64, row);
                // Evaluate the exact successor including retained deployment guards against
                // the derived candidate before DDL, without inserting a validation probe.
                var admitted = await Rows(B.Receipts).Aggregate<BsonDocument>(tx, new BsonDocument[] {
                    new("$match", new BsonDocument("_id", row["_id"])),
                    new("$replaceRoot", new BsonDocument("newRoot", new BsonDocument("$literal", BankTransactionClaims.Credit(row)))),
                    new("$match", targetValidator ?? BankTransactionClaimSchemas.Validator())
                }).ToListAsync(ct); if (admitted.Count != 1) throw Bad();
            }
        var confirmationIds = new HashSet<long>();
        using (var cursor = await Rows(B.Confirmations).Find(tx, new BsonDocument()).ToCursorAsync(ct))
            while (await cursor.MoveNextAsync(ct)) foreach (var candidate in cursor.Current) {
                await lease.RenewAsync(ct); var confirmation = await BankReceiptRecords.ReadConfirmationAsync(database, tx, candidate["_id"].AsInt64, ct);
                if (!receipts.TryGetValue(confirmation["receiptId"].AsInt64, out var receipt)) throw Bad();
                var import = await new BankStatementRecords(database).ImportAsync(tx, confirmation["importId"].AsInt64, ct);
                var line = import["preview"]["rows"].AsBsonArray.SingleOrDefault(line => line["number"] == confirmation["rowNumber"])?.AsBsonDocument ?? throw Bad();
                var source = new BankStatementReceiptSource(import, line, null);
                BankReceiptRecords.ConfirmationSource(confirmation, source); BankReceiptRecords.SameTransfer(receipt, source);
                if (confirmation["createdReceipt"].AsBoolean != (receipt["confirmationId"] == confirmation["_id"]) ||
                    receipt["recordedAt"].ToUniversalTime() > confirmation["recordedAt"].ToUniversalTime()) throw Bad();
                confirmationIds.Add(confirmation["_id"].AsInt64);
            }
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            if (await Rows(table).CountDocumentsAsync(tx, new BsonDocument(field, B.Operation), cancellationToken: ct) != confirmationIds.Count) throw Bad();
        var prefix = new BsonRegularExpression(@"^billing\.bank-receipt(?:[.:]|$)");
        if (await Rows("auditEvents").CountDocumentsAsync(tx, new BsonDocument("$or", new BsonArray {
                new BsonDocument("kind", B.Operation), new BsonDocument("operation", B.Operation) }), cancellationToken: ct) != confirmationIds.Count ||
            await Rows("outboxEvents").CountDocumentsAsync(tx, new BsonDocument("$or", new BsonArray {
                new BsonDocument("kind", B.Operation), new BsonDocument("sourceKey", prefix) }), cancellationToken: ct) != confirmationIds.Count) throw Bad();
        foreach (var (table, filter) in new[] {
            ("idempotencyRecords", new BsonDocument("$and", new BsonArray { new BsonDocument("operation", prefix), new BsonDocument("operation", new BsonDocument("$ne", B.Operation)) })),
            ("outboxEvents", new BsonDocument("$and", new BsonArray { new BsonDocument("kind", prefix), new BsonDocument("kind", new BsonDocument("$ne", B.Operation)) }))
        }) if (await Rows(table).Find(tx, filter).AnyAsync(ct)) throw Bad();
        // Read audit rows are legitimate; every other reserved bank mutation must be the
        // exact confirmed operation already accounted for above.
        if (await Rows("auditEvents").Find(tx, new BsonDocument("$and", new BsonArray {
            new BsonDocument("$or", new BsonArray { new BsonDocument("kind", prefix), new BsonDocument("operation", prefix) }),
            new BsonDocument("kind", new BsonDocument("$nin", new BsonArray { B.Operation, B.Prefix + "read" })) })).AnyAsync(ct)) throw Bad();
        if (await Rows("journalEntries").CountDocumentsAsync(tx, new BsonDocument("source.namespace", "billing.bank-receipt"), cancellationToken: ct) != receipts.Count) throw Bad();
        // Reverse generic journal effects as well: a deleted original journal must not
        // turn its surviving posted audit/outbox into an admissible bank predecessor.
        foreach (var (table, field) in new[] { ("accountingAuditEvents", "journalId"), ("outboxEvents", "payload.journalId") }) {
            var orphan = await Rows(table).Aggregate<BsonDocument>(tx, new BsonDocument[] {
                new("$match", new BsonDocument("kind", "journal.posted")),
                new("$lookup", new BsonDocument { { "from", "journalEntries" }, { "localField", field }, { "foreignField", "_id" }, { "as", "retainedJournal" } }),
                new("$match", new BsonDocument("retainedJournal", new BsonDocument("$size", 0))), new("$limit", 1)
            }).ToListAsync(ct); if (orphan.Count != 0) throw Bad();
        }
        using (var cursor = await Rows(B.Accounts).Find(tx, new BsonDocument()).ToCursorAsync(ct))
            while (await cursor.MoveNextAsync(ct)) foreach (var anchor in cursor.Current) {
                if (!receipts.TryGetValue(anchor["firstReceiptId"].AsInt64, out var first)) throw Bad();
                BankReceiptRecords.Equal(anchor["snapshotHash"], B.Hash(anchor)); BankReceiptRecords.Equal(anchor["scope"], first["scope"]);
                BankReceiptRecords.Equal(anchor["physicalIdentity"], first["physicalIdentity"]); BankReceiptRecords.Equal(anchor["identityNamespace"], first["identityNamespace"]);
                BankReceiptRecords.Equal(anchor["createdAt"], first["recordedAt"]);
            }
        using (var cursor = await Rows(BankTransactionClaims.Collection).Find(tx, new BsonDocument()).ToCursorAsync(ct))
            while (await cursor.MoveNextAsync(ct)) foreach (var claim in cursor.Current) {
                if (claim["direction"] != "credit" || !receipts.TryGetValue(claim["ownerId"].AsInt64, out var receipt)) throw Bad();
                BankReceiptRecords.Equal(claim, BankTransactionClaims.Credit(receipt));
                if (!await Rows(BankTransactionClaims.Collection).Find(tx, new BsonDocument("$and", new BsonArray {
                    new BsonDocument("_id", claim["_id"]), BankTransactionClaimSchemas.Validator() })).AnyAsync(ct)) throw Bad();
            }
        foreach (var (table, maximum) in new[] { (B.Receipts, receipts.Keys.DefaultIfEmpty().Max()), (B.Confirmations, confirmationIds.DefaultIfEmpty().Max()) }) {
            var counter = await Rows("idCounters").Find(tx, new BsonDocument("_id", table)).SingleOrDefaultAsync(ct);
            if (counter is null ? maximum > 0 : !counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 < maximum || value.AsInt64 is < 0 or >= long.MaxValue - 1) throw Bad();
        }
        return receipts.Keys.Order().ToArray();
    }
    private static InvalidOperationException Bad() => new("Bank transaction claim migration preflight failed");
}
