using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using D = UnitToko.Platform.Billing.RefundSettlementBson;
using P = UnitToko.Platform.Billing.RefundPayoutBson;
using R = UnitToko.Platform.Billing.PaymentAllocationRecords;

namespace UnitToko.Platform.Mongo.Migrations;

// One-writer deployment runs this forward step after installed bank claims (56).
// No operation, policy, bank transfer or synthetic authorization is provisioned here.
internal sealed class RefundSettlementMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 57;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        await lease.RenewAsync(ct); await RequireHistoryAsync(ct);
        var installs = new List<(string Name, bool Exists, BsonDocument Validator, BsonDocument[] Missing)>();
        foreach (var name in RefundSettlementSchemas.Collections) {
            await lease.RenewAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad();
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad();
            var keys = RefundSettlementSchemas.Keys(name); var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                    index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !keys.Any(key => index["key"] == key)) ||
                definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"] == new BsonDocument("_id", 1))) throw Bad();
            var missing = new List<BsonDocument>();
            for (var i = 0; i < keys.Length; i++) {
                var matching = indexes.Where(index => index["key"] == keys[i] || index["name"] == "ux_" + name + "_" + i).ToArray();
                if (matching.Any(index => index["key"] != keys[i] || !index.GetValue("unique", false).AsBoolean)) throw Bad();
                if (matching.Length == 0) missing.Add(keys[i]);
            }
            var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument; var required = RefundSettlementSchemas.Validator(name);
            installs.Add((name, definition is not null, current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current :
                new BsonDocument("$and", new BsonArray { current, required }), missing.ToArray()));
        }
        // All retained predecessor storage is required, never repaired by this successor.
        foreach (var name in RefundPayoutSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, RefundPayoutSchemas.Validator(name), RefundPayoutSchemas.Keys(name), ct);
        await PaymentAllocationStorage.RequireAsync(database, ct); await PaymentAllocationStorage.RequireFinancialAsync(database, ct);
        await BankTransactionClaimSchemas.RequireStorageAsync(database, ct);
        await ReturnFinancialApprovalService.PreflightPayoutApprovalsAsync(database, clock, lease, ct);
        using (var tx = await database.Client.StartSessionAsync(cancellationToken: ct)) {
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
            await BankTransactionClaimMigration.PreflightInstalledAsync(database, tx, lease, ct);
            await PreflightPayoutsAsync(tx, lease, ct); await RequireEmptyEffectsAsync(tx, ct);
            await tx.AbortTransactionAsync(ct);
        }
        // No DDL precedes the complete real payout/private-pin/money/bank-claim proof.
        foreach (var item in installs) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { item.Exists ? "collMod" : "create", item.Name },
                { "validator", item.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in item.Missing) await database.GetCollection<BsonDocument>(item.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = true, Name = "ux_" + item.Name + "_" +
                    Array.FindIndex(RefundSettlementSchemas.Keys(item.Name), expected => expected == key) }), cancellationToken: ct);
        }
    }
    private async Task RequireHistoryAsync(CancellationToken ct) {
        var history = await database.GetCollection<BsonDocument>("schemaMigrations").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(58).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(56).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying") throw Bad();
    }
    private async Task RequireEmptyEffectsAsync(IClientSessionHandle tx, CancellationToken ct) {
        var prefix = new BsonRegularExpression("^" + Regex.Escape(D.Operation) + @"(?:[.:]|$)");
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"),
            ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("journalEntries", "source.namespace") })
            if (await database.GetCollection<BsonDocument>(table).Find(tx, new BsonDocument(field, prefix)).AnyAsync(ct)) throw Bad();
        // Settlement shares the allocation counter. An unrelated own-event counter cannot
        // silently establish an alternate event identity sequence during empty-target resume.
        if (await database.GetCollection<BsonDocument>("idCounters").Find(tx, new BsonDocument("_id", D.Events)).AnyAsync(ct)) throw Bad();
    }
    private async Task PreflightPayoutsAsync(IClientSessionHandle tx, MigrationLease lease, CancellationToken ct) {
        if (database.Client is not MongoClient client) throw Bad();
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
        var records = new RefundPayoutRecords(database, clock, new MongoUnitOfWork(client)); var money = new R(database, clock);
        var drafts = new Dictionary<long, BsonDocument>(); var payouts = new Dictionary<long, BsonDocument>();
        async Task Source(BsonDocument row, string date) {
            var approval = await money.RawApprovalAsync(tx, row["financialApprovalId"].AsInt64, ct);
            R.Equal(row["approvalHash"], approval["snapshotHash"]);
            var approving = approval["people"].AsBsonArray.Select(ReceivingAccountBson.Person).Single(person => person.UserId == approval["actorId"].AsInt64);
            if (approving.Verification != PersonVerification.Verified || approving.PersonId is null || approval["action"] != "refund") throw Bad();
            var journal = await Rows("journalEntries").Find(tx, new BsonDocument("_id", approval["journalId"])).SingleOrDefaultAsync(ct) ?? throw Bad();
            var liability = AccountingBson.ReadDraft(journal).Lines.Single(line => line.MappingKey == approval["policy"]["refundLiabilityMapping"].AsString);
            var expected = P.ApprovalSnapshot(approval, new BsonDocument { { "accountId", liability.AccountId }, { "accountCode", liability.AccountCode } });
            R.Equal(row["source"]["approval"], expected);
            // A payout retains its actual draft's immutable source. The original bank
            // observation was captured at draft creation, rather than at later recording.
            var capturedAt = row[date];
            if (row.TryGetValue("draftId", out var draftId)) {
                if (!drafts.TryGetValue(draftId.AsInt64, out var draft)) throw Bad();
                capturedAt = draft["createdAt"];
            }
            _ = await BankReceiptService.ReadPayoutBankHistoryAsync(database, tx, row["source"]["payingBank"].AsBsonDocument,
                new DateTimeOffset(capturedAt.ToUniversalTime()), ct);
        }
        foreach (var (name, date) in new[] { (P.Drafts, "createdAt"), (P.Payouts, "recordedAt") }) {
            using var cursor = await Rows(name).Find(tx, new BsonDocument()).ToCursorAsync(ct);
            while (await cursor.MoveNextAsync(ct)) foreach (var candidate in cursor.Current) {
                await lease.RenewAsync(ct);
                var row = name == P.Drafts ? await records.DraftAsync(tx, candidate["_id"].AsInt64, ct) : await records.PayoutAsync(tx, candidate["_id"].AsInt64, false, ct);
                await Source(row, date); (name == P.Drafts ? drafts : payouts).Add(row["_id"].AsInt64, row);
            }
        }
        foreach (var draft in drafts.Values) if (!draft["recordedPayoutId"].IsBsonNull &&
            (!payouts.TryGetValue(draft["recordedPayoutId"].AsInt64, out var payout) || payout["draftId"] != draft["_id"])) throw Bad();
        foreach (var (operation, count) in new[] { (P.DraftOperation, drafts.Count), (P.Operation, payouts.Count) })
            foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind") })
                if (await Rows(table).CountDocumentsAsync(tx, new BsonDocument(field, operation), cancellationToken: ct) != count) throw Bad();
        var prefix = new BsonRegularExpression(@"^billing\.refund\.payout(?:[.:]|$)");
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind") })
            if (await Rows(table).Find(tx, new BsonDocument("$and", new BsonArray { new BsonDocument(field, prefix),
                new BsonDocument(field, new BsonDocument("$nin", new BsonArray { P.DraftOperation, P.Operation })) })).AnyAsync(ct)) throw Bad();
        if (await Rows("outboxEvents").CountDocumentsAsync(tx, new BsonDocument("sourceKey", prefix), cancellationToken: ct) != drafts.Count + payouts.Count ||
            await Rows("journalEntries").Find(tx, new BsonDocument("source.namespace", new BsonRegularExpression(@"^billing\.refund(?:[.:-]|$)"))).AnyAsync(ct)) throw Bad();
        var pins = new HashSet<long>();
        using (var cursor = await Rows("evidencePins").Find(tx, new BsonDocument("associationKind", "RefundPayout")).ToCursorAsync(ct))
            while (await cursor.MoveNextAsync(ct)) foreach (var pin in cursor.Current) {
                if (!payouts.TryGetValue(pin["associationId"].AsInt64, out var payout) || payout["pinId"] != pin["_id"] || !pins.Add(pin["_id"].AsInt64)) throw Bad();
            }
        if (pins.Count != payouts.Count) throw Bad();
        foreach (var (name, maximum) in new[] { (P.Drafts, drafts.Keys.DefaultIfEmpty().Max()), (P.Payouts, payouts.Keys.DefaultIfEmpty().Max()), ("evidencePins", pins.DefaultIfEmpty().Max()) }) {
            var counter = await Rows("idCounters").Find(tx, new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is null ? maximum > 0 : !counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 < maximum || value.AsInt64 is < 0 or >= long.MaxValue - 1) throw Bad();
        }
    }
    private static InvalidOperationException Bad() => new("Refund settlement migration preflight failed");
}
