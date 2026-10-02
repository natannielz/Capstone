using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using B = UnitToko.Platform.Billing.RefundPayoutBson;

namespace UnitToko.Platform.Mongo.Migrations;
internal sealed class RefundPayoutMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 55;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var installs = new List<(string Name, bool Exists, BsonDocument Validator, BsonDocument[] Missing)>();
        foreach (var name in RefundPayoutSchemas.Collections) {
            await lease.RenewAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad();
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad();
            var keys = RefundPayoutSchemas.Keys(name);
            var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                    index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !keys.Any(key => index["key"].Equals(key))) ||
                definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Bad();
            var missing = new List<BsonDocument>();
            for (var index = 0; index < keys.Length; index++) {
                var key = keys[index]; var expected = "ux_" + name + "_" + index;
                var matching = indexes.Where(value => value["key"].Equals(key) || value["name"] == expected).ToArray();
                if (matching.Any(value => !value["key"].Equals(key) || !value.GetValue("unique", false).AsBoolean)) throw Bad();
                if (matching.Length == 0) missing.Add(key);
            }
            var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument; var required = RefundPayoutSchemas.Validator(name);
            installs.Add((name, definition is not null, current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current :
                new BsonDocument("$and", new BsonArray { current, required }), missing.ToArray()));
        }
        // Empty recording targets cannot adopt orphan operations, private pins or outgoing journals.
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"), ("outboxEvents", "sourceKey") })
            if (await database.GetCollection<BsonDocument>(table).WithReadConcern(ReadConcern.Majority)
                .Find(new BsonDocument(field, Prefix("billing.refund.payout"))).Limit(1).AnyAsync(ct)) throw Bad();
        if (await database.GetCollection<BsonDocument>("journalEntries").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument("source.namespace", new BsonRegularExpression(@"^billing\.refund(?:[.:-]|$)"))).Limit(1).AnyAsync(ct)) throw Bad();
        if (await database.GetCollection<BsonDocument>("evidencePins").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument("associationKind", "RefundPayout")).Limit(1).AnyAsync(ct)) throw Bad();
        var evidence = await RefundPayoutEvidenceMigration.PreflightAsync(database, lease, ct);
        await ReturnFinancialApprovalService.PreflightPayoutApprovalsAsync(database, clock, lease, ct);
        await lease.RenewAsync(ct);
        using (var tx = await database.Client.StartSessionAsync(cancellationToken: ct)) {
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
            await BankReceiptService.PreflightPayoutBanksAsync(database, clock, tx, ct);
            await tx.AbortTransactionAsync(ct);
        }
        // All actual source graphs and every replacement have passed before the first DDL.
        await RefundPayoutEvidenceMigration.ApplyAsync(database, evidence, lease, ct);
        foreach (var item in installs) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { item.Exists ? "collMod" : "create", item.Name }, { "validator", item.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in item.Missing) await database.GetCollection<BsonDocument>(item.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = true, Name = "ux_" + item.Name + "_" +
                    Array.FindIndex(RefundPayoutSchemas.Keys(item.Name), expected => expected.Equals(key)) }), cancellationToken: ct);
        }
    }
    private static BsonRegularExpression Prefix(string operation) => new("^" + Regex.Escape(operation) + @"(?:[.:]|$)");
    private static InvalidOperationException Bad() => new("Refund payout recording migration preflight failed");
}
