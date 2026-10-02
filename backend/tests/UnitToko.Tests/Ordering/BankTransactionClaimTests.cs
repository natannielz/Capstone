using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.BankReceiptBson;
using C = UnitToko.Platform.Billing.BankTransactionClaims;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class BankTransactionClaimTests(MongoFixture fixture)
{
    [Fact]
    public async Task Real_credits_have_exact_durable_owners_and_reimport_preserves_original_cash_and_claim() {
        await using var data = await BankReceiptTestData.CreateAsync(fixture);
        var original = await data.ConfirmAsync(await data.PreviewAsync(), "claim-original");
        var nextImport = (await data.Statements.StageAsync("claim-distinct-import", BankStatementTestData.Original("0002")))["importId"].AsInt64;
        var distinct = await data.ConfirmAsync(await data.PreviewAsync(nextImport), "claim-distinct", BankReceiptTestData.Observation("0002"));
        var before = await MoneyAsync(data); var claims = await data.Rows(C.Collection).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.Equal(2, claims.Count);
        foreach (var claim in claims) {
            var receipt = await data.Rows(B.Receipts).Find(new BsonDocument("_id", claim["ownerId"])).SingleAsync();
            Assert.Equal(C.Credit(receipt), claim); Assert.Equal(claim["canonicalIdentity"], claim["_id"]); Assert.Equal("credit", claim["direction"]);
        }
        var overlap = (await data.Statements.StageAsync("claim-overlap", filename: "actual-overlap.csv"))["importId"].AsInt64;
        var repeated = await data.ConfirmAsync(await data.PreviewAsync(overlap), "claim-overlap-confirm");
        Assert.Equal(original["receiptId"], repeated["receiptId"]); Assert.False(repeated["createdReceipt"].AsBoolean);
        Assert.Equal(new BsonArray(claims), new BsonArray(await data.Rows(C.Collection).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        Assert.Equal(before, await MoneyAsync(data));
        Assert.Equal(original, await data.Service.RecoverAsync(data.Reviewer, "claim-original"));
        Assert.Equal(distinct, await data.Service.RecoverAsync(data.Reviewer, "claim-distinct"));
    }
    [Theory]
    [InlineData("missing")] [InlineData("hash")] [InlineData("owner")] [InlineData("source")]
    public async Task Missing_or_coherently_damaged_claim_is_never_repaired_by_read_recovery_or_replay(string fault) {
        await using var data = await BankReceiptTestData.CreateAsync(fixture); var preview = await data.PreviewAsync();
        var result = await data.ConfirmAsync(preview, "claim-damage"); var claim = await data.Rows(C.Collection).Find(new BsonDocument()).SingleAsync();
        if (fault == "missing") await data.Rows(C.Collection).DeleteOneAsync(new BsonDocument("_id", claim["_id"]));
        else {
            if (fault == "hash") claim["snapshotHash"] = new string('a', 64);
            if (fault == "owner") { claim["ownerHash"] = new string('b', 64); claim["snapshotHash"] = B.Hash(claim); }
            if (fault == "source") { claim["rowHash"] = new string('c', 64); claim["snapshotHash"] = B.Hash(claim); }
            await data.Rows(C.Collection).ReplaceOneAsync(new BsonDocument("_id", claim["_id"]), claim);
        }
        var before = await StateAsync(data);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Reader, result["receiptId"].AsInt64));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverAsync(data.Reviewer, "claim-damage"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.ConfirmAsync(preview, "claim-damage"));
        Assert.Equal(before, await StateAsync(data));
    }
    [Fact]
    public async Task Deleted_later_credit_owner_cannot_be_adopted_from_reimport_even_when_only_original_cash_journal_survives() {
        await using var data = await BankReceiptTestData.CreateAsync(fixture); await data.ConfirmAsync(await data.PreviewAsync(), "anchor");
        var import = (await data.Statements.StageAsync("orphan-credit", BankStatementTestData.Original("0002")))["importId"].AsInt64;
        var result = await data.ConfirmAsync(await data.PreviewAsync(import), "orphan-confirm", BankReceiptTestData.Observation("0002"));
        var receipt = await data.Rows(B.Receipts).Find(new BsonDocument("_id", result["receiptId"])).SingleAsync();
        var confirmation = await data.Rows(B.Confirmations).Find(new BsonDocument("_id", result["confirmationId"])).SingleAsync();
        await data.Rows(C.Collection).DeleteOneAsync(new BsonDocument("_id", receipt["canonicalIdentity"]));
        await data.Rows(B.Receipts).DeleteOneAsync(new BsonDocument("_id", receipt["_id"]));
        await data.Rows(B.Confirmations).DeleteOneAsync(new BsonDocument("_id", confirmation["_id"]));
        await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("_id", confirmation["auditId"]));
        await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { { "operation", B.Operation }, { "actorId", confirmation["actorId"] }, { "key", confirmation["requestKey"] } });
        await data.Rows("outboxEvents").DeleteOneAsync(new BsonDocument("sourceKey", B.SourceKey(confirmation)));
        var overlap = (await data.Statements.StageAsync("orphan-overlap", BankStatementTestData.Original("0002"), "new-name.csv"))["importId"].AsInt64;
        var before = await StateAsync(data);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.PreviewAsync(overlap)); Assert.Equal(before, await StateAsync(data));
        Assert.Equal(2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument("source.namespace", "billing.bank-receipt")));
    }
    [Fact]
    public async Task Permanent_original_bank_snapshot_and_head_hash_remain_provable_after_real_later_account_approval() {
        await using var data = await BankReceiptTestData.CreateAsync(fixture); await data.ConfirmAsync(await data.PreviewAsync(), "historic-anchor");
        var catalog = data.Statements.Accounts.Catalog; var at = catalog.Clock.UtcNow;
        var compact = await new AuthorizationStore(catalog.Database, new(catalog.Client), catalog.Clock).ReadAsync(data.Reader, async (authority, tx, ct) => {
            var selection = data.Statements.Selection;
            var source = await data.Service.PayoutBankSourceAsync(data.Reader, authority, tx, selection.AccountId, selection.AccountVersionId, ct);
            var result = new BsonDocument {
                { "accountId", source.Account["accountId"] }, { "versionId", source.Account["_id"] }, { "headHash", B.Hash(source.AccountHead) }, { "versionHash", source.Account["snapshotHash"] },
                { "physicalIdentity", source.Account["physicalIdentity"] }, { "identityNamespace", source.IdentityNamespace }, { "anchorHash", source.Anchor["snapshotHash"] },
                { "firstReceiptId", source.FirstReceipt["_id"] }, { "firstReceiptHash", source.FirstReceipt["snapshotHash"] }, { "cashJournalId", source.CashJournal["_id"] },
                { "cashJournalFingerprint", source.CashJournal["fingerprint"] }, { "scope", source.Scope.DeepClone() }, { "book", source.Book.DeepClone() },
                { "cashAccountId", source.CashAccount["accountId"] }, { "cashAccountCode", source.CashAccount["accountCode"] }, { "currency", source.Account["details"]["currency"] }
            };
            await source.FenceAsync(ct); source.RequireCurrent(); return result;
        }, default);
        var accounts = data.Statements.Accounts; var proposal = await accounts.ProposeAsync("historic-v2", data.Statements.Selection.AccountId, 1);
        await accounts.ApproveAsync(proposal, await accounts.ObserveAsync(proposal, key: "historic-v2-observe"), "historic-v2-approve");
        var money = await MoneyAsync(data);
        await new MongoUnitOfWork(catalog.Client).ExecuteAsync(async (tx, ct) => {
            var current = await new ReceivingAccountRecords(catalog.Database).HeadAsync(tx, data.Statements.Selection.AccountId, ct);
            Assert.NotEqual(compact["headHash"], new BsonString(B.Hash(current)));
            var cash = await BankReceiptService.ReadPayoutBankHistoryAsync(catalog.Database, tx, compact, at, ct);
            Assert.Equal("bank_cash", cash["mappingKey"]); Assert.Equal(compact["cashAccountId"], cash["accountId"]); Assert.Equal(compact["cashAccountCode"], cash["accountCode"]);
            return true;
        });
        var changed = compact.DeepClone().AsBsonDocument; changed["headHash"] = new string('e', 64);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new MongoUnitOfWork(catalog.Client).ExecuteAsync(async (tx, ct) => {
            _ = await BankReceiptService.ReadPayoutBankHistoryAsync(catalog.Database, tx, changed, at, ct); return true;
        }));
        Assert.Equal(money, await MoneyAsync(data));
    }
    [Fact]
    public async Task Required_claim_write_failure_rolls_back_cash_and_effects_and_original_key_can_retry_once() {
        await using var data = await BankReceiptTestData.CreateAsync(fixture); var preview = await data.PreviewAsync();
        var required = BankTransactionClaimSchemas.Validator(); var db = data.Statements.Accounts.Catalog.Database;
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", C.Collection },
            { "validator", new BsonDocument("$and", new BsonArray { required, new BsonDocument("direction", new BsonDocument("$ne", "credit")) }) },
            { "validationLevel", "strict" }, { "validationAction", "error" } });
        var before = await StateAsync(data); Assert.NotNull(await Record.ExceptionAsync(() => data.ConfirmAsync(preview, "failed-claim")));
        Assert.Equal(before, await StateAsync(data)); Assert.False(await data.Rows(B.Receipts).Find(new BsonDocument()).AnyAsync());
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", C.Collection }, { "validator", required }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        var result = await data.ConfirmAsync(preview, "failed-claim"); Assert.Equal(result, await data.Service.RecoverAsync(data.Reviewer, "failed-claim"));
        Assert.Equal(1, await data.Rows(C.Collection).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument("source.namespace", "billing.bank-receipt")));
    }
    internal static async Task<BsonDocument> MoneyAsync(BankReceiptTestData data) {
        var result = new BsonDocument();
        foreach (var name in new[] { B.Receipts, "journalEntries", "paymentReceiptBalances", "paymentInvoiceBalances", "paymentAllocations", "refundPayouts", C.Settlements }) {
            var rows = await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); result[name] = new BsonArray(rows);
        }
        return result;
    }
    private static async Task<BsonDocument> StateAsync(BankReceiptTestData data) {
        var result = await data.StateAsync(); result[C.Collection] = new BsonArray(await data.Rows(C.Collection).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()); return result;
    }
}
