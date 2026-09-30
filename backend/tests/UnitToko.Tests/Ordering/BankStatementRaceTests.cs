using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Tests.Authorization;
using B = UnitToko.Platform.Billing.BankStatementBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class BankStatementRaceTests(MongoFixture fixture)
{
    [Theory] [InlineData("profile")] [InlineData("mfa")] [InlineData("session")]
    public async Task Expiry_during_final_owner_authorization_cannot_commit_an_import(string kind) {
        await using var data=await BankStatementTestData.CreateAsync(fixture); var start=data.Accounts.Catalog.Clock.UtcNow;
        if(kind=="profile") await data.Rows(B.Profiles).UpdateOneAsync(new BsonDocument("_id",910L),Builders<BsonDocument>.Update.Set("expiresAt",start.AddMinutes(1).UtcDateTime));
        if(kind=="session") await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id",data.Maker.Session!.Id),Builders<BsonDocument>.Update.Set("idleExpiresAt",start.AddMinutes(1).UtcDateTime));
        var bytes=BankStatementTestData.Original(); var preview=await data.Service.PreviewAsync(data.Maker,data.Selection,bytes); var before=await data.StateAsync();
        var writing=false; var selected=0; var advanced=false;
        var settings=MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI"));
        settings.ClusterConfigurator=builder=>{
            builder.Subscribe<CommandStartedEvent>(command=>{
                if(command.Command.TryGetValue("update",out var updated) && updated==B.Profiles) writing=true;
                if(writing && command.Command.TryGetValue("find",out var find) && find=="identitySessions") selected=command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command=>{ if(!advanced && command.RequestId==selected) { advanced=true; data.Accounts.Catalog.Clock.UtcNow=start.AddMinutes(kind=="mfa"?6:2); } });
        };
        var service=data.NewService(new MongoClient(settings));
        Assert.NotNull(await Record.ExceptionAsync(()=>service.StageAsync(data.Maker,data.Selection,bytes,"synthetic.csv",preview.Binding,"synthetic-source",true,BankStatementTestData.Meta("expire"))));
        Assert.True(advanced); Assert.Equal(before,await data.StateAsync());
    }
    [Fact] public async Task Concurrent_profile_revocation_conflicts_reloads_and_denies_staging() {
        await using var data=await BankStatementTestData.CreateAsync(fixture); var bytes=BankStatementTestData.Original(); var preview=await data.Service.PreviewAsync(data.Maker,data.Selection,bytes);
        using var release=new ManualResetEventSlim(); var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var paused=0;
        var settings=MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI"));
        settings.ClusterConfigurator=builder=>builder.Subscribe<CommandStartedEvent>(command=>{
            if(command.Command.TryGetValue("insert",out var insert) && insert==B.Imports && Interlocked.Exchange(ref paused,1)==0) {
                entered.TrySetResult(); if(!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Statement race did not resume");
            }
        });
        var service=data.NewService(new MongoClient(settings));
        var pending=Task.Run(()=>Record.ExceptionAsync(()=>service.StageAsync(data.Maker,data.Selection,bytes,"synthetic.csv",preview.Binding,"synthetic-source",true,BankStatementTestData.Meta("revoke"))));
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); await data.Rows(B.Profiles).UpdateOneAsync(new BsonDocument("_id",910L),Builders<BsonDocument>.Update.Set("active",false)); }
        finally { release.Set(); }
        Assert.NotNull(await pending); Assert.False(await data.Rows(B.Imports).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument("operation",B.Operation)).AnyAsync());
    }
}
