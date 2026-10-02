using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Billing.BankReceiptBson;

namespace UnitToko.Platform.Billing;

// Read admission only. This capture neither consumes a bank transaction nor authorizes
// settlement: the financial owner must supply independent verification and an atomic,
// cross-direction claim in its command transaction before any financial effect.
internal sealed record RefundSettlementBankSource(BsonDocument Import, BsonDocument Row, BsonDocument Profile,
    RefundPayoutBankSource PayingBank, Action RequireCurrent, Func<CancellationToken, Task> FenceAsync);

public sealed partial class BankReceiptService
{
    internal async Task<RefundSettlementBankSource> SettlementDebitSourceAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long accountId, long accountVersionId, long importId, int rowNumber, CancellationToken ct) {
        if (actor is null || authority is null || tx is null || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) ||
            actor.UserId != authority.UserId || actor.GrantVersion != authority.GrantVersion || accountId <= 0 || accountVersionId <= 0) throw B.Broken();
        Selection(importId, rowNumber); _ = actor.Validate(); _ = Require(new(actor, authority, tx, false));
        await StorageAsync(ct);
        var paying = await PayoutBankSourceAsync(actor, authority, tx, accountId, accountVersionId, ct);
        var (context, selected) = await SourceAsync(new(actor, authority, tx, false), importId, rowNumber, true, ct);
        RequireSettlementDebit(selected, paying, accountId, accountVersionId);
        await RequireUnclaimedIncomingAsync(actor, authority, tx, selected.Row["identityCandidate"].AsString, ct);

        var frozenImport = SettlementStable(selected.Import); var frozenRow = selected.Row.DeepClone().AsBsonDocument;
        var frozenProfile = SettlementStable(selected.CurrentProfile ?? throw B.Broken()); var frozenBank = SettlementBankSnapshot(paying);
        var exposedImport = frozenImport.DeepClone().AsBsonDocument; var exposedRow = frozenRow.DeepClone().AsBsonDocument;
        var exposedProfile = frozenProfile.DeepClone().AsBsonDocument;
        void Current() {
            if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
            _ = Require(context); paying.RequireCurrent(); BankStatementRecords.ProfileAt(frozenProfile, Now);
            BankReceiptRecords.Equal(exposedImport, frozenImport); BankReceiptRecords.Equal(exposedRow, frozenRow);
            BankReceiptRecords.Equal(exposedProfile, frozenProfile); BankReceiptRecords.Equal(SettlementBankSnapshot(paying), frozenBank);
            ct.ThrowIfCancellationRequested();
        }
        async Task Fence(CancellationToken token) {
            Current();
            var actual = await SourceAsync(context, importId, rowNumber, true, token);
            RequireSettlementDebit(actual.Source, paying, accountId, accountVersionId);
            BankReceiptRecords.Equal(SettlementStable(actual.Source.Import), frozenImport); BankReceiptRecords.Equal(actual.Source.Row, frozenRow);
            BankReceiptRecords.Equal(SettlementStable(actual.Source.CurrentProfile ?? throw B.Broken()), frozenProfile);
            await RequireUnclaimedIncomingAsync(actor, authority, tx, frozenRow["identityCandidate"].AsString, token);
            // This is the actual physical anchor fence also used by incoming credit admission.
            // Absence alone is not a cross-direction claim; the command owner must create it.
            await paying.FenceAsync(token); await FenceAsync(context, token);
            Current(); token.ThrowIfCancellationRequested();
        }
        Current();
        return new(exposedImport, exposedRow, exposedProfile, paying, Current, Fence);
    }
    private static BsonDocument SettlementStable(BsonDocument row) {
        var copy = row.DeepClone().AsBsonDocument; copy.Remove("fence"); return copy;
    }
    private static BsonDocument SettlementBankSnapshot(RefundPayoutBankSource source) => new() {
        { "anchor", SettlementStable(source.Anchor) }, { "account", SettlementStable(source.Account) }, { "head", SettlementStable(source.AccountHead) },
        { "firstReceipt", SettlementStable(source.FirstReceipt) }, { "cashJournal", source.CashJournal.DeepClone() },
        { "scope", source.Scope.DeepClone() }, { "book", source.Book.DeepClone() }, { "cashAccount", source.CashAccount.DeepClone() }, { "identityNamespace", source.IdentityNamespace } };
    private static void RequireSettlementDebit(BankStatementReceiptSource source, RefundPayoutBankSource paying, long accountId, long versionId) {
        var row = source.Row; var import = source.Import; var account = import["account"];
        if (row["direction"] != "debit" || row["transactionId"].IsBsonNull || row["identityCandidate"].IsBsonNull ||
            InvoiceBson.Money(row["amount"]).Value <= 0 ||
            import["preview"]["rows"].AsBsonArray.Count(value => value["identityCandidate"] == row["identityCandidate"]) != 1)
            throw BankReceiptPolicy.Invalid("refund_settlement_debit_unavailable");
        if (source.CurrentProfile is null) throw B.Broken();
        BankReceiptRecords.Equal(account["accountId"], accountId); BankReceiptRecords.Equal(account["_id"], versionId);
        BankReceiptRecords.Equal(SettlementStable(account.AsBsonDocument), SettlementStable(paying.Account));
        BankReceiptRecords.Equal(import["scope"], paying.Scope); BankReceiptRecords.Equal(account["physicalIdentity"], paying.Anchor["physicalIdentity"]);
        BankReceiptRecords.Equal(import["profile"]["format"]["identityNamespace"], paying.IdentityNamespace);
        BankReceiptRecords.Equal(source.CurrentProfile["format"]["identityNamespace"], paying.IdentityNamespace);
        BankReceiptRecords.Equal(row["identityCandidate"], BankStatementParser.Candidate(account["physicalIdentity"].AsString,
            paying.IdentityNamespace, row["transactionId"].AsString));
    }
    private async Task RequireUnclaimedIncomingAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        string canonicalIdentity, CancellationToken ct) {
        var receipt = await records.Rows(B.Receipts).Find(tx, new BsonDocument("canonicalIdentity", canonicalIdentity)).SingleOrDefaultAsync(ct);
        if (receipt is not null) {
            _ = await records.ReceiptAsync(actor, authority, tx, receipt["_id"].AsInt64, ct);
            throw B.Changed("refund_settlement_identity_claimed");
        }
        var confirmation = await records.Rows(B.Confirmations).Find(tx, new BsonDocument("canonicalIdentity", canonicalIdentity))
            .Sort(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
        if (confirmation is not null) {
            // The actual owner refuses orphan/effect/source corruption rather than treating
            // a missing receipt as an unclaimed transaction that could be consumed again.
            _ = await records.ConfirmationAsync(actor, authority, tx, confirmation["_id"].AsInt64, ct);
            throw B.Changed("refund_settlement_identity_claimed");
        }
    }
}
