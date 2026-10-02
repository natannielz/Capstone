using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using B = UnitToko.Platform.Billing.BankReceiptBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class RefundSettlementBankSourceTests(MongoFixture fixture)
{
    [Fact]
    public async Task Actual_unique_debit_capture_and_final_fence_use_bank_read_grants_and_change_no_money_or_claim() {
        await using var data = await BankReceiptTestData.CreateAsync(fixture); await data.ConfirmAsync(await data.PreviewAsync());
        var debit = await DebitAsync(data); var original = await FinancialAsync(data); var catalog = data.Statements.Accounts.Catalog;
        await new AuthorizationStore(catalog.Database, new(catalog.Client), catalog.Clock).ReadAsync(data.Reader, async (authority, tx, ct) => {
            var selection = data.Statements.Selection;
            var source = await data.Service.SettlementDebitSourceAsync(data.Reader, authority, tx, selection.AccountId, selection.AccountVersionId, debit, 2, ct);
            Assert.Equal("debit", source.Row["direction"]); Assert.Equal("0002", source.Row["transactionId"]); Assert.Equal(1000.25m, source.Row["amount"].AsDecimal);
            Assert.Equal(debit, source.Import["_id"].AsInt64); Assert.Equal(source.Import["account"]["physicalIdentity"], source.PayingBank.Anchor["physicalIdentity"]);
            Assert.Equal(source.Import["scope"], source.PayingBank.Scope); Assert.Equal(source.Profile["format"]["identityNamespace"], source.PayingBank.IdentityNamespace);
            Assert.DoesNotContain(authority.Grants.SelectMany(grant => grant.Permissions), permission => permission is Permissions.BankReceiptConfirm or Permissions.DisbursementVerify);
            await source.FenceAsync(ct); source.RequireCurrent(); return true;
        }, default);
        Assert.Equal(original, await FinancialAsync(data));
    }
    [Fact]
    public async Task Current_bank_read_grant_removed_after_actual_capture_denies_final_fence_and_rolls_back() {
        await using var data = await BankReceiptTestData.CreateAsync(fixture); await data.ConfirmAsync(await data.PreviewAsync());
        var debit = await DebitAsync(data); var catalog = data.Statements.Accounts.Catalog; var original = await FinancialAsync(data);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new AuthorizationStore(catalog.Database, new(catalog.Client), catalog.Clock).ReadAsync(data.Reader, async (authority, tx, ct) => {
            var selection = data.Statements.Selection;
            var source = await data.Service.SettlementDebitSourceAsync(data.Reader, authority, tx, selection.AccountId, selection.AccountVersionId, debit, 2, ct);
            var changed = await data.Rows("authorizationGrants").UpdateManyAsync(tx, new BsonDocument("userId", data.Reader.UserId),
                Builders<BsonDocument>.Update.Pull("permissions", Permissions.BankStatementRead), cancellationToken: ct);
            Assert.True(changed.ModifiedCount > 0); await source.FenceAsync(ct); return true;
        }, default));
        Assert.Equal(original, await FinancialAsync(data));
    }
    [Theory]
    [InlineData("credit")] [InlineData("ambiguous")] [InlineData("missing")]
    public async Task Credit_missing_identity_and_candidates_repeated_across_all_rows_cannot_be_debit_sources(string fault) {
        await using var data = await BankReceiptTestData.CreateAsync(fixture); await data.ConfirmAsync(await data.PreviewAsync());
        var csv = Encoding.UTF8.GetString(BankStatementTestData.Original(fault == "missing" ? "" : "0002"));
        if (fault != "credit") csv = csv.Replace(",C,", ",D,", StringComparison.Ordinal);
        if (fault == "ambiguous") csv += csv.Split("\r\n", StringSplitOptions.None)[1].Replace(",D,", ",C,", StringComparison.Ordinal) + "\r\n";
        var import = (await data.Statements.StageAsync("debit-denied-" + fault, Encoding.UTF8.GetBytes(csv)))["importId"].AsInt64;
        var before = await FinancialAsync(data); var error = await Assert.ThrowsAsync<DomainValidationException>(() => CaptureAsync(data, import));
        Assert.Equal("refund_settlement_debit_unavailable", error.Code); Assert.Equal(before, await FinancialAsync(data));
    }
    [Fact]
    public async Task Canonical_credit_claim_and_its_orphan_confirmation_never_become_an_unclaimed_debit() {
        await using var data = await BankReceiptTestData.CreateAsync(fixture); await data.ConfirmAsync(await data.PreviewAsync());
        var credit = (await data.Statements.StageAsync("second-real-credit", BankStatementTestData.Original("0003")))["importId"].AsInt64;
        var receipt = await data.ConfirmAsync(await data.PreviewAsync(credit), "confirm-second-real-credit", BankReceiptTestData.Observation("0003"));
        var debit = await DebitAsync(data, "0003"); var original = await FinancialAsync(data);
        var error = await Assert.ThrowsAsync<UnitToko.Platform.Mongo.PersistenceConflictException>(() => CaptureAsync(data, debit));
        Assert.Equal("refund_settlement_identity_claimed", error.Code); Assert.Equal(original, await FinancialAsync(data));
        // Keep the actual first physical anchor intact while deliberately orphaning a later claim.
        Assert.Equal(1, (await data.Rows(B.Receipts).DeleteOneAsync(new BsonDocument("_id", receipt["receiptId"]))).DeletedCount);
        var damaged = await FinancialAsync(data); await Assert.ThrowsAsync<AuthorizationDeniedException>(() => CaptureAsync(data, debit));
        Assert.Equal(damaged, await FinancialAsync(data)); Assert.Equal(2, await data.Rows(B.Confirmations).CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Outside_scope_and_mutated_exposed_source_never_pass_actual_final_admission() {
        await using var data = await BankReceiptTestData.CreateAsync(fixture); await data.ConfirmAsync(await data.PreviewAsync());
        var debit = await DebitAsync(data); var catalog = data.Statements.Accounts.Catalog; var original = await FinancialAsync(data);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => CaptureAsync(data, debit, outside: true));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new AuthorizationStore(catalog.Database, new(catalog.Client), catalog.Clock).ReadAsync(data.Reader, async (authority, tx, ct) => {
            var selection = data.Statements.Selection;
            var source = await data.Service.SettlementDebitSourceAsync(data.Reader, authority, tx, selection.AccountId, selection.AccountVersionId, debit, 2, ct);
            source.Row["amount"] = new BsonDecimal128(1m); await source.FenceAsync(ct); return true;
        }, default));
        Assert.Equal(original, await FinancialAsync(data));
    }
    private static async Task<long> DebitAsync(BankReceiptTestData data, string transactionId = "0002") {
        var csv = Encoding.UTF8.GetString(BankStatementTestData.Original(transactionId)).Replace(",C,", ",D,", StringComparison.Ordinal);
        return (await data.Statements.StageAsync("debit-" + transactionId, Encoding.UTF8.GetBytes(csv)))["importId"].AsInt64;
    }
    private static Task<bool> CaptureAsync(BankReceiptTestData data, long import, bool outside = false) {
        var catalog = data.Statements.Accounts.Catalog; var actor = outside ? data.Other : data.Reader;
        return new AuthorizationStore(catalog.Database, new(catalog.Client), catalog.Clock).ReadAsync(actor, async (authority, tx, ct) => {
            var selection = data.Statements.Selection;
            var source = await data.Service.SettlementDebitSourceAsync(actor, authority, tx, selection.AccountId, selection.AccountVersionId, import, 2, ct);
            await source.FenceAsync(ct); source.RequireCurrent(); return true;
        }, default);
    }
    private static async Task<BsonDocument> FinancialAsync(BankReceiptTestData data) {
        var snapshot = new BsonDocument();
        foreach (var name in new[] { B.Receipts, B.Confirmations, B.Accounts, "journalEntries", "paymentReceiptBalances", "paymentInvoiceBalances", "paymentAllocations", "refundPayouts" }) {
            var rows = await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); snapshot[name] = new BsonArray(rows);
        }
        return snapshot;
    }
}
