using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Documents;
using D = UnitToko.Tests.Procurement.ProcurementSettlementTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ProcurementSettlementHistoryTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Missing_actual_claim_journal_or_owner_never_becomes_an_unclaimed_debit_or_another_posting()
    {
        await using var data = await D.CreateAsync(fixture);
        const string key = "settlement-retained-proof";
        var preview = await data.PreviewAsync(); var saved = await data.VerifyAsync(preview, key);
        var owner = await data.Rows(D.Events).Find(new BsonDocument("_id", saved["id"])).SingleAsync();
        var claim = await data.Rows(BankTransactionClaims.Collection).Find(new BsonDocument("_id", owner["canonicalIdentity"])).SingleAsync();
        Assert.Equal(ProcurementSettlementBson.Claim(owner), claim);
        Assert.Equal(2, claim["schemaVersion"].AsInt32); Assert.Equal("debit", claim["direction"].AsString);
        Assert.Equal("procurementDisbursementSettlement", claim["ownerKind"].AsString);
        Assert.Equal(owner["_id"], claim["ownerId"]); Assert.Equal(owner["snapshotHash"], claim["ownerHash"]);
        var intact = await FinancialStateAsync();

        foreach (var (table, filter, readCode) in new[] {
            (BankTransactionClaims.Collection, new BsonDocument("_id", claim["_id"]), "bank_receipt_unavailable"),
            ("journalEntries", new BsonDocument("_id", owner["journalId"]), "procurement_settlement_integrity_unavailable"),
            (D.Events, new BsonDocument("_id", owner["_id"]), "procurement_settlement_integrity_unavailable") }) {
            var actual = await data.Rows(table).Find(filter).SingleAsync();
            Assert.Equal(1, (await data.Rows(table).DeleteOneAsync(filter)).DeletedCount);
            try {
                var damaged = await FinancialStateAsync();
                Assert.Equal(readCode, (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Reader, data.DisbursementId))).Code);
                Assert.Equal(readCode, (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.LookupAsync(data.Verifier, D.Operation, key))).Code);
                if (table == D.Events)
                    Assert.Equal("procurement_settlement_integrity_unavailable", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.VerifyAsync(preview, "new-admission-after-owner-loss"))).Code);
                else
                    Assert.Equal("procurement_settlement_already_verified", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.VerifyAsync(preview, "new-admission-after-" + table))).Code);

                // Also challenge the bank-owned admission directly: this bypasses the
                // already-verified disbursement check, but never supplies a fabricated owner.
                if (table == "journalEntries")
                    Assert.Equal("bank_transaction_already_claimed", (await Assert.ThrowsAsync<PersistenceConflictException>(CaptureBankDebitAsync)).Code);
                else
                    Assert.Equal("bank_receipt_unavailable", (await Assert.ThrowsAsync<AuthorizationDeniedException>(CaptureBankDebitAsync)).Code);
                Assert.False(await data.Rows(table).Find(filter).AnyAsync());
                Assert.Equal(damaged, await FinancialStateAsync());
            } finally {
                // Restore only the exact genuine row deleted by this isolated case.
                await data.Rows(table).InsertOneAsync(actual);
            }
            Assert.Equal(intact, await FinancialStateAsync());
        }

        Assert.Equal(saved, await data.Service.LookupAsync(data.Verifier, D.Operation, key));
        var restoredView = (await data.Service.ReadAsync(data.Reader, data.DisbursementId))["settlement"].AsBsonDocument;
        foreach (var field in saved) Assert.Equal(field.Value, restoredView[field.Name]);
        Assert.Equal(claim, await data.Rows(BankTransactionClaims.Collection).Find(new BsonDocument("_id", claim["_id"])).SingleAsync());
        Assert.Equal(1, await data.Rows(D.Events).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument("source.namespace", D.Operation)));
        Assert.Equal(intact, await FinancialStateAsync());

        Task<bool> CaptureBankDebitAsync() => new AuthorizationStore(data.Database, data.Unit, data.Clock).ReadAsync(data.Verifier,
            async (authority, tx, ct) => {
                var bank = data.Disbursement.Bank.Service;
                var captured = await bank.CaptureProcurementSettlementAsync(data.Verifier, authority, tx,
                    data.Disbursement.Bank.Statements.Selection.AccountId, data.Disbursement.Bank.Statements.Selection.AccountVersionId,
                    data.BankImportId, 2, ct);
                await captured.FenceAsync(ct); captured.RequireCurrent(); return true;
            }, CancellationToken.None);
        async Task<BsonDocument> FinancialStateAsync() {
            var state = await data.StateAsync();
            foreach (var name in new[] { "accountingBooks", "periods", "accountingPeriodHistory" })
                state[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
            state["journalPostingEffects"] = new BsonArray(await data.Rows("outboxEvents").Find(new BsonDocument("kind", "journal.posted")).Sort(new BsonDocument("_id", 1)).ToListAsync());
            return state;
        }
    }
}
