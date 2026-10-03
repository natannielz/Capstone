using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Billing.BankReceiptBson;

namespace UnitToko.Platform.Billing;

// A bank-owned, transaction-bound proof. Financial authorization belongs to the procurement owner.
internal sealed class ProcurementSettlementBankCapture(BankReceiptService owner, ActorContext actor, AuthorizationSnapshot authority,
    IClientSessionHandle tx, BsonDocument import, BsonDocument row, BsonDocument profile, BsonDocument debit, RefundPayoutBankSource payingBank,
    Action current, Func<CancellationToken, Task> fence, Func<BsonDocument, CancellationToken, Task> claim)
{
    private readonly long transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
    private readonly string sourceHash = SourceHash(import, row, profile, debit, payingBank);
    internal BsonDocument Import { get; } = import;
    internal BsonDocument Row { get; } = row;
    internal BsonDocument Profile { get; } = profile;
    internal BsonDocument Debit { get; } = debit;
    internal RefundPayoutBankSource PayingBank { get; } = payingBank;
    internal void RequireContext(BankReceiptService service, ActorContext caller, AuthorizationSnapshot auth, IClientSessionHandle session) {
        if (!ReferenceEquals(service, owner) || caller != actor || !ReferenceEquals(auth, authority) || !ReferenceEquals(session, tx) || !session.IsInTransaction ||
            session.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction || SourceHash(Import, Row, Profile, Debit, PayingBank) != sourceHash) throw B.Broken();
        current();
    }
    private static string SourceHash(BsonDocument import, BsonDocument row, BsonDocument profile, BsonDocument debit, RefundPayoutBankSource bank) => B.Hash(new() {
        { "import", import }, { "row", row }, { "profile", profile }, { "debit", debit }, { "anchor", bank.Anchor }, { "account", bank.Account }, { "head", bank.AccountHead },
        { "receipt", bank.FirstReceipt }, { "journal", bank.CashJournal }, { "scope", bank.Scope }, { "book", bank.Book }, { "cash", bank.CashAccount }, { "namespace", bank.IdentityNamespace } });
    internal void RequireCurrent() => RequireContext(owner, actor, authority, tx);
    internal async Task FenceAsync(CancellationToken ct) { RequireCurrent(); await fence(ct); RequireCurrent(); ct.ThrowIfCancellationRequested(); }
    internal async Task ClaimAsync(BsonDocument settlement, CancellationToken ct) { RequireCurrent(); await claim(settlement, ct); RequireCurrent(); ct.ThrowIfCancellationRequested(); }
}

public sealed partial class BankReceiptService
{
    internal async Task<ProcurementSettlementBankCapture> CaptureProcurementSettlementAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long accountId, long accountVersionId, long importId, int rowNumber, CancellationToken ct) {
        BankTransactionClaims.RequireTransaction(database, tx);
        var transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        var source = await SettlementDebitSourceAsync(actor, authority, tx, accountId, accountVersionId, importId, rowNumber, ct);
        var claims = new BankTransactionClaims(database); var canonical = source.Row["identityCandidate"].AsString;
        await claims.RequireNoOrphanSettlementAsync(tx, ct);
        await claims.RequireUnclaimedAsync(tx, canonical, ct); await claims.RequireNoOrphanCreditAsync(tx, canonical, ct);
        var frozen = SettlementDebitSnapshot(source.Import, source.Row); var exposed = frozen.DeepClone().AsBsonDocument;
        BsonDocument? claimed = null;
        void Current() {
            BankTransactionClaims.RequireTransaction(database, tx); source.RequireCurrent(); BankReceiptRecords.Equal(exposed, frozen);
            if (actor.UserId != authority.UserId || actor.GrantVersion != authority.GrantVersion || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction) throw B.Broken();
            ct.ThrowIfCancellationRequested();
        }
        async Task Fence(CancellationToken token) {
            Current(); await source.FenceAsync(token);
            if (claimed is null) await claims.RequireUnclaimedAsync(tx, canonical, token);
            else await claims.RequireProcurementDebitAsync(tx, claimed, token);
            Current(); token.ThrowIfCancellationRequested();
        }
        async Task Claim(BsonDocument settlement, CancellationToken token) {
            Current(); if (claimed is not null) throw B.Broken();
            BankReceiptRecords.Equal(settlement["debit"], frozen); BankReceiptRecords.Equal(settlement["canonicalIdentity"], canonical);
            BankReceiptRecords.Equal(settlement["actorId"], actor.UserId); BankReceiptRecords.Equal(settlement["actorGrantVersion"], actor.GrantVersion);
            if (settlement["recordedAt"].ToUniversalTime() < source.Import["recordedAt"].ToUniversalTime()) throw B.Broken();
            var expected = ProcurementSettlementBson.Claim(settlement);
            var owners = await records.Rows(ProcurementSettlementBson.Events).Find(tx, new BsonDocument("canonicalIdentity", canonical)).Limit(2).ToListAsync(token);
            if (owners.Count != 1) throw B.Broken(); BankReceiptRecords.Equal(owners[0], settlement);
            if (await claims.ReadAsync(tx, canonical, token) is not null) throw B.Changed("bank_transaction_already_claimed");
            foreach (var name in new[] { B.Receipts, B.Confirmations, BankTransactionClaims.Settlements })
                if (await records.Rows(name).Find(tx, new BsonDocument("canonicalIdentity", canonical)).AnyAsync(token)) throw B.Broken();
            await claims.RequireNoOrphanCreditAsync(tx, canonical, token);
            await source.FenceAsync(token); Current();
            await records.Rows(BankTransactionClaims.Collection).InsertOneAsync(tx, expected, cancellationToken: token);
            claimed = settlement.DeepClone().AsBsonDocument; await claims.RequireProcurementDebitAsync(tx, claimed, token); Current();
        }
        Current(); return new(this, actor, authority, tx, source.Import, source.Row, source.Profile, exposed, source.PayingBank, Current, Fence, Claim);
    }

    internal async Task<BsonDocument> ProcurementSettlementImportsAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        long accountId, long versionId, long? beforeId, CancellationToken ct, Func<CancellationToken, Task>? finalValidation = null) {
        BankTransactionClaims.RequireTransaction(database, tx);
        if (beforeId is <= 0) throw B.Broken();
        await StorageAsync(ct); var paying = await PayoutBankSourceAsync(actor, authority, tx, accountId, versionId, ct);
        var context = new Context(actor, authority, tx, false, paying.Scope); _ = Require(context);
        var filter = new BsonDocument { { "account.accountId", accountId }, { "account._id", versionId }, { "scope", paying.Scope } };
        if (beforeId is { } cursor) filter.Add("_id", new BsonDocument("$lt", cursor));
        var candidates = await records.Rows(BankStatementBson.Imports).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(51).ToListAsync(ct);
        var items = new BsonArray(); var profiles = new List<BsonDocument>();
        foreach (var candidate in candidates.Take(50)) {
            // The owner proves the complete parsed import before exposing even its filename.
            var verified = await new BankStatementRecords(database).ImportAsync(tx, candidate["_id"].AsInt64, ct);
            var first = verified["preview"]["rows"].AsBsonArray.FirstOrDefault()?.AsBsonDocument ?? throw B.Broken();
            var actual = await SourceAsync(context, verified["_id"].AsInt64, first["number"].AsInt32, true, ct);
            RequireProcurementImport(actual.Source, paying, accountId, versionId);
            profiles.Add(actual.Source.CurrentProfile ?? throw B.Broken());
            items.Add(new BsonDocument { { "id", verified["_id"] }, { "filename", verified["filename"] },
                { "profileName", verified["profile"]["name"] }, { "recordedAt", verified["recordedAt"] }, { "rowCount", verified["preview"]["rows"].AsBsonArray.Count } });
        }
        await paying.FenceAsync(ct); await FenceAsync(context, ct);
        if (finalValidation is not null) await finalValidation(ct);
        paying.RequireCurrent();
        foreach (var profile in profiles) BankStatementRecords.ProfileAt(profile, Now);
        _ = Require(context); ct.ThrowIfCancellationRequested();
        return new() { { "items", items }, { "nextImportId", candidates.Count > 50 ? candidates[49]["_id"] : BsonNull.Value } };
    }

    internal async Task<BsonDocument> ProcurementSettlementDebitsAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        long accountId, long versionId, long importId, Money expectedAmount, DateOnly transferDate, CancellationToken ct, Func<CancellationToken, Task>? finalValidation = null) {
        BankTransactionClaims.RequireTransaction(database, tx);
        if (expectedAmount.Value <= 0 || transferDate == default) throw B.Broken();
        await StorageAsync(ct); var paying = await PayoutBankSourceAsync(actor, authority, tx, accountId, versionId, ct);
        var context = new Context(actor, authority, tx, false, paying.Scope); _ = Require(context);
        // Check private scope before reconstructing the selected import graph.
        var raw = await records.Rows(BankStatementBson.Imports).Find(tx, new BsonDocument { { "_id", importId }, { "account.accountId", accountId },
            { "account._id", versionId }, { "scope", paying.Scope } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var verified = await new BankStatementRecords(database).ImportAsync(tx, raw["_id"].AsInt64, ct);
        var first = verified["preview"]["rows"].AsBsonArray.FirstOrDefault()?.AsBsonDocument ?? throw B.Broken();
        var actual = await SourceAsync(context, importId, first["number"].AsInt32, true, ct);
        RequireProcurementImport(actual.Source, paying, accountId, versionId);
        var claims = new BankTransactionClaims(database); await claims.RequireNoOrphanSettlementAsync(tx, ct);
        var items = new BsonArray(); var lines = actual.Source.Import["preview"]["rows"].AsBsonArray;
        foreach (var line in lines.Where(line => line["direction"] == "debit")) {
            string? issue = null;
            if (line["transactionId"].IsBsonNull || line["identityCandidate"].IsBsonNull) issue = "missing_identity";
            else if (lines.Count(other => other["identityCandidate"] == line["identityCandidate"]) != 1) issue = "duplicate_identity";
            else {
                var canonical = line["identityCandidate"].AsString;
                if (await claims.ReadAsync(tx, canonical, ct) is not null) issue = "already_claimed";
                else { await claims.RequireUnclaimedAsync(tx, canonical, ct); await claims.RequireNoOrphanCreditAsync(tx, canonical, ct); }
            }
            if (issue is null && InvoiceBson.Money(line["amount"]) != expectedAmount) issue = "amount_mismatch";
            if (issue is null && (InvoiceBson.Date(line["bookedDate"]) < transferDate || InvoiceBson.Date(line["bookedDate"]) > AccountingCalendar.BusinessDate(Now))) issue = "date_mismatch";
            items.Add(new BsonDocument { { "rowNumber", line["number"] }, { "transactionId", line["transactionId"] }, { "bookedDate", line["bookedDate"] },
                { "amount", InvoiceBson.Money(line["amount"]).ToString() }, { "direction", "debit" }, { "reference", line["reference"] },
                { "eligible", issue is null }, { "issue", issue is null ? BsonNull.Value : new BsonString(issue) } });
        }
        await paying.FenceAsync(ct); await FenceAsync(actual.Context, ct);
        if (finalValidation is not null) await finalValidation(ct);
        paying.RequireCurrent();
        BankStatementRecords.ProfileAt(actual.Source.CurrentProfile ?? throw B.Broken(), Now); _ = Require(actual.Context); ct.ThrowIfCancellationRequested();
        return new() { { "items", items } };
    }

    private static void RequireProcurementImport(BankStatementReceiptSource source, RefundPayoutBankSource paying, long accountId, long versionId) {
        var account = source.Import["account"]; BankReceiptRecords.Equal(account["accountId"], accountId); BankReceiptRecords.Equal(account["_id"], versionId);
        BankReceiptRecords.Equal(SettlementStable(account.AsBsonDocument), SettlementStable(paying.Account)); BankReceiptRecords.Equal(source.Import["scope"], paying.Scope);
        BankReceiptRecords.Equal(source.Import["profile"]["format"]["identityNamespace"], paying.IdentityNamespace);
        BankReceiptRecords.Equal((source.CurrentProfile ?? throw B.Broken())["format"]["identityNamespace"], paying.IdentityNamespace);
    }
}
