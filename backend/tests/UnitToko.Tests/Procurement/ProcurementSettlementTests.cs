using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using D = UnitToko.Tests.Procurement.ProcurementSettlementTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ProcurementSettlementTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Genuine_record_and_bank_debit_post_one_advance_journal_with_exact_claim_and_original_key_recovery()
    {
        await using var data = await D.CreateAsync(fixture);
        var before = await data.Disbursement.PreservedAsync(); var recorded = await data.Disbursement.StateAsync();
        var preview = await data.PreviewAsync(); var result = await data.VerifyAsync(preview);
        Assert.Equal("bank_debit_verified", result["status"].AsString); Assert.Equal("6000.00", result["amount"].AsString);
        Assert.Equal(result, await data.VerifyAsync(preview));
        Assert.Equal(result, await data.Service.LookupAsync(data.Verifier, D.Operation, "settlement-verify"));
        Assert.Null(await data.Service.LookupAsync(data.Verifier, D.Operation, "unknown-key"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.LookupAsync(data.Disbursement.Actor, D.Operation, "settlement-verify"));
        var row = await data.Rows(D.Events).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(data.DisbursementId, row["disbursementId"].AsInt64); Assert.Equal(data.Verifier.UserId, row["actorId"].AsInt64);
        var claim = await data.Rows("bankTransactionClaims").Find(new BsonDocument("ownerKind", "procurementDisbursementSettlement")).SingleAsync();
        Assert.Equal(row["_id"], claim["ownerId"]); Assert.Equal(row["snapshotHash"], claim["ownerHash"]); Assert.Equal(row["canonicalIdentity"], claim["_id"]);
        Assert.Equal(2, claim["schemaVersion"].AsInt32); Assert.Equal("debit", claim["direction"].AsString);
        Assert.Equal(before["bankTransactionClaims"].AsBsonArray.Count + 1, await data.Rows("bankTransactionClaims").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(before["journalEntries"].AsBsonArray.Count + 1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        var journal = await data.Rows("journalEntries").Find(new BsonDocument("_id", row["journalId"])).SingleAsync();
        var lines = journal["lines"].AsBsonArray; Assert.Equal(2, lines.Count);
        var advance = lines.Single(line => line["mappingKey"] == "supplier_advance"); var cash = lines.Single(line => line["mappingKey"] == "bank_cash");
        Assert.Equal(803L, advance["accountId"].AsInt64); Assert.Equal(6000m, advance["debit"].AsDecimal); Assert.Equal(0m, advance["credit"].AsDecimal);
        Assert.Equal(801L, cash["accountId"].AsInt64); Assert.Equal(0m, cash["debit"].AsDecimal); Assert.Equal(6000m, cash["credit"].AsDecimal);
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, D.Operation)));
        var view = await data.Service.ReadAsync(data.Reader, data.DisbursementId);
        Assert.DoesNotContain(SupplierBankTestData.Details.AccountNumber, view.ToJson());
        Assert.Equal(recorded, await data.Disbursement.StateAsync());
        var after = await data.Disbursement.PreservedAsync();
        foreach (var name in new[] { "bankTransactionClaims", "journalEntries" }) { before.Remove(name); after.Remove(name); }
        Assert.Equal(before, after);
    }

    [WindowsStorageFact]
    public async Task Fully_granted_recorder_and_bank_importer_cannot_independently_verify_their_own_work()
    {
        await using var data = await D.CreateAsync(fixture); var source = data.Disbursement;
        string[] permissions = [Permissions.ProcurementSettlementRead, Permissions.DisbursementVerify, Permissions.ProcurementDisbursementRead,
            Permissions.ProcurementDisbursementEvidenceRead, Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead,
            Permissions.BankReceiptRead, Permissions.BankStatementRead, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead];
        await source.Base.AddGrantAsync(source.Actor, 889981, CatalogAuthoringTestData.Shop, permissions);
        await source.Base.AddGrantAsync(source.Bank.Statements.Maker, 889982, CatalogAuthoringTestData.Shop, permissions);
        var before = await data.StateAsync();
        foreach (var actor in new[] { source.Actor, source.Bank.Statements.Maker }) {
            var error = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.PreviewAsync(actor));
            Assert.Equal("independent_person_required", error.Code);
        }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.PreviewAsync(data.Reader));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.PreviewAsync(data.Outsider));
        Assert.Equal(before, await data.StateAsync());
    }

    [WindowsStorageTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_verification_and_same_key_retries_cannot_duplicate_the_claim_or_cash_posting(bool sameKey)
    {
        await using var data = await D.CreateAsync(fixture); var preview = await data.PreviewAsync();
        var journals = await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument());
        async Task<(BsonDocument? Result, Exception? Error)> Attempt(string key) {
            try { return (await data.VerifyAsync(preview, key), null); } catch (Exception error) { return (null, error); }
        }
        var results = await Task.WhenAll(Attempt("racing-verify"), Attempt(sameKey ? "racing-verify" : "racing-other-key"));
        Assert.Contains(results, result => result.Error is null);
        if (sameKey) { Assert.All(results, result => Assert.Null(result.Error)); Assert.Equal(results[0].Result, results[1].Result); }
        else Assert.Single(results, result => result.Error is null);
        var settlement = await data.Rows(D.Events).Find(new BsonDocument()).SingleAsync();
        var claim = await data.Rows("bankTransactionClaims").Find(new BsonDocument("ownerKind", "procurementDisbursementSettlement")).SingleAsync();
        Assert.Equal(settlement["_id"], claim["ownerId"]); Assert.Equal(journals + 1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", D.Operation)));
    }

    [WindowsStorageFact]
    public async Task Withdrawn_policy_or_access_cannot_post_the_previously_reviewed_debit()
    {
        await using var data = await D.CreateAsync(fixture); var preview = await data.PreviewAsync(); var before = await data.StateAsync();
        await data.Rows(D.Policies).UpdateOneAsync(new BsonDocument("_id", D.PolicyId), Builders<BsonDocument>.Update.Set("active", false));
        await Assert.ThrowsAsync<UnitToko.Platform.Mongo.PersistenceConflictException>(() => data.VerifyAsync(preview, "withdrawn-policy"));
        Assert.Equal(before, await data.StateAsync());
        await data.Rows(D.Policies).UpdateOneAsync(new BsonDocument("_id", D.PolicyId), Builders<BsonDocument>.Update.Set("active", true));
        await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", data.Verifier.UserId),
            Builders<BsonDocument>.Update.Pull("permissions", Permissions.DisbursementVerify));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.VerifyAsync(preview, "revoked-verifier"));
        Assert.Equal(before, await data.StateAsync());
    }
}
