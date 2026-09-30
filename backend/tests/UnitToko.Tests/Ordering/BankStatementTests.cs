using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using B = UnitToko.Platform.Billing.BankStatementBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class BankStatementTests(MongoFixture fixture)
{
    [Fact] public async Task Actual_mongo_staging_preserves_originals_and_provenance_without_recognizing_money() {
        await using var data=await BankStatementTestData.CreateAsync(fixture); var bytes=BankStatementTestData.Original();
        var preview=await data.Service.PreviewAsync(data.Maker,data.Selection,bytes); Assert.True(preview.Preview.CanStage);
        var receipt=await data.StageAsync(); var id=receipt["importId"].AsInt64;
        Assert.Equal(receipt,await data.Service.RecoverAsync(data.Maker,"stage"));
        Assert.Equal(receipt,await data.Service.StageAsync(data.Maker,data.Selection,bytes,"synthetic.csv",preview.Binding,"synthetic-private-statement-reference",true,BankStatementTestData.Meta("stage")));
        Assert.Equal(bytes,await data.Service.ReadOriginalAsync(data.Reader,id));
        var detail=await data.Service.ReadAsync(data.Reader,id); Assert.False(detail.Contains("original")); Assert.False(detail.Contains("personAtImport"));
        Assert.Equal("staged",detail["state"]); Assert.Equal("0001",detail["preview"]["rows"][0]["transactionId"]);
        Assert.Equal("Unknown",(await data.Rows(B.Imports).Find(new BsonDocument("_id",id)).SingleAsync())["personAtImport"]["verification"]);
        var list=await data.Service.ListImportsAsync(data.Reader); Assert.Single(list); Assert.DoesNotContain("0000123456789",list[0].ToJson());
        Assert.Single(await data.Service.ListAccountsAsync(data.Reader,910,1));
        Assert.Equal(1,await data.Rows(B.Imports).CountDocumentsAsync(new BsonDocument()));
        foreach(var name in new[]{"journalEntries","bankReceipts","paymentAllocations"}) Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
        var audit=await data.Rows("auditEvents").Find(new BsonDocument("kind",B.Operation)).SingleAsync(); Assert.DoesNotContain("Synthetic buyer",audit.ToJson()); Assert.DoesNotContain("0000123456789",audit.ToJson());
    }
    [Fact] public async Task Renamed_overlapping_imports_retain_same_candidate_but_make_no_uniqueness_or_cash_claim() {
        await using var data=await BankStatementTestData.CreateAsync(fixture);
        var first=await data.StageAsync(); var second=await data.StageAsync("renamed",filename:"renamed.csv");
        var a=await data.Service.ReadAsync(data.Reader,first["importId"].AsInt64); var b=await data.Service.ReadAsync(data.Reader,second["importId"].AsInt64);
        Assert.NotEqual(first["importId"],second["importId"]); Assert.Equal(a["preview"],b["preview"]);
        var distinct=await data.StageAsync("distinct",BankStatementTestData.Original("0002")); var c=await data.Service.ReadAsync(data.Reader,distinct["importId"].AsInt64);
        Assert.Equal(a["preview"]["rows"][0]["reference"],c["preview"]["rows"][0]["reference"]);
        Assert.NotEqual(a["preview"]["rows"][0]["identityCandidate"],c["preview"]["rows"][0]["identityCandidate"]);
        Assert.False(await data.Rows("journalEntries").Find(new BsonDocument()).AnyAsync());
    }
    [Fact] public async Task Server_authority_and_exact_preview_bind_all_inputs_and_original_receipt() {
        await using var data=await BankStatementTestData.CreateAsync(fixture); var bytes=BankStatementTestData.Original();
        var preview=await data.Service.PreviewAsync(data.Maker,data.Selection,bytes);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(()=>data.Service.PreviewAsync(data.Reader,data.Selection,bytes));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(()=>data.Service.PreviewAsync(data.Other,data.Selection,bytes));
        Assert.Empty(await data.Service.ListProfilesAsync(data.Other));
        await Assert.ThrowsAsync<PersistenceConflictException>(()=>data.Service.StageAsync(data.Maker,data.Selection,BankStatementTestData.Original(amount:"2.00"),"changed.csv",preview.Binding,"source",true,BankStatementTestData.Meta("changed")));
        Assert.False(await data.Rows(B.Imports).Find(new BsonDocument()).AnyAsync());
        var saved=await data.StageAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(()=>data.Service.StageAsync(data.Maker,data.Selection,bytes,"renamed.csv",preview.Binding,"synthetic-private-statement-reference",true,BankStatementTestData.Meta("stage")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(()=>data.Service.ReadOriginalAsync(data.Other,saved["importId"].AsInt64));
        Assert.Null(await data.Service.RecoverAsync(data.Maker,"unknown"));
    }
    [Fact] public async Task Profile_revocation_and_later_account_versions_block_new_staging_but_preserve_original_recovery() {
        await using var data=await BankStatementTestData.CreateAsync(fixture); var saved=await data.StageAsync(); var bytes=BankStatementTestData.Original();
        var preview=await data.Service.PreviewAsync(data.Maker,data.Selection,bytes);
        var proposal=await data.Accounts.ProposeAsync("version2",data.Selection.AccountId,1);
        await data.Accounts.ApproveAsync(proposal,await data.Accounts.ObserveAsync(proposal,key:"observe-v2"),"approve-v2");
        await Assert.ThrowsAsync<PersistenceConflictException>(()=>data.Service.StageAsync(data.Maker,data.Selection,bytes,"synthetic.csv",preview.Binding,"source",true,BankStatementTestData.Meta("stale")));
        await data.Rows(B.Profiles).UpdateOneAsync(new BsonDocument("_id",910L),Builders<BsonDocument>.Update.Set("active",false));
        Assert.Equal(saved,await data.Service.RecoverAsync(data.Maker,"stage"));
        Assert.Equal(saved,await data.Service.StageAsync(data.Maker,data.Selection,bytes,"synthetic.csv",preview.Binding,"synthetic-private-statement-reference",true,BankStatementTestData.Meta("stage")));
        Assert.Equal(bytes,await data.Service.ReadOriginalAsync(data.Reader,saved["importId"].AsInt64));
        Assert.Equal(1,await data.Rows(B.Imports).CountDocumentsAsync(new BsonDocument()));
    }
    [Theory] [InlineData("receipt")] [InlineData("audit")] [InlineData("outbox")] [InlineData("original")] [InlineData("account")]
    public async Task Missing_or_changed_provenance_cannot_be_repaired_by_a_retry(string fault) {
        await using var data=await BankStatementTestData.CreateAsync(fixture); var result=await data.StageAsync(); var row=await data.Rows(B.Imports).Find(new BsonDocument("_id",result["importId"])).SingleAsync();
        if(fault=="receipt") await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { { "operation",B.Operation }, { "key","stage" } });
        if(fault=="audit") await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("_id",row["auditId"]));
        if(fault=="outbox") await data.Rows("outboxEvents").DeleteOneAsync(new BsonDocument("sourceKey",B.SourceKey(row)));
        if(fault=="original") await data.Rows(B.Imports).UpdateOneAsync(new BsonDocument("_id",row["_id"]),Builders<BsonDocument>.Update.Set("original",new BsonBinaryData(BankStatementTestData.Original("OTHER"))));
        if(fault=="account") await data.Rows(ReceivingAccountBson.Versions).DeleteOneAsync(new BsonDocument("_id",data.Selection.AccountVersionId));
        var before=await data.StateAsync();
        Assert.NotNull(await Record.ExceptionAsync(()=>data.Service.RecoverAsync(data.Maker,"stage")));
        Assert.Equal(before,await data.StateAsync());
    }
    [Fact] public async Task Simultaneous_same_key_commits_one_import_and_downstream_failure_rolls_back_source_and_receipt() {
        await using var data=await BankStatementTestData.CreateAsync(fixture); var bytes=BankStatementTestData.Original(); var preview=await data.Service.PreviewAsync(data.Maker,data.Selection,bytes);
        Task<BsonDocument> Stage(string key)=>data.Service.StageAsync(data.Maker,data.Selection,bytes,"synthetic.csv",preview.Binding,"source",true,BankStatementTestData.Meta(key));
        var results=await Task.WhenAll(Stage("race"),Stage("race")); Assert.Equal(results[0],results[1]); Assert.Equal(1,await data.Rows(B.Imports).CountDocumentsAsync(new BsonDocument()));
        await data.Accounts.Catalog.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod","outboxEvents" }, { "validator",new BsonDocument("$expr",false) }, { "validationLevel","strict" }, { "validationAction","error" } });
        var before=await data.StateAsync(); Assert.NotNull(await Record.ExceptionAsync(()=>Stage("rollback"))); Assert.Equal(before,await data.StateAsync());
    }
}
