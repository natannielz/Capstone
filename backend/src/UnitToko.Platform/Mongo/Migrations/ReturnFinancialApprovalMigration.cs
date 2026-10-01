using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.RegularExpressions;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;
using E = UnitToko.Platform.Billing.ReturnEntitlementBson;
using D = UnitToko.Platform.Fulfillment.AfterSalesDecisionRecords;
using I = UnitToko.Platform.Inventory.ReturnInspectionRecords;
using P = UnitToko.Platform.Fulfillment.ReturnResolutionRecords;
using Q = UnitToko.Platform.Inventory.ReturnQuarantineLedger;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;

namespace UnitToko.Platform.Mongo.Migrations;
internal sealed class ReturnFinancialApprovalMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 54;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var installs = new List<(string Name, bool Exists, BsonDocument Validator, BsonDocument[] Missing)>();
        foreach (var name in ReturnFinancialApprovalSchemas.Collections) {
            await lease.RenewAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad();
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad();
            var keys = ReturnFinancialApprovalSchemas.Keys(name);
            var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                    index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !keys.Any(key => index["key"].Equals(key))) ||
                definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Bad();
            var missing = new List<BsonDocument>();
            for (var i = 0; i < keys.Length; i++) {
                var key = keys[i]; var expected = "ux_" + name + "_" + i;
                var matching = indexes.Where(index => index["key"].Equals(key) || index["name"] == expected).ToArray();
                if (matching.Any(index => !index["key"].Equals(key) || !index.GetValue("unique", false).AsBoolean)) throw Bad();
                if (matching.Length == 0) missing.Add(key);
            }
            var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument; var required = ReturnFinancialApprovalSchemas.Validator(name);
            installs.Add((name, definition is not null, current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current :
                new BsonDocument("$and", new BsonArray { current, required }), missing.ToArray()));
        }
        // New approval targets must be empty. Surviving financial effects cannot be adopted as history.
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"), ("outboxEvents", "sourceKey") })
            if (await database.GetCollection<BsonDocument>(table).WithReadConcern(ReadConcern.Majority)
                .Find(new BsonDocument(field, Prefix(A.Operation))).Limit(1).AnyAsync(ct)) throw Bad();
        if (await database.GetCollection<BsonDocument>("journalEntries").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument("source.namespace", "billing.return-financial")).Limit(1).AnyAsync(ct)) throw Bad();
        await ReturnEntitlementStorage.RequireAsync(database, ct);
        await PaymentAllocationStorage.RequireAsync(database, ct);
        foreach (var (name, validator, keys) in new[] {
            (R.Collection, AfterSalesCaseSchemas.Validator(), new[] { AfterSalesCaseSchemas.Key() }),
            (D.Collection, AfterSalesDecisionSchemas.Validator(), new[] { AfterSalesDecisionSchemas.Key() }),
            (Q.Collection, ReturnQuarantineSchemas.Validator(), new[] { ReturnQuarantineSchemas.Key(), ReturnQuarantineSchemas.RequestKey() }),
            (I.Collection, ReturnInspectionSchemas.Validator(), new[] { ReturnInspectionSchemas.Key() }),
            (P.Collection, ReturnResolutionSchemas.Validator(), new[] { ReturnResolutionSchemas.Key() }) })
            await InventoryRepository.RequireStorageAsync(database, name, validator, keys, ct);
        await ReturnEntitlementMigration.PreflightSourcesAsync(database, clock, lease, ct);
        await PreflightRequestsAsync(database, clock, lease, ct);
        foreach (var item in installs) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { item.Exists ? "collMod" : "create", item.Name }, { "validator", item.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in item.Missing) await database.GetCollection<BsonDocument>(item.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = true, Name = "ux_" + item.Name + "_" +
                    Array.FindIndex(ReturnFinancialApprovalSchemas.Keys(item.Name), expected => expected.Equals(key)) }), cancellationToken: ct);
        }
    }
    internal static async Task PreflightRequestsAsync(IMongoDatabase database, IClock clock, MigrationLease lease, CancellationToken ct) {
        using var tx = await database.Client.StartSessionAsync(cancellationToken: ct);
        tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        var records = new ReturnEntitlementRecords(database, clock); var invoices = new Dictionary<long, (BsonDocument Money, BsonArray Issued)>(); var count = 0L;
        using var cursor = await database.GetCollection<BsonDocument>(E.Requests).Find(tx, new BsonDocument()).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var candidate in cursor.Current) {
            await lease.RenewAsync(ct); var row = await records.RawAsync(tx, candidate["_id"].AsInt64, ct); var invoiceId = row["invoiceId"].AsInt64;
            if (!invoices.ContainsKey(invoiceId)) {
                var invoice = await database.GetCollection<BsonDocument>(InvoiceBson.Invoices).Find(tx, new BsonDocument("_id", invoiceId)).SingleOrDefaultAsync(ct) ?? throw Bad();
                await InvoiceRecords.VerifyPersistedAsync(database, tx, invoice, ct);
                invoices.Add(invoiceId, (await InvoiceService.AllocationSnapshotAsync(database, tx, invoice, ct), E.IssuedLines(invoice)));
            }
            await ValidateRequestSourceAsync(database, clock, tx, row, ct); count = checked(count + 1);
        }
        foreach (var source in invoices.Values) {
            await lease.RenewAsync(ct);
            _ = await records.PoolAsync(tx, source.Money, source.Issued, row => ValidateRequestSourceAsync(database, clock, tx, row, ct), ct);
        }
        if (await database.GetCollection<BsonDocument>(E.Pools).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct) != invoices.Count) throw Bad();
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"), ("outboxEvents", "sourceKey") })
            if (await database.GetCollection<BsonDocument>(table).CountDocumentsAsync(tx, new BsonDocument(field, Prefix(E.Operation)), cancellationToken: ct) != count) throw Bad();
        await tx.AbortTransactionAsync(ct);
    }
    private static async Task ValidateRequestSourceAsync(IMongoDatabase database, IClock clock, IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        var source = row["source"].AsBsonDocument;
        var root = await new R(database).ReadAsync(tx, row["caseId"].AsInt64, ct);
        var decision = await new D(database).ReadAsync(tx, root, ct) ?? throw Bad();
        if (decision["state"] != "return_authorized") throw Bad();
        var receipt = await database.GetCollection<BsonDocument>(Q.Collection).Find(tx, new BsonDocument { { "_id", row["_id"] }, { "caseId", root["_id"] } }).SingleOrDefaultAsync(ct) ?? throw Bad();
        var inspection = await new I(database, clock).ReadAsync(tx, receipt, ct) ?? throw Bad();
        var proposal = await new P(database, clock).ReadAsync(tx, inspection, ct) ?? throw Bad();
        if (proposal["input"]["action"] != row["action"] || proposal["revision"] != 1L || proposal["state"] != "resolution_requested" ||
            inspection["input"]["lines"].AsBsonArray.Any(line => line["identity"] == "different") ||
            decision["decidedAt"].CompareTo(root["requestedAt"]) < 0 || receipt["source"]["decidedAt"] != decision["decidedAt"] ||
            receipt["receivedAt"].CompareTo(decision["decidedAt"]) < 0 || inspection["inspectedAt"].CompareTo(receipt["receivedAt"]) < 0 ||
            proposal["requestedAt"].CompareTo(inspection["inspectedAt"]) < 0 || row["requestedAt"].CompareTo(proposal["requestedAt"]) < 0) throw Bad();
        foreach (var (field, value) in new (string, BsonValue)[] { ("caseHash", root["snapshotHash"]), ("custodyHash", receipt["snapshotHash"]),
            ("inspectionHash", inspection["snapshotHash"]), ("proposalHash", proposal["snapshotHash"]), ("requestedBy", root["requestedBy"]),
            ("receivedBy", receipt["actorId"]), ("inspectedBy", inspection["actorId"]), ("proposedBy", proposal["actorId"]),
            ("inspectionLines", P.Source(inspection)["lines"]) }) Equal(source[field], value);
        Equal(source["participants"], new BsonArray(new[] { root["requestedBy"].AsInt64, decision["actorId"].AsInt64, receipt["actorId"].AsInt64,
            inspection["actorId"].AsInt64, proposal["actorId"].AsInt64 }.Distinct().Order()));
        var invoice = await database.GetCollection<BsonDocument>(InvoiceBson.Invoices).Find(tx, new BsonDocument("_id", row["invoiceId"])).SingleOrDefaultAsync(ct) ?? throw Bad();
        await InvoiceRecords.VerifyPersistedAsync(database, tx, invoice, ct);
        Equal(root["source"], InvoiceService.CaseSnapshot(invoice));
        Equal(source["invoice"], await InvoiceService.AllocationSnapshotAsync(database, tx, invoice, ct)); Equal(source["issuedLines"], E.IssuedLines(invoice));
        var journal = await database.GetCollection<BsonDocument>("journalEntries").Find(tx, new BsonDocument("_id", invoice["journalId"])).SingleOrDefaultAsync(ct) ?? throw Bad();
        var posted = AccountingBson.ReadDraft(journal); var terms = InvoiceBson.Terms(invoice["terms"].AsBsonDocument); var accounts = new BsonArray();
        foreach (var (component, mapping) in new[] { ("total", terms.ReceivableMapping), ("net", terms.RevenueMapping), ("tax", terms.TaxMapping), ("charges", terms.ChargesMapping) }) {
            if (InvoiceBson.Money(invoice["totals"][component]).Value == 0) continue;
            var line = posted.Lines.Single(value => value.MappingKey == mapping);
            accounts.Add(new BsonDocument { { "component", component }, { "mappingKey", mapping }, { "accountId", line.AccountId }, { "accountCode", line.AccountCode } });
        }
        Equal(source["originalAccounts"], accounts);
    }
    private static void Equal(BsonValue left, BsonValue right) { if (!InvoiceRecords.Equal(left, right)) throw Bad(); }
    private static BsonRegularExpression Prefix(string operation) => new("^" + Regex.Escape(operation) + @"(?:[.:]|$)");
    private static InvalidOperationException Bad() => new("Return financial approval migration preflight failed");
}
