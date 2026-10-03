using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Inventory;
using B = UnitToko.Platform.Billing.BankReceiptBson;
using D = UnitToko.Platform.Billing.RefundSettlementBson;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed partial class ProcurementDisbursementMigration
{
    // M56 intentionally admitted only incoming claims before refunds existed. This successor
    // proves both established v1 owners without changing either their bytes or that old factory.
    private async Task PreflightRetainedBankClaimsAsync(IClientSessionHandle tx, MigrationLease lease, CancellationToken ct) {
        try { await RequireRetainedBankClaimsAsync(tx, lease, ct); }
        catch (PersistenceConflictException) { throw Bad(); }
        catch (AuthorizationDeniedException) { throw Bad(); }
    }
    private async Task RequireRetainedBankClaimsAsync(IClientSessionHandle tx, MigrationLease lease, CancellationToken ct) {
        BankTransactionClaims.RequireTransaction(database, tx);
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
        foreach (var name in RefundSettlementSchemas.Collections) {
            await InventoryRepository.RequireStorageAsync(database, name, RefundSettlementSchemas.Validator(name), RefundSettlementSchemas.Keys(name), ct);
            if (await Rows(name).Find(tx, new BsonDocument("$nor", new BsonArray { RefundSettlementSchemas.Validator(name) })).AnyAsync(ct)) throw Bad();
        }
        var claims = new BankTransactionClaims(database);
        var receipts = new Dictionary<long, BsonDocument>(); var settlements = new Dictionary<long, BsonDocument>();
        var identities = new HashSet<string>(StringComparer.Ordinal); var journals = new HashSet<long>();
        using (var cursor = await Rows(B.Receipts).Find(tx, new BsonDocument()).ToCursorAsync(ct))
            while (await cursor.MoveNextAsync(ct)) foreach (var raw in cursor.Current) {
                await lease.RenewAsync(ct);
                var row = await BankReceiptRecords.ReadReceiptHistoryAsync(database, tx, raw["_id"].AsInt64, ct);
                await claims.RequireCreditAsync(tx, row, ct);
                if (!identities.Add(row["canonicalIdentity"].AsString) || !journals.Add(row["journalId"].AsInt64)) throw Bad();
                receipts.Add(row["_id"].AsInt64, row);
            }
        var confirmationIds = new HashSet<long>();
        using (var cursor = await Rows(B.Confirmations).Find(tx, new BsonDocument()).ToCursorAsync(ct))
            while (await cursor.MoveNextAsync(ct)) foreach (var raw in cursor.Current) {
                await lease.RenewAsync(ct);
                var confirmation = await BankReceiptRecords.ReadConfirmationAsync(database, tx, raw["_id"].AsInt64, ct);
                if (!receipts.TryGetValue(confirmation["receiptId"].AsInt64, out var receipt)) throw Bad();
                var import = await new BankStatementRecords(database).ImportAsync(tx, confirmation["importId"].AsInt64, ct);
                var line = import["preview"]["rows"].AsBsonArray.SingleOrDefault(value => value["number"] == confirmation["rowNumber"])?.AsBsonDocument ?? throw Bad();
                var source = new BankStatementReceiptSource(import, line, null);
                BankReceiptRecords.ConfirmationSource(confirmation, source); BankReceiptRecords.SameTransfer(receipt, source);
                if (confirmation["createdReceipt"].AsBoolean != (receipt["confirmationId"] == confirmation["_id"]) || receipt["recordedAt"].CompareTo(confirmation["recordedAt"]) > 0) throw Bad();
                confirmationIds.Add(confirmation["_id"].AsInt64);
            }
        using (var cursor = await Rows(D.Events).Find(tx, new BsonDocument()).ToCursorAsync(ct))
            while (await cursor.MoveNextAsync(ct)) foreach (var raw in cursor.Current) {
                await lease.RenewAsync(ct);
                var row = await RefundSettlementRecords.ReadAsync(database, tx, raw["_id"].AsInt64, ct, clock);
                if (!identities.Add(row["canonicalIdentity"].AsString) || !journals.Add(row["journalId"].AsInt64)) throw Bad();
                settlements.Add(row["_id"].AsInt64, row);
            }
        // Forward owner proofs alone miss deleted owners. Count all reserved effects and
        // journals, and reject unknown mutations while retaining legitimate bank read audits.
        foreach (var (operation, root, count, journalCount) in new[] {
            (B.Operation, "billing.bank-receipt", confirmationIds.Count, receipts.Count), (D.Operation, D.Operation, settlements.Count, settlements.Count) }) {
            var prefix = new BsonRegularExpression("^" + System.Text.RegularExpressions.Regex.Escape(root) + @"(?:[.:]|$)");
            foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                if (await Rows(table).CountDocumentsAsync(tx, new BsonDocument(field, operation), cancellationToken: ct) != count) throw Bad();
            if (await Rows("auditEvents").CountDocumentsAsync(tx, new BsonDocument("$or", new BsonArray { new BsonDocument("kind", operation), new BsonDocument("operation", operation) }), cancellationToken: ct) != count ||
                await Rows("outboxEvents").CountDocumentsAsync(tx, new BsonDocument("$or", new BsonArray { new BsonDocument("kind", operation), new BsonDocument("sourceKey", prefix) }), cancellationToken: ct) != count ||
                await Rows("journalEntries").CountDocumentsAsync(tx, new BsonDocument("source.namespace", root), cancellationToken: ct) != journalCount) throw Bad();
            foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("outboxEvents", "kind"), ("journalEntries", "source.namespace") })
                if (await Rows(table).Find(tx, new BsonDocument("$and", new BsonArray { new BsonDocument(field, prefix), new BsonDocument(field, new BsonDocument("$ne", table == "journalEntries" ? root : operation)) })).AnyAsync(ct)) throw Bad();
            var allowed = new BsonArray { operation }; if (operation == B.Operation) allowed.Add(B.Prefix + "read");
            if (await Rows("auditEvents").Find(tx, new BsonDocument("$and", new BsonArray {
                new BsonDocument("$or", new BsonArray { new BsonDocument("kind", prefix), new BsonDocument("operation", prefix) }),
                new BsonDocument("kind", new BsonDocument("$nin", allowed)) })).AnyAsync(ct)) throw Bad();
        }
        await claims.RequireNoOrphanSettlementAsync(tx, ct);
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
                foreach (var field in new[] { "physicalIdentity", "identityNamespace" }) BankReceiptRecords.Equal(anchor[field], first[field]);
                BankReceiptRecords.Equal(anchor["createdAt"], first["recordedAt"]);
            }
        if (await Rows(BankTransactionClaims.Collection).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct) != identities.Count) throw Bad();
        using (var cursor = await Rows(BankTransactionClaims.Collection).Find(tx, new BsonDocument()).ToCursorAsync(ct))
            while (await cursor.MoveNextAsync(ct)) foreach (var raw in cursor.Current) {
                await lease.RenewAsync(ct);
                var claim = await claims.ReadAsync(tx, raw["_id"].AsString, ct) ?? throw Bad();
                var owners = claim["direction"] == "credit" ? receipts : settlements;
                if (!owners.TryGetValue(claim["ownerId"].AsInt64, out var owner)) throw Bad();
                BankReceiptRecords.Equal(claim, claim["direction"] == "credit" ? BankTransactionClaims.Credit(owner) : BankTransactionClaims.Debit(owner));
            }
        foreach (var (name, maximum) in new[] { (B.Receipts, receipts.Keys.DefaultIfEmpty().Max()), (B.Confirmations, confirmationIds.DefaultIfEmpty().Max()),
            (PaymentAllocationBson.Events, settlements.Keys.DefaultIfEmpty().Max()) }) {
            var counter = await Rows("idCounters").Find(tx, new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is null ? maximum > 0 : !counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 < maximum || value.AsInt64 is < 0 or >= long.MaxValue - 1) throw Bad();
        }
        // Refund settlements deliberately share paymentAllocations' event sequence.
        if (await Rows("idCounters").Find(tx, new BsonDocument("_id", D.Events)).AnyAsync(ct)) throw Bad();
    }
}
