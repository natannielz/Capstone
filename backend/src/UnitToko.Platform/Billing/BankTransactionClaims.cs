using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Billing.BankReceiptBson;

namespace UnitToko.Platform.Billing;

// Internal deduplication keys are hashes, not public document IDs. This owner binds a
// financial record; it never authenticates a bank channel or authorizes a settlement.
internal sealed class BankTransactionClaims(IMongoDatabase database)
{
    internal const string Collection = "bankTransactionClaims", Settlements = "refundSettlements", SettlementOperation = "billing.refund.payout.verify";
    internal static void RequireTransaction(IMongoDatabase database, IClientSessionHandle tx) {
        if (tx is null || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
    }
    internal static BsonDocument Credit(BsonDocument receipt) {
        if (receipt["snapshotHash"].IsBsonNull || receipt["snapshotHash"] != B.Hash(receipt) || receipt["postingFence"] != 1L ||
            receipt["journalId"].IsBsonNull || receipt["journalFingerprint"].IsBsonNull) throw B.Broken();
        var row = new BsonDocument {
            { "_id", receipt["canonicalIdentity"] }, { "schemaVersion", 1 }, { "canonicalIdentity", receipt["canonicalIdentity"] },
            { "direction", "credit" }, { "ownerId", receipt["_id"] }, { "ownerHash", receipt["snapshotHash"] }
        };
        foreach (var name in new[] { "scope", "physicalIdentity", "identityNamespace", "transactionId", "bookedDate", "amount", "importId", "rowNumber", "importHash", "rowHash", "actorId", "recordedAt" })
            row.Add(name, receipt[name].DeepClone());
        row.Add("snapshotHash", B.Hash(row)); return row;
    }
    internal async Task<BsonDocument?> ReadAsync(IClientSessionHandle tx, string canonicalIdentity, CancellationToken ct) {
        RequireTransaction(database, tx);
        if (canonicalIdentity is not { Length: 64 } || canonicalIdentity.Any(c => !char.IsAsciiHexDigitLower(c))) throw B.Broken();
        var rows = await database.GetCollection<BsonDocument>(Collection).Find(tx, new BsonDocument("$or", new BsonArray {
            new BsonDocument("_id", canonicalIdentity), new BsonDocument("canonicalIdentity", canonicalIdentity) })).Limit(2).ToListAsync(ct);
        if (rows.Count == 0) return null;
        if (rows.Count != 1) throw B.Broken();
        var row = await database.GetCollection<BsonDocument>(Collection).Find(tx, new BsonDocument("$and", new BsonArray {
            new BsonDocument("_id", canonicalIdentity), ProcurementBankClaimSchemas.Validator() })).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (row["snapshotHash"] != B.Hash(row) || row["canonicalIdentity"] != BankStatementParser.Candidate(row["physicalIdentity"].AsString,
            row["identityNamespace"].AsString, row["transactionId"].AsString)) throw B.Broken();
        _ = InvoiceBson.Date(row["bookedDate"]);
        if (row["direction"] == "credit") {
            var receipt = await database.GetCollection<BsonDocument>(B.Receipts).Find(tx, new BsonDocument("_id", row["ownerId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            BankReceiptRecords.Equal(row, Credit(receipt));
        } else if (row["schemaVersion"] == 1) {
            var owners = await database.GetCollection<BsonDocument>(Settlements).Find(tx, new BsonDocument("canonicalIdentity", canonicalIdentity)).Limit(2).ToListAsync(ct);
            if (owners.Count != 1 || owners[0]["_id"] != row["ownerId"]) throw B.Broken(); var settlement = owners[0];
            BankReceiptRecords.Equal(row, Debit(settlement));
            await BankReceiptService.ReadSettlementDebitHistoryAsync(database, tx, settlement["debit"].AsBsonDocument, ct);
        } else {
            if (row["ownerKind"] != ProcurementSettlementBson.ClaimOwner) throw B.Broken();
            var owners = await database.GetCollection<BsonDocument>(ProcurementSettlementBson.Events).Find(tx, new BsonDocument("canonicalIdentity", canonicalIdentity)).Limit(2).ToListAsync(ct);
            if (owners.Count != 1 || owners[0]["_id"] != row["ownerId"]) throw B.Broken();
            BankReceiptRecords.Equal(row, ProcurementSettlementBson.Claim(owners[0]));
            await BankReceiptService.ReadSettlementDebitHistoryAsync(database, tx, owners[0]["debit"].AsBsonDocument, ct);
        }
        return row;
    }
    internal async Task RequireCreditAsync(IClientSessionHandle tx, BsonDocument receipt, CancellationToken ct) {
        var actual = await ReadAsync(tx, receipt["canonicalIdentity"].AsString, ct) ?? throw B.Broken();
        BankReceiptRecords.Equal(actual, Credit(receipt));
        if (await database.GetCollection<BsonDocument>(Settlements).Find(tx, new BsonDocument("canonicalIdentity", receipt["canonicalIdentity"])).AnyAsync(ct) ||
            await database.GetCollection<BsonDocument>(ProcurementSettlementBson.Events).Find(tx, new BsonDocument("canonicalIdentity", receipt["canonicalIdentity"])).AnyAsync(ct)) throw B.Broken();
    }
    internal async Task ClaimCreditAsync(IClientSessionHandle tx, BsonDocument receipt, CancellationToken ct) {
        RequireTransaction(database, tx);
        await RequireUnclaimedAsync(tx, receipt["canonicalIdentity"].AsString, ct);
        var persisted = await database.GetCollection<BsonDocument>(B.Receipts).Find(tx, new BsonDocument("_id", receipt["_id"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        BankReceiptRecords.Equal(persisted, receipt);
        await database.GetCollection<BsonDocument>(Collection).InsertOneAsync(tx, Credit(receipt), cancellationToken: ct);
    }
    internal async Task RequireUnclaimedAsync(IClientSessionHandle tx, string canonicalIdentity, CancellationToken ct) {
        if (await ReadAsync(tx, canonicalIdentity, ct) is not null) throw B.Changed("bank_transaction_already_claimed");
        if (await database.GetCollection<BsonDocument>(Settlements).Find(tx, new BsonDocument("canonicalIdentity", canonicalIdentity)).AnyAsync(ct) ||
            await database.GetCollection<BsonDocument>(ProcurementSettlementBson.Events).Find(tx, new BsonDocument("canonicalIdentity", canonicalIdentity)).AnyAsync(ct)) throw B.Broken();
    }
    // Admission runs before its command receipt is inserted. A surviving outgoing
    // effect/journal cannot be adopted after the actual settlement owner was deleted.
    // This reverse proof never invokes the aggregate financial owner or grants authority.
    internal async Task RequireNoOrphanSettlementAsync(IClientSessionHandle tx, CancellationToken ct) {
        RequireTransaction(database, tx);
        foreach (var (owner, operation) in new[] { (Settlements, SettlementOperation), (ProcurementSettlementBson.Events, ProcurementSettlementBson.Operation) })
        foreach (var (table, field, ownerField) in new[] { ("auditEvents", "kind", "result.settlementId"),
            ("idempotencyRecords", "operation", "result.settlementId"), ("outboxEvents", "kind", "payload.settlementId"),
            ("journalEntries", "source.namespace", "source.documentId") }) {
            var exactOwnerField = owner == ProcurementSettlementBson.Events ? ownerField.Replace("settlementId", "id", StringComparison.Ordinal) : ownerField;
            var orphan = await database.GetCollection<BsonDocument>(table).Aggregate<BsonDocument>(tx, new BsonDocument[] {
                new("$match", new BsonDocument(field, operation)),
                new("$lookup", new BsonDocument { { "from", owner }, { "localField", exactOwnerField }, { "foreignField", "_id" }, { "as", "retainedSettlement" } }),
                new("$match", new BsonDocument("retainedSettlement", new BsonDocument("$size", 0))), new("$limit", 1)
            }).ToListAsync(ct);
            if (orphan.Count != 0) throw B.Broken();
        }
    }
    internal async Task RequireNoOrphanCreditAsync(IClientSessionHandle tx, string canonicalIdentity, CancellationToken ct) {
        RequireTransaction(database, tx);
        using var imports = await database.GetCollection<BsonDocument>(BankStatementBson.Imports)
            .Find(tx, new BsonDocument("preview.rows.identityCandidate", canonicalIdentity)).ToCursorAsync(ct);
        while (await imports.MoveNextAsync(ct)) foreach (var import in imports.Current)
            foreach (var line in import["preview"]["rows"].AsBsonArray.Where(line => line["identityCandidate"] == canonicalIdentity)) {
                foreach (var (table, operation, result) in new[] { ("auditEvents", "kind", "result"), ("idempotencyRecords", "operation", "result"), ("outboxEvents", "kind", "payload") })
                    if (await database.GetCollection<BsonDocument>(table).Find(tx, new BsonDocument {
                        { operation, B.Operation }, { result + ".importId", import["_id"] }, { result + ".rowNumber", line["number"] }
                    }).AnyAsync(ct)) throw B.Broken();
            }
        foreach (var (table, operation, ownerField) in new[] { ("auditEvents", "kind", "result.confirmationId"), ("idempotencyRecords", "operation", "result.confirmationId"), ("outboxEvents", "kind", "payload.confirmationId") }) {
            var orphanEffects = await database.GetCollection<BsonDocument>(table).Aggregate<BsonDocument>(tx, new BsonDocument[] {
                new("$match", new BsonDocument(operation, B.Operation)),
                new("$lookup", new BsonDocument { { "from", B.Confirmations }, { "localField", ownerField }, { "foreignField", "_id" }, { "as", "retainedConfirmation" } }),
                new("$match", new BsonDocument("retainedConfirmation", new BsonDocument("$size", 0))), new("$limit", 1)
            }).ToListAsync(ct); if (orphanEffects.Count != 0) throw B.Broken();
        }
        // A retained cash journal can never be adopted by a new source when its owner was
        // deleted, even if the original confirmation/effects were deleted with it.
        var orphans = await database.GetCollection<BsonDocument>("journalEntries").Aggregate<BsonDocument>(tx, new BsonDocument[] {
            new("$match", new BsonDocument("source.namespace", "billing.bank-receipt")),
            new("$lookup", new BsonDocument { { "from", B.Receipts }, { "localField", "source.documentId" }, { "foreignField", "_id" }, { "as", "retainedOwner" } }),
            new("$match", new BsonDocument("retainedOwner", new BsonDocument("$size", 0))), new("$limit", 1)
        }).ToListAsync(ct);
        if (orphans.Count != 0) throw B.Broken();
    }
    internal static BsonDocument Debit(BsonDocument settlement) {
        if (settlement["snapshotHash"] != RefundSettlementBson.Hash(settlement) || settlement["_id"].AsInt64 <= 0 ||
            settlement["payoutId"].AsInt64 <= 0 || settlement["financialApprovalId"].AsInt64 <= 0 ||
            settlement["operation"] != SettlementOperation || !settlement["recordedAt"].IsBsonDateTime ||
            settlement["postingFence"] != 1L || settlement["journalId"].IsBsonNull || settlement["journalFingerprint"].IsBsonNull) throw B.Broken();
        var debit = settlement["debit"].AsBsonDocument;
        BankReceiptRecords.Equal(debit["snapshotHash"], B.Hash(debit)); BankReceiptRecords.Equal(settlement["canonicalIdentity"], debit["canonicalIdentity"]);
        var row = new BsonDocument { { "_id", debit["canonicalIdentity"] }, { "schemaVersion", 1 }, { "canonicalIdentity", debit["canonicalIdentity"] },
            { "direction", "debit" }, { "ownerId", settlement["_id"] }, { "ownerHash", settlement["snapshotHash"] },
            { "actorId", settlement["actorId"] }, { "recordedAt", settlement["recordedAt"] } };
        foreach (var name in new[] { "scope", "physicalIdentity", "identityNamespace", "transactionId", "bookedDate", "amount", "importId", "rowNumber", "importHash", "rowHash" })
            row.Add(name, debit[name].DeepClone());
        row.Add("snapshotHash", B.Hash(row)); return row;
    }
    internal async Task RequireDebitAsync(IClientSessionHandle tx, BsonDocument settlement, CancellationToken ct) {
        var claim = await ReadAsync(tx, settlement["canonicalIdentity"].AsString, ct) ?? throw B.Broken();
        BankReceiptRecords.Equal(claim, Debit(settlement));
        if (await database.GetCollection<BsonDocument>(B.Receipts).Find(tx, new BsonDocument("canonicalIdentity", settlement["canonicalIdentity"])).AnyAsync(ct) ||
            await database.GetCollection<BsonDocument>(B.Confirmations).Find(tx, new BsonDocument("canonicalIdentity", settlement["canonicalIdentity"])).AnyAsync(ct) ||
            await database.GetCollection<BsonDocument>(ProcurementSettlementBson.Events).Find(tx, new BsonDocument("canonicalIdentity", settlement["canonicalIdentity"])).AnyAsync(ct)) throw B.Broken();
    }
    internal async Task RequireProcurementDebitAsync(IClientSessionHandle tx, BsonDocument settlement, CancellationToken ct) {
        var claim = await ReadAsync(tx, settlement["canonicalIdentity"].AsString, ct) ?? throw B.Broken();
        BankReceiptRecords.Equal(claim, ProcurementSettlementBson.Claim(settlement));
        foreach (var name in new[] { B.Receipts, B.Confirmations, Settlements })
            if (await database.GetCollection<BsonDocument>(name).Find(tx, new BsonDocument("canonicalIdentity", settlement["canonicalIdentity"])).AnyAsync(ct)) throw B.Broken();
    }
}
