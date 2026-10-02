using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Billing.BankReceiptBson;

namespace UnitToko.Platform.Billing;

// A captured bank source binds a claim to an actual command transaction. It does not
// authorize the financial command, its person separation, or any money movement.
internal sealed record RefundSettlementClaimSource(RefundSettlementBankSource BankSource, BsonDocument Debit,
    Action RequireCurrent, Func<CancellationToken, Task> FenceAsync, Func<BsonDocument, CancellationToken, Task> ClaimAsync);
internal sealed record RefundSettlementHistoricalBankSource(BsonDocument Import, BsonDocument Row, BsonDocument Debit,
    RefundPayoutBankSource PayingBank, Action RequireCurrent, Func<CancellationToken, Task> FenceAsync)
{
    internal BsonDocument CashAccount => PayingBank.CashAccount;
}

public sealed partial class BankReceiptService
{
    internal async Task<RefundSettlementHistoricalBankSource> SettlementHistorySourceAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, BsonDocument settlement, CancellationToken ct) {
        BankTransactionClaims.RequireTransaction(database, tx);
        if (actor.UserId != authority.UserId || actor.GrantVersion != authority.GrantVersion) throw B.Broken();
        var debit = settlement["debit"].AsBsonDocument; Selection(debit["importId"].AsInt64, debit["rowNumber"].AsInt32);
        _ = Require(new(actor, authority, tx, false)); await StorageAsync(ct);
        var actual = await SourceAsync(new(actor, authority, tx, false), debit["importId"].AsInt64, debit["rowNumber"].AsInt32, false, ct);
        var context = actual.Context; var frozenDebit = debit.DeepClone().AsBsonDocument;
        BankReceiptRecords.Equal(frozenDebit, SettlementDebitSnapshot(actual.Source.Import, actual.Source.Row));
        var claims = new BankTransactionClaims(database); await claims.RequireDebitAsync(tx, settlement, ct);
        var accountOwner = new ReceivingAccountService(database, new(database.Client as MongoClient ?? throw B.Broken()), ids, clock);
        var account = await accountOwner.ReadSourceAsync(actor, authority, tx, debit["accountId"].AsInt64, debit["accountVersionId"].AsInt64, false, ct);
        var frozen = await PayoutBankRowsAsync(database, clock, tx, debit["accountId"].AsInt64, debit["accountVersionId"].AsInt64, ct, current: false);
        BankReceiptRecords.Equal(frozen.Account, PayoutBankStable(account));
        var import = SettlementStable(actual.Source.Import); var row = actual.Source.Row.DeepClone().AsBsonDocument;
        var exposedImport = import.DeepClone().AsBsonDocument; var exposedRow = row.DeepClone().AsBsonDocument; var exposedDebit = frozenDebit.DeepClone().AsBsonDocument;
        var owner = SettlementStable(settlement); RefundPayoutBankSource paying = null!;
        void Current() {
            BankTransactionClaims.RequireTransaction(database, tx); _ = Require(context);
            BankReceiptRecords.Equal(exposedImport, import); BankReceiptRecords.Equal(exposedRow, row); BankReceiptRecords.Equal(exposedDebit, frozenDebit);
            if (paying is not null) {
                BankReceiptRecords.Equal(paying.Anchor, frozen.Anchor); BankReceiptRecords.Equal(paying.Account, frozen.Account);
                BankReceiptRecords.Equal(paying.AccountHead, frozen.AccountHead); BankReceiptRecords.Equal(paying.FirstReceipt, frozen.Receipt);
                BankReceiptRecords.Equal(paying.CashJournal, frozen.Journal); BankReceiptRecords.Equal(paying.Scope, frozen.Account["scope"]);
                BankReceiptRecords.Equal(paying.Book, frozen.Book); BankReceiptRecords.Equal(paying.CashAccount, frozen.CashAccount);
                BankReceiptRecords.Equal(paying.IdentityNamespace, frozen.Anchor["identityNamespace"]);
            }
            ct.ThrowIfCancellationRequested();
        }
        async Task Fence(CancellationToken token) {
            Current(); var selected = await SourceAsync(context, debit["importId"].AsInt64, debit["rowNumber"].AsInt32, false, token);
            BankReceiptRecords.Equal(SettlementStable(selected.Source.Import), import); BankReceiptRecords.Equal(selected.Source.Row, row);
            var actualOwner = await records.Rows(BankTransactionClaims.Settlements).Find(tx, new BsonDocument("_id", settlement["_id"])).SingleOrDefaultAsync(token) ?? throw B.Broken();
            BankReceiptRecords.Equal(SettlementStable(actualOwner), owner); await claims.RequireDebitAsync(tx, actualOwner, token);
            var selectedAccount = await accountOwner.ReadSourceAsync(actor, authority, tx, debit["accountId"].AsInt64, debit["accountVersionId"].AsInt64, false, token);
            BankReceiptRecords.Equal(PayoutBankStable(selectedAccount), frozen.Account);
            EqualPayoutBank(await PayoutBankRowsAsync(database, clock, tx, debit["accountId"].AsInt64, debit["accountVersionId"].AsInt64, token, current: false), frozen);
            var anchor = await records.AnchorAsync(tx, frozen.Anchor["physicalIdentity"].AsString, token) ?? throw B.Broken();
            var first = await records.ReceiptAsync(actor, authority, tx, frozen.Receipt["_id"].AsInt64, token);
            BankReceiptRecords.Equal(PayoutBankStable(anchor), frozen.Anchor); BankReceiptRecords.Equal(PayoutBankStable(first), frozen.Receipt);
            await records.TouchAsync(tx, B.Accounts, anchor, token); await records.TouchAsync(tx, B.Receipts, first, token);
            await FenceAsync(context, token); Current(); token.ThrowIfCancellationRequested();
        }
        paying = new(frozen.Anchor.DeepClone().AsBsonDocument, frozen.Account.DeepClone().AsBsonDocument, frozen.AccountHead.DeepClone().AsBsonDocument,
            frozen.Receipt.DeepClone().AsBsonDocument, frozen.Journal.DeepClone().AsBsonDocument, frozen.Account["scope"].DeepClone().AsBsonDocument,
            frozen.Book.DeepClone().AsBsonDocument, frozen.CashAccount.DeepClone().AsBsonDocument, frozen.Anchor["identityNamespace"].AsString, Current, Fence);
        Current(); return new(exposedImport, exposedRow, exposedDebit, paying, Current, Fence);
    }
    internal async Task<RefundSettlementClaimSource> CaptureSettlementClaimAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long accountId, long accountVersionId, long importId, int rowNumber, CancellationToken ct) {
        BankTransactionClaims.RequireTransaction(database, tx);
        var source = await SettlementDebitSourceAsync(actor, authority, tx, accountId, accountVersionId, importId, rowNumber, ct);
        var claims = new BankTransactionClaims(database); var canonical = source.Row["identityCandidate"].AsString;
        await claims.RequireNoOrphanSettlementAsync(tx, ct);
        await claims.RequireUnclaimedAsync(tx, canonical, ct); await claims.RequireNoOrphanCreditAsync(tx, canonical, ct);
        var frozen = SettlementDebitSnapshot(source.Import, source.Row); var exposed = frozen.DeepClone().AsBsonDocument;
        BsonDocument? claimed = null;
        void Current() {
            BankTransactionClaims.RequireTransaction(database, tx); source.RequireCurrent(); BankReceiptRecords.Equal(exposed, frozen);
            if (actor.UserId != authority.UserId || actor.GrantVersion != authority.GrantVersion) throw B.Broken();
            ct.ThrowIfCancellationRequested();
        }
        async Task Fence(CancellationToken token) {
            Current(); await source.FenceAsync(token);
            if (claimed is null) await claims.RequireUnclaimedAsync(tx, canonical, token);
            else await claims.RequireDebitAsync(tx, claimed, token);
            Current(); token.ThrowIfCancellationRequested();
        }
        async Task Claim(BsonDocument settlement, CancellationToken token) {
            Current(); if (claimed is not null) throw B.Broken();
            BankReceiptRecords.Equal(settlement["debit"], frozen); BankReceiptRecords.Equal(settlement["canonicalIdentity"], canonical);
            BankReceiptRecords.Equal(settlement["actorId"], actor.UserId); BankReceiptRecords.Equal(settlement["actorGrantVersion"], actor.GrantVersion);
            if (settlement["recordedAt"].ToUniversalTime() < source.Import["recordedAt"].ToUniversalTime()) throw B.Broken();
            var expected = BankTransactionClaims.Debit(settlement);
            var owners = await records.Rows(BankTransactionClaims.Settlements).Find(tx, new BsonDocument("canonicalIdentity", canonical)).Limit(2).ToListAsync(token);
            if (owners.Count != 1) throw B.Broken(); BankReceiptRecords.Equal(owners[0], settlement);
            if (await claims.ReadAsync(tx, canonical, token) is not null) throw B.Changed("bank_transaction_already_claimed");
            if (await records.Rows(B.Receipts).Find(tx, new BsonDocument("canonicalIdentity", canonical)).AnyAsync(token) ||
                await records.Rows(B.Confirmations).Find(tx, new BsonDocument("canonicalIdentity", canonical)).AnyAsync(token)) throw B.Broken();
            await claims.RequireNoOrphanCreditAsync(tx, canonical, token);
            await source.FenceAsync(token); Current();
            await records.Rows(BankTransactionClaims.Collection).InsertOneAsync(tx, expected, cancellationToken: token);
            claimed = settlement.DeepClone().AsBsonDocument; await claims.RequireDebitAsync(tx, claimed, token); Current();
        }
        Current(); return new(source, exposed, Current, Fence, Claim);
    }
    private static BsonDocument SettlementDebitSnapshot(BsonDocument import, BsonDocument row) {
        var account = import["account"]; var result = new BsonDocument {
            { "importId", import["_id"] }, { "importHash", import["snapshotHash"] }, { "rowNumber", row["number"] }, { "rowHash", B.Hash(row) },
            { "accountId", account["accountId"] }, { "accountVersionId", account["_id"] }, { "physicalIdentity", account["physicalIdentity"] },
            { "identityNamespace", import["profile"]["format"]["identityNamespace"] }, { "transactionId", row["transactionId"] },
            { "canonicalIdentity", row["identityCandidate"] }, { "bookedDate", row["bookedDate"] }, { "amount", row["amount"] },
            { "importedBy", import["actorId"] }, { "scope", import["scope"].DeepClone() }
        };
        result.Add("snapshotHash", B.Hash(result)); return result;
    }
    internal static async Task<BsonDocument> ReadPayoutBankHistoryAsync(IMongoDatabase database, IClientSessionHandle tx,
        BsonDocument compact, DateTimeOffset capturedAt, CancellationToken ct) {
        BankTransactionClaims.RequireTransaction(database, tx);
        var accounts = new ReceivingAccountRecords(database); var head = await accounts.HeadAsync(tx, compact["accountId"].AsInt64, ct);
        var account = await accounts.RowAsync(tx, ReceivingAccountBson.Versions, compact["versionId"].AsInt64, ct); ReceivingAccountRecords.Hash(account);
        BankReceiptRecords.Equal(account["accountId"], head["_id"]); BankReceiptRecords.Equal(account["scope"], head["scope"]);
        if (account["effectiveAt"].ToUniversalTime() > capturedAt.UtcDateTime) throw B.Broken();
        // Only these three permanent version facts change the hashed head. HeadAsync
        // proves the root and all real approval/effect history before reconstruction.
        var historicalHead = head.DeepClone().AsBsonDocument; historicalHead["version"] = account["version"];
        historicalHead["versionId"] = account["_id"]; historicalHead["updatedAt"] = account["effectiveAt"];
        var anchor = await database.GetCollection<BsonDocument>(B.Accounts).Find(tx, new BsonDocument("physicalIdentity", account["physicalIdentity"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        BankReceiptRecords.Equal(anchor["snapshotHash"], B.Hash(anchor)); BankReceiptRecords.Equal(anchor["scope"], account["scope"]);
        var first = await BankReceiptRecords.ReadReceiptHistoryAsync(database, tx, anchor["firstReceiptId"].AsInt64, ct);
        await new BankTransactionClaims(database).RequireCreditAsync(tx, first, ct);
        if (first["recordedAt"].ToUniversalTime() > capturedAt.UtcDateTime) throw B.Broken();
        var journal = await database.GetCollection<BsonDocument>("journalEntries").Find(tx, new BsonDocument("_id", first["journalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (journal["createdAt"].ToUniversalTime() > capturedAt.UtcDateTime) throw B.Broken();
        var draft = AccountingBson.ReadDraft(journal); var cash = draft.Lines.SingleOrDefault(line => line.MappingKey == first["policy"]["cashMapping"].AsString) ?? throw B.Broken();
        if (cash.Debit != InvoiceBson.Money(first["amount"]) || cash.Credit.Value != 0 || account["details"]["currency"] != draft.Book.Currency) throw B.Broken();
        var expected = new BsonDocument {
            { "accountId", account["accountId"] }, { "versionId", account["_id"] }, { "headHash", B.Hash(historicalHead) }, { "versionHash", account["snapshotHash"] },
            { "physicalIdentity", account["physicalIdentity"] }, { "identityNamespace", anchor["identityNamespace"] }, { "anchorHash", anchor["snapshotHash"] },
            { "firstReceiptId", first["_id"] }, { "firstReceiptHash", first["snapshotHash"] }, { "cashJournalId", journal["_id"] }, { "cashJournalFingerprint", journal["fingerprint"] },
            { "scope", account["scope"].DeepClone() }, { "book", AccountingBson.Key(draft.Book) }, { "cashAccountId", cash.AccountId },
            { "cashAccountCode", cash.AccountCode }, { "currency", account["details"]["currency"] }
        };
        BankReceiptRecords.Equal(compact, expected);
        return new() { { "mappingKey", cash.MappingKey }, { "accountId", cash.AccountId }, { "accountCode", cash.AccountCode } };
    }
    // Historical bank proof, without fabricated current actors. Current application reads
    // must first obtain their real scoped source admission; the financial owner also
    // reconstructs its own immutable settlement, journal, effects and person receipt.
    internal static async Task<BsonDocument> ReadSettlementDebitHistoryAsync(IMongoDatabase database, IClientSessionHandle tx, BsonDocument debit, CancellationToken ct) {
        BankTransactionClaims.RequireTransaction(database, tx); BankReceiptRecords.Equal(debit["snapshotHash"], B.Hash(debit));
        var import = await new BankStatementRecords(database).ImportAsync(tx, debit["importId"].AsInt64, ct);
        var row = import["preview"]["rows"].AsBsonArray.SingleOrDefault(row => row["number"] == debit["rowNumber"])?.AsBsonDocument ?? throw B.Broken();
        if (row["direction"] != "debit" || row["transactionId"].IsBsonNull || row["identityCandidate"].IsBsonNull ||
            InvoiceBson.Money(row["amount"]).Value <= 0 || import["preview"]["rows"].AsBsonArray.Count(value => value["identityCandidate"] == row["identityCandidate"]) != 1) throw B.Broken();
        BankReceiptRecords.Equal(debit, SettlementDebitSnapshot(import, row));
        var account = import["account"].AsBsonDocument; var accounts = new ReceivingAccountRecords(database);
        var head = await accounts.HeadAsync(tx, debit["accountId"].AsInt64, ct);
        var version = await accounts.RowAsync(tx, ReceivingAccountBson.Versions, debit["accountVersionId"].AsInt64, ct);
        ReceivingAccountRecords.Hash(version); BankReceiptRecords.Equal(version, account); BankReceiptRecords.Equal(head["scope"], debit["scope"]);
        var anchor = await database.GetCollection<BsonDocument>(B.Accounts).Find(tx, new BsonDocument("physicalIdentity", debit["physicalIdentity"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        BankReceiptRecords.Equal(anchor["snapshotHash"], B.Hash(anchor)); BankReceiptRecords.Equal(anchor["scope"], debit["scope"]);
        BankReceiptRecords.Equal(anchor["identityNamespace"], debit["identityNamespace"]);
        var first = await BankReceiptRecords.ReadReceiptHistoryAsync(database, tx, anchor["firstReceiptId"].AsInt64, ct);
        await new BankTransactionClaims(database).RequireCreditAsync(tx, first, ct);
        var journal = await database.GetCollection<BsonDocument>("journalEntries").Find(tx, new BsonDocument("_id", first["journalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var draft = AccountingBson.ReadDraft(journal); var cash = draft.Lines.SingleOrDefault(line => line.MappingKey == first["policy"]["cashMapping"].AsString) ?? throw B.Broken();
        if (cash.Debit != InvoiceBson.Money(first["amount"]) || cash.Credit.Value != 0 || account["details"]["currency"] != draft.Book.Currency) throw B.Broken();
        return new() { { "mappingKey", cash.MappingKey }, { "accountId", cash.AccountId }, { "accountCode", cash.AccountCode } };
    }
}
