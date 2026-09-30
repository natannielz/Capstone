using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using B = UnitToko.Platform.Billing.BankReceiptBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class BankReceiptTests(MongoFixture fixture)
{
    [Fact] public async Task Changed_identity_namespace_cannot_recognize_a_physical_account_transfer_again() {
        await using var data=await BankReceiptTestData.CreateAsync(fixture); await data.ConfirmAsync(await data.PreviewAsync());
        await data.Rows(B.Policies).UpdateOneAsync(new BsonDocument("_id",920L),Builders<BsonDocument>.Update.Set("identityNamespace","different-domain").Inc("revision",1L));
        await data.Rows(BankStatementBson.Profiles).UpdateOneAsync(new BsonDocument("_id",910L),Builders<BsonDocument>.Update.Set("format.identityNamespace","different-domain").Inc("revision",1L));
        var selected=data.Statements.Selection with { ProfileRevision=2 }; var bytes=BankStatementTestData.Original(); var service=data.Statements.Service;
        var preview=await service.PreviewAsync(data.Statements.Maker,selected,bytes);
        var staged=await service.StageAsync(data.Statements.Maker,selected,bytes,"new-domain.csv",preview.Binding,"synthetic-bank-source",true,BankStatementTestData.Meta("changed-domain"));
        var before=await data.StateAsync(); var error=await Assert.ThrowsAsync<PersistenceConflictException>(()=>data.PreviewAsync(staged["importId"].AsInt64));
        Assert.Equal("bank_receipt_namespace_locked",error.Code); Assert.Equal(before,await data.StateAsync());
    }
    [Theory] [InlineData("missing")] [InlineData("repeated")] [InlineData("debit")]
    public async Task Unresolved_identity_and_debit_rows_never_become_receipts(string fault) {
        await using var data=await BankReceiptTestData.CreateAsync(fixture);
        var csv=System.Text.Encoding.UTF8.GetString(BankStatementTestData.Original(fault=="missing"?"":"0002"));
        if(fault=="debit") csv=csv.Replace(",C,",",D,");
        if(fault=="repeated") csv+=csv.Split("\r\n")[1]+"\r\n";
        var import=(await data.Statements.StageAsync("unresolved",System.Text.Encoding.UTF8.GetBytes(csv)))["importId"].AsInt64;
        var before=await data.StateAsync(); var failure=await Assert.ThrowsAsync<DomainValidationException>(()=>data.PreviewAsync(import));
        Assert.Equal("bank_receipt_identity_unresolved",failure.Code); Assert.Equal(before,await data.StateAsync());
    }
    [Fact] public async Task Later_approved_account_version_cannot_post_the_same_bank_transfer_again() {
        await using var data=await BankReceiptTestData.CreateAsync(fixture); var original=await data.ConfirmAsync(await data.PreviewAsync());
        var accounts=data.Statements.Accounts; var proposal=await accounts.ProposeAsync("account-v2",data.Statements.Selection.AccountId,1);
        await accounts.ApproveAsync(proposal,await accounts.ObserveAsync(proposal,key:"observe-v2"),"approve-v2");
        var version=await data.Rows(ReceivingAccountBson.Versions).Find(new BsonDocument { { "accountId",data.Statements.Selection.AccountId }, { "version",2L } }).SingleAsync();
        var selected=data.Statements.Selection with { AccountVersionId=version["_id"].AsInt64 }; var bytes=BankStatementTestData.Original();
        var preview=await data.Statements.Service.PreviewAsync(data.Statements.Maker,selected,bytes);
        var staged=await data.Statements.Service.StageAsync(data.Statements.Maker,selected,bytes,"later-version.csv",preview.Binding,"synthetic-current-bank-export",true,BankStatementTestData.Meta("later-version"));
        var repeated=await data.ConfirmAsync(await data.PreviewAsync(staged["importId"].AsInt64),"confirm-v2");
        Assert.Equal(original["receiptId"],repeated["receiptId"]); Assert.False(repeated["createdReceipt"].AsBoolean);
        Assert.Equal(1,await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument())); Assert.Equal(original,await data.Service.RecoverAsync(data.Reviewer,"confirm"));
    }
    [Fact] public async Task Independent_confirmation_posts_real_balanced_journal_once_and_recovery_uses_original_receipt() {
        await using var data=await BankReceiptTestData.CreateAsync(fixture); var preview=await data.PreviewAsync();
        var result=await data.ConfirmAsync(preview); Assert.True(result["createdReceipt"].AsBoolean);
        Assert.Equal(result,await data.ConfirmAsync(preview)); Assert.Equal(result,await data.Service.RecoverAsync(data.Reviewer,"confirm"));
        var receipt=await data.Service.ReadAsync(data.Reader,result["receiptId"].AsInt64);
        var draft=AccountingBson.ReadDraft(await data.Rows("journalEntries").Find(new BsonDocument()).SingleAsync());
        Assert.Equal(1000.25m,draft.Total.Value); Assert.Equal(1000.25m,draft.Lines.Single(line=>line.MappingKey=="bank_cash").Debit.Value);
        Assert.Equal(1000.25m,draft.Lines.Single(line=>line.MappingKey=="unidentified_cash").Credit.Value); Assert.Equal("2026-09-27",receipt["bookedDate"]);
        Assert.Single(await data.Service.ListAsync(data.Reader)); Assert.Single(await data.Service.ImportConfirmationsAsync(data.Reader,data.ImportId));
        foreach(var name in new[]{"paymentAllocations","paymentVerifications"}) Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
        await data.Rows(B.Policies).UpdateOneAsync(new BsonDocument("_id",920L),Builders<BsonDocument>.Update.Set("active",false));
        Assert.Equal(result,await data.ConfirmAsync(preview)); Assert.Equal(result,await data.Service.RecoverAsync(data.Reviewer,"confirm"));
    }
    [Fact] public async Task Overlapping_statements_race_to_one_cash_event_but_distinct_transfers_with_shared_reference_stay_distinct() {
        await using var data=await BankReceiptTestData.CreateAsync(fixture); var other=(await data.Statements.StageAsync("overlap",filename:"renamed.csv"))["importId"].AsInt64;
        var a=await data.PreviewAsync(); var b=await data.PreviewAsync(other);
        var attempts=await Task.WhenAll(Record.ExceptionAsync(()=>data.ConfirmAsync(a,"a")),Record.ExceptionAsync(()=>data.ConfirmAsync(b,"b")));
        Assert.Contains(attempts,value=>value is null);
        foreach(var failure in attempts.Where(value=>value is not null)) Assert.Equal("version_conflict",Assert.IsType<PersistenceConflictException>(failure).Code);
        // The bounded transaction may return a known conflict. Replay the original keys after the competing writer finishes.
        var results=new[] { await data.ConfirmAsync(a,"a"),await data.ConfirmAsync(b,"b") };
        Assert.Equal(results[0]["receiptId"],results[1]["receiptId"]); Assert.Single(results,result=>result["createdReceipt"].AsBoolean);
        Assert.Equal(1,await data.Rows(B.Receipts).CountDocumentsAsync(new BsonDocument())); Assert.Equal(1,await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        var next=(await data.Statements.StageAsync("distinct",BankStatementTestData.Original("0002")))["importId"].AsInt64;
        await data.ConfirmAsync(await data.PreviewAsync(next),"distinct-credit",BankReceiptTestData.Observation("0002"));
        Assert.Equal(2,await data.Rows(B.Receipts).CountDocumentsAsync(new BsonDocument())); Assert.Equal(2,await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact] public async Task Same_key_retries_and_competing_new_keys_never_duplicate_receipt_or_row_claim() {
        await using var data=await BankReceiptTestData.CreateAsync(fixture); var preview=await data.PreviewAsync();
        var attempts=await Task.WhenAll(Record.ExceptionAsync(()=>data.ConfirmAsync(preview,"same")),Record.ExceptionAsync(()=>data.ConfirmAsync(preview,"same")));
        Assert.Contains(attempts,value=>value is null);
        foreach(var failure in attempts.Where(value=>value is not null)) Assert.Equal("version_conflict",Assert.IsType<PersistenceConflictException>(failure).Code);
        var result=await data.ConfirmAsync(preview,"same"); Assert.Equal(result,await data.Service.RecoverAsync(data.Reviewer,"same"));
        var error=await Assert.ThrowsAsync<PersistenceConflictException>(()=>data.ConfirmAsync(preview,"different")); Assert.Equal("bank_receipt_row_already_reviewed",error.Code);
        Assert.Equal(1,await data.Rows(B.Confirmations).CountDocumentsAsync(new BsonDocument()));
    }
    [Fact] public async Task Identity_collision_or_manual_observation_mismatch_cannot_post_or_retarget_cash() {
        await using var data=await BankReceiptTestData.CreateAsync(fixture); var preview=await data.PreviewAsync(); var before=await data.StateAsync();
        var error=await Assert.ThrowsAsync<DomainValidationException>(()=>data.ConfirmAsync(preview,observation:BankReceiptTestData.Observation(amount:1m)));
        Assert.Equal("bank_receipt_observation_mismatch",error.Code); Assert.Equal(before,await data.StateAsync());
        await data.ConfirmAsync(preview);
        var changed=(await data.Statements.StageAsync("conflicting",BankStatementTestData.Original(amount:"2000.25")))["importId"].AsInt64;
        before=await data.StateAsync(); var conflict=await Assert.ThrowsAsync<PersistenceConflictException>(()=>data.PreviewAsync(changed));
        Assert.Equal("bank_receipt_identity_conflict",conflict.Code); Assert.Equal(before,await data.StateAsync());
    }
    [Theory] [InlineData("policy")] [InlineData("profile")] [InlineData("period")] [InlineData("chart")] [InlineData("audit")] [InlineData("outbox")] [InlineData("grant")]
    public async Task Missing_current_authority_policy_or_required_effect_rolls_back_all_financial_effects(string fault) {
        await using var data=await BankReceiptTestData.CreateAsync(fixture); var preview=await data.PreviewAsync();
        if(fault=="policy") await data.Rows(B.Policies).UpdateOneAsync(new BsonDocument("_id",920L),Builders<BsonDocument>.Update.Set("active",false));
        if(fault=="profile") await data.Rows(BankStatementBson.Profiles).UpdateOneAsync(new BsonDocument("_id",910L),Builders<BsonDocument>.Update.Set("active",false));
        if(fault=="period") await data.Rows("accountingBooks").UpdateOneAsync(new BsonDocument("_id",1L),Builders<BsonDocument>.Update.Set("closedThrough","2026-09"));
        if(fault=="chart") await data.Rows("accountingChartVersions").DeleteManyAsync(new BsonDocument());
        if(fault=="grant") await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId",data.Reviewer.UserId),Builders<BsonDocument>.Update.Set("status","revoked"));
        if(fault is "audit" or "outbox") await data.Statements.Accounts.Catalog.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod",fault=="audit"?"auditEvents":"outboxEvents" },
            { "validator",new BsonDocument("$and",new BsonArray { new BsonDocument("kind",new BsonDocument("$ne",B.Operation)) }) }, { "validationLevel","strict" }, { "validationAction","error" } });
        var before=await data.StateAsync(); Assert.NotNull(await Record.ExceptionAsync(()=>data.ConfirmAsync(preview))); Assert.Equal(before,await data.StateAsync());
        Assert.False(await data.Rows(B.Receipts).Find(new BsonDocument()).AnyAsync()); Assert.False(await data.Rows("journalEntries").Find(new BsonDocument()).AnyAsync());
    }
    [Fact] public async Task Permissions_and_current_person_equivalence_hold_after_role_or_account_changes() {
        await using var data=await BankReceiptTestData.CreateAsync(fixture);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(()=>data.Service.PreviewAsync(data.Reader,data.ImportId,2));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(()=>data.Service.PreviewAsync(data.Other,data.ImportId,2));
        var preview=await data.PreviewAsync(); var result=await data.ConfirmAsync(preview);
        Assert.Empty(await data.Service.ListAsync(data.Other)); Assert.Null(await data.Service.RecoverAsync(data.Other,"confirm"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(()=>data.Service.ReadAsync(data.Other,result["receiptId"].AsInt64));
        var maker=data.Statements.Maker.UserId; var reviewer=data.Reviewer.UserId;
        await new MongoUnitOfWork(data.Statements.Accounts.Catalog.Client).ExecuteAsync(async (tx,ct)=>{
            await data.Rows("personRecords").UpdateOneAsync(tx,new BsonDocument("_id",PersonTestData.PersonId(reviewer)),Builders<BsonDocument>.Update.Set("currentEquivalenceId",PersonTestData.GroupId(maker)).Inc("version",1L),cancellationToken:ct);
            await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx,new BsonDocument("_id",PersonTestData.GroupId(maker)),Builders<BsonDocument>.Update.Set("memberPersonIds",new BsonArray { PersonTestData.PersonId(maker),PersonTestData.PersonId(reviewer) }).Set("memberUserIds",new BsonArray { maker,reviewer }).Inc("version",1L),cancellationToken:ct);
            await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx,new BsonDocument("_id",PersonTestData.GroupId(reviewer)),Builders<BsonDocument>.Update.Set("status","superseded").Inc("version",1L),cancellationToken:ct); return true;
        });
        var before=await data.StateAsync(); var error=await Assert.ThrowsAsync<DomainValidationException>(()=>data.Service.RecoverAsync(data.Reviewer,"confirm"));
        Assert.Equal("independent_person_required",error.Code); Assert.Equal(before,await data.StateAsync());
    }
    [Theory] [InlineData("receipt")] [InlineData("journal")] [InlineData("source")] [InlineData("anchor")] [InlineData("audit")] [InlineData("command")]
    public async Task Missing_financial_provenance_is_not_repaired_by_replay(string fault) {
        await using var data=await BankReceiptTestData.CreateAsync(fixture); var preview=await data.PreviewAsync(); var result=await data.ConfirmAsync(preview);
        if(fault=="receipt") await data.Rows(B.Receipts).DeleteOneAsync(new BsonDocument("_id",result["receiptId"]));
        if(fault=="journal") await data.Rows("journalEntries").DeleteManyAsync(new BsonDocument());
        if(fault=="source") await data.Rows(BankStatementBson.Imports).DeleteOneAsync(new BsonDocument("_id",data.ImportId));
        if(fault=="anchor") await data.Rows(B.Accounts).DeleteManyAsync(new BsonDocument());
        if(fault=="audit") await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("kind",B.Operation));
        if(fault=="command") await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("operation",B.Operation));
        var before=await data.StateAsync(); Assert.NotNull(await Record.ExceptionAsync(()=>data.ConfirmAsync(preview))); Assert.Equal(before,await data.StateAsync());
    }
}
