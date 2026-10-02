using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Billing.BankReceiptBson;
using Q = UnitToko.Platform.Billing.ReceivingAccountBson;

namespace UnitToko.Platform.Billing;

internal sealed record RefundPayoutBankSource(BsonDocument Anchor, BsonDocument Account, BsonDocument AccountHead,
    BsonDocument FirstReceipt, BsonDocument CashJournal, BsonDocument Scope, BsonDocument Book, BsonDocument CashAccount,
    string IdentityNamespace, Action RequireCurrent, Func<CancellationToken, Task> FenceAsync);

public sealed partial class BankReceiptService
{
    internal IMongoDatabase PayoutBankDatabase => database;
    internal Task PayoutBankStorageAsync(CancellationToken ct) => StorageAsync(ct);
    private static BsonDocument PayoutBankStable(BsonDocument row) {
        var copy = row.DeepClone().AsBsonDocument; copy.Remove("fence"); return copy;
    }
    private sealed record PayoutBankRows(BsonDocument Anchor, BsonDocument Account, BsonDocument AccountHead,
        BsonDocument Receipt, BsonDocument Journal, BsonDocument Book, BsonDocument CashAccount);

    // Actual bank/account graph validation, usable by migration without a fabricated reader.
    private static async Task<PayoutBankRows> PayoutBankRowsAsync(IMongoDatabase database, IClock clock,
        IClientSessionHandle tx, long accountId, long versionId, CancellationToken ct, bool current = true) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || accountId <= 0 || versionId <= 0) throw B.Broken();
        var accounts = new ReceivingAccountRecords(database); var head = await accounts.HeadAsync(tx, accountId, ct);
        var account = await accounts.RowAsync(tx, Q.Versions, versionId, ct); ReceivingAccountRecords.Hash(account);
        if (current) BankReceiptRecords.Equal(head["versionId"], versionId);
        BankReceiptRecords.Equal(account["accountId"], accountId); BankReceiptRecords.Equal(account["scope"], head["scope"]);
        if (account["effectiveAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime) throw B.Broken();
        var nativeClient = database.Client as MongoClient ?? throw B.Broken();
        var sourceUnit = new MongoUnitOfWork(nativeClient);
        var accountOwner = new ReceivingAccountService(database, sourceUnit, new(database), clock);
        var statementOwner = new BankStatementService(database, sourceUnit, new(database), clock, accountOwner);
        var bank = new BankReceiptRecords(database, statementOwner);
        var anchor = await bank.AnchorAsync(tx, account["physicalIdentity"].AsString, ct) ?? throw B.Broken();
        BankReceiptRecords.Equal(anchor["scope"], account["scope"]);
        var first = await bank.RowAsync(tx, B.Receipts, anchor["firstReceiptId"].AsInt64, ct); BankReceiptRecords.Equal(first["snapshotHash"], B.Hash(first));
        var import = await new BankStatementRecords(database).ImportAsync(tx, first["importId"].AsInt64, ct);
        var transfer = import["preview"]["rows"].AsBsonArray.SingleOrDefault(row => row["number"] == first["rowNumber"])?.AsBsonDocument ?? throw B.Broken();
        var source = new BankStatementReceiptSource(import, transfer, null);
        var confirmation = await BankReceiptRecords.ReadConfirmationAsync(database, tx, first["confirmationId"].AsInt64, ct);
        BankReceiptRecords.ConfirmationSource(confirmation, source); BankReceiptRecords.SameTransfer(first, source);
        foreach (var field in new[] { "actorId", "recordedAt", "policy" }) BankReceiptRecords.Equal(confirmation[field], first[field]);
        BankReceiptRecords.Equal(confirmation["receiptId"], first["_id"]); BankReceiptRecords.Equal(confirmation["createdReceipt"], true);
        BankReceiptRecords.Equal(first["importHash"], import["snapshotHash"]); BankReceiptRecords.Equal(first["rowHash"], B.Hash(transfer));
        if (first["postingFence"] != 1L || first["journalId"].IsBsonNull || first["journalFingerprint"].IsBsonNull) throw B.Broken();
        var journal = await bank.Rows("journalEntries").Find(tx, new BsonDocument("_id", first["journalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var draft = AccountingBson.ReadDraft(journal); var expected = BankReceiptPostingSource.Snapshot(first);
        if (draft.Source != expected.Source || draft.SourceRevision != expected.Revision || draft.Book != expected.Book || draft.Date != expected.Date || draft.Dimensions != expected.Dimensions ||
            draft.ReversalOf is not null || draft.Fingerprint != first["journalFingerprint"].AsString || journal["actorId"] != first["actorId"] ||
            !draft.Lines.Select(line => new UnitToko.Modules.Accounting.PostingLine(line.MappingKey, line.Debit, line.Credit)).SequenceEqual(expected.Lines)) throw B.Broken();
        var cash = draft.Lines.Single(line => line.MappingKey == first["policy"]["cashMapping"].AsString);
        if (cash.Debit != InvoiceBson.Money(first["amount"]) || cash.Credit.Value != 0 || account["details"]["currency"] != draft.Book.Currency) throw B.Broken();
        return new(PayoutBankStable(anchor), PayoutBankStable(account), PayoutBankStable(head), PayoutBankStable(first), journal.DeepClone().AsBsonDocument,
            AccountingBson.Key(draft.Book), new() { { "mappingKey", cash.MappingKey }, { "accountId", cash.AccountId }, { "accountCode", cash.AccountCode } });
    }
    private static void EqualPayoutBank(PayoutBankRows actual, PayoutBankRows expected) {
        foreach (var pair in new[] { (actual.Anchor, expected.Anchor), (actual.Account, expected.Account), (actual.AccountHead, expected.AccountHead),
            (actual.Receipt, expected.Receipt), (actual.Journal, expected.Journal), (actual.Book, expected.Book), (actual.CashAccount, expected.CashAccount) }) BankReceiptRecords.Equal(pair.Item1, pair.Item2);
    }

    internal async Task<RefundPayoutBankSource> PayoutBankSourceAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long accountId, long accountVersionId, CancellationToken ct) {
        if (actor is null || authority is null || tx is null || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) ||
            actor.UserId != authority.UserId || actor.GrantVersion != authority.GrantVersion || accountId <= 0 || accountVersionId <= 0) throw B.Broken();
        _ = actor.Validate(); _ = Require(new(actor, authority, tx, false));
        var accountOwner = new ReceivingAccountService(database, new(database.Client as MongoClient ?? throw B.Broken()), ids, clock);
        var approved = await accountOwner.ReadSourceAsync(actor, authority, tx, accountId, accountVersionId, true, ct);
        var context = new Context(actor, authority, tx, false, approved["scope"].AsBsonDocument); _ = Require(context);
        var frozen = await PayoutBankRowsAsync(database, clock, tx, accountId, accountVersionId, ct); BankReceiptRecords.Equal(frozen.Account, PayoutBankStable(approved));
        // Also use the live bank-owned reader, which fences the original source import/account scope.
        var first = await records.ReceiptAsync(actor, authority, tx, frozen.Receipt["_id"].AsInt64, ct); BankReceiptRecords.Equal(PayoutBankStable(first), frozen.Receipt);
        void Current() { _ = Require(context); ct.ThrowIfCancellationRequested(); }
        async Task Fence(CancellationToken token) {
            Current();
            var currentAccount = await accountOwner.ReadSourceAsync(actor, authority, tx, accountId, accountVersionId, true, token); BankReceiptRecords.Equal(PayoutBankStable(currentAccount), frozen.Account);
            var currentReceipt = await records.ReceiptAsync(actor, authority, tx, frozen.Receipt["_id"].AsInt64, token); BankReceiptRecords.Equal(PayoutBankStable(currentReceipt), frozen.Receipt);
            var currentAnchor = await records.AnchorAsync(tx, frozen.Anchor["physicalIdentity"].AsString, token) ?? throw B.Broken(); BankReceiptRecords.Equal(PayoutBankStable(currentAnchor), frozen.Anchor);
            await records.TouchAsync(tx, B.Accounts, currentAnchor, token); await records.TouchAsync(tx, B.Receipts, currentReceipt, token);
            EqualPayoutBank(await PayoutBankRowsAsync(database, clock, tx, accountId, accountVersionId, token), frozen);
            await FenceAsync(context, token); Current(); token.ThrowIfCancellationRequested();
        }
        // This same-transaction capture does not complete admission. Every successful caller
        // must invoke the returned full FenceAsync before returning or committing its phase.
        Current();
        return new(frozen.Anchor.DeepClone().AsBsonDocument, frozen.Account.DeepClone().AsBsonDocument, frozen.AccountHead.DeepClone().AsBsonDocument,
            frozen.Receipt.DeepClone().AsBsonDocument, frozen.Journal.DeepClone().AsBsonDocument, frozen.Account["scope"].DeepClone().AsBsonDocument,
            frozen.Book.DeepClone().AsBsonDocument, frozen.CashAccount.DeepClone().AsBsonDocument, frozen.Anchor["identityNamespace"].AsString, Current, Fence);
    }

    internal static async Task PreflightPayoutBanksAsync(IMongoDatabase database, IClock clock, IClientSessionHandle tx, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
        using var anchors = await database.GetCollection<BsonDocument>(B.Accounts).Find(tx, new BsonDocument()).ToCursorAsync(ct);
        while (await anchors.MoveNextAsync(ct)) foreach (var anchor in anchors.Current) {
            var first = await database.GetCollection<BsonDocument>(B.Receipts).Find(tx, new BsonDocument("_id", anchor["firstReceiptId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            var import = await new BankStatementRecords(database).ImportAsync(tx, first["importId"].AsInt64, ct);
            var accountId = import["account"]["accountId"].AsInt64;
            var source = await PayoutBankRowsAsync(database, clock, tx, accountId, import["account"]["_id"].AsInt64, ct, current: false);
            BankReceiptRecords.Equal(source.Anchor, PayoutBankStable(anchor));
        }
        foreach (var receipt in await database.GetCollection<BsonDocument>(B.Receipts).Find(tx, new BsonDocument()).ToListAsync(ct))
            if (!await database.GetCollection<BsonDocument>(B.Accounts).Find(tx, new BsonDocument("physicalIdentity", receipt["physicalIdentity"])).AnyAsync(ct)) throw B.Broken();
    }
}
