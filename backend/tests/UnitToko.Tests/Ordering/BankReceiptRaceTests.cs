using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Tests.Authorization;
using B = UnitToko.Platform.Billing.BankReceiptBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class BankReceiptRaceTests(MongoFixture fixture)
{
    [Theory] [InlineData("policy")] [InlineData("mfa")] [InlineData("session")]
    public async Task Expiry_during_final_owner_authorization_cannot_commit_a_receipt(string kind) {
        await using var data=await BankReceiptTestData.CreateAsync(fixture); var start=data.Statements.Accounts.Catalog.Clock.UtcNow;
        if(kind=="policy") await data.Rows(B.Policies).UpdateOneAsync(new BsonDocument("_id",920L),Builders<BsonDocument>.Update.Set("expiresAt",start.AddMinutes(1).UtcDateTime));
        if(kind=="session") await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id",data.Reviewer.Session!.Id),Builders<BsonDocument>.Update.Set("idleExpiresAt",start.AddMinutes(1).UtcDateTime));
        var preview=await data.PreviewAsync(); var before=await data.StateAsync();
        var writing=false; var selected=0; var advanced=false;
        var settings=MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI"));
        settings.ClusterConfigurator=builder=>{
            builder.Subscribe<CommandStartedEvent>(command=>{
                if(command.Command.TryGetValue("update",out var updated) && updated==B.Policies) writing=true;
                if(writing && command.Command.TryGetValue("find",out var find) && find=="identitySessions") selected=command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command=>{ if(!advanced && command.RequestId==selected) { advanced=true; data.Statements.Accounts.Catalog.Clock.UtcNow=start.AddMinutes(kind=="mfa"?6:2); } });
        };
        var service=data.NewService(new MongoClient(settings));
        Assert.NotNull(await Record.ExceptionAsync(()=>service.ConfirmAsync(data.Reviewer,data.ImportId,2,preview["binding"].AsString,BankReceiptTestData.Observation(),"synthetic-source",true,BankReceiptTestData.Meta("expire"))));
        Assert.True(advanced); Assert.Equal(before,await data.StateAsync());
    }
    [Theory] [InlineData("policy")] [InlineData("close")]
    public async Task Concurrent_revocation_or_period_close_reloads_and_denies_confirmation(string kind) {
        await using var data=await BankReceiptTestData.CreateAsync(fixture); var preview=await data.PreviewAsync();
        using var release=new ManualResetEventSlim(); var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var paused=0;
        var settings=MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI"));
        settings.ClusterConfigurator=builder=>builder.Subscribe<CommandStartedEvent>(command=>{
            if(command.Command.TryGetValue("insert",out var insert) && insert==B.Receipts && Interlocked.Exchange(ref paused,1)==0) {
                entered.TrySetResult(); if(!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Receipt race did not resume");
            }
        });
        var service=data.NewService(new MongoClient(settings));
        var pending=Task.Run(()=>Record.ExceptionAsync(()=>service.ConfirmAsync(data.Reviewer,data.ImportId,2,preview["binding"].AsString,BankReceiptTestData.Observation(),"synthetic-source",true,BankReceiptTestData.Meta("revoke"))));
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if(kind=="policy") await data.Rows(B.Policies).UpdateOneAsync(new BsonDocument("_id",920L),Builders<BsonDocument>.Update.Set("active",false));
            else await data.Rows("accountingBooks").UpdateOneAsync(new BsonDocument("_id",1L),Builders<BsonDocument>.Update.Set("closedThrough","2026-09").Inc("version",1L).Inc("fence",1L));
        } finally { release.Set(); }
        Assert.NotNull(await pending); Assert.False(await data.Rows(B.Receipts).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows("journalEntries").Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument("operation",B.Operation)).AnyAsync());
    }
}
