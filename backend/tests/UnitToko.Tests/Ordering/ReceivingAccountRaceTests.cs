using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Billing;
using UnitToko.Tests.Authorization;
using B = UnitToko.Platform.Billing.ReceivingAccountBson;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class ReceivingAccountRaceTests(MongoFixture fixture)
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Session_expiry_after_final_global_read_denies_empty_list_and_recovery(bool recovery) {
        await using var data = await ReceivingAccountTestData.CreateAsync(fixture);
        var start = data.Catalog.Clock.UtcNow;
        await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Approver.Session!.Id), Builders<BsonDocument>.Update.Set("idleExpiresAt", start.AddMinutes(1).UtcDateTime));
        var before = await data.StateAsync(); var audited = false; var selected = 0; var advanced = false;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI"));
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (command.Command.TryGetValue("insert", out var collection) && collection == "auditEvents") audited = true;
                if (audited && command.Command.TryGetValue("find", out var find) && find == "identitySessions") selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => { if (!advanced && selected == command.RequestId) { advanced = true; data.Catalog.Clock.UtcNow = start.AddMinutes(2); } });
        };
        var service = data.NewService(new MongoClient(settings));
        await Assert.ThrowsAsync<UnitToko.Platform.Authorization.AuthorizationDeniedException>(async () => {
            if (recovery) await service.RecoverAsync(data.Approver, B.Prefix + "approve", "missing"); else await service.ListProposalsAsync(data.Approver);
        });
        Assert.True(advanced); Assert.Equal(before, await data.StateAsync());
    }
    [Theory]
    [InlineData("policy", false)] [InlineData("observation", false)] [InlineData("person", false)] [InlineData("person", true)]
    [InlineData("mfa", false)] [InlineData("mfa", true)] [InlineData("session", false)] [InlineData("session", true)]
    [InlineData("policy-generic", false)] [InlineData("observation-generic", false)] [InlineData("person-generic", false)]
    public async Task Expiry_after_workflow_fences_aborts_new_decision_and_recovery(string expiry, bool recovery) {
        await using var data = await ReceivingAccountTestData.CreateAsync(fixture); var proposal = await data.ProposeAsync(); var observed = await data.ObserveAsync(proposal);
        if (recovery) await data.ApproveAsync(proposal, observed);
        var start = data.Catalog.Clock.UtcNow;
        var genericTail = expiry.EndsWith("-generic", StringComparison.Ordinal); var kind = expiry.Replace("-generic", "", StringComparison.Ordinal);
        if (kind == "policy") { // New proposal captures this policy's shorter approved lifetime.
            await data.Rows(B.Policies).UpdateOneAsync(new BsonDocument("_id", 810L), Builders<BsonDocument>.Update.Set("expiresAt", start.AddMinutes(1).UtcDateTime));
            proposal = await data.ProposeAsync("short-policy"); observed = await data.ObserveAsync(proposal, key: "short-policy-observation");
        }
        if (kind == "observation") {
            await data.Rows(B.Policies).UpdateOneAsync(new BsonDocument("_id", 810L), Builders<BsonDocument>.Update.Set("maxObservationAgeSeconds", 60L));
            proposal = await data.ProposeAsync("short-observation"); observed = await data.ObserveAsync(proposal, key: "short-observation-observe");
        }
        if (kind == "person") await data.Rows("personAccountBindings").UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId), Builders<BsonDocument>.Update.Set("expiresAt", start.AddMinutes(1).UtcDateTime));
        if (expiry == "session") await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Approver.Session!.Id), Builders<BsonDocument>.Update.Set("idleExpiresAt", start.AddMinutes(1).UtcDateTime));
        var before = await data.StateAsync(); var fenced = false; var selected = 0; var advanced = false;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI"));
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (command.Command.TryGetValue("update", out var collection) && collection == B.Heads) fenced = true;
                if (fenced && command.Command.TryGetValue("find", out var find) && find == "identitySessions" && (!genericTail || command.Command.Contains("projection"))) selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (!advanced && selected == command.RequestId) { advanced = true; data.Catalog.Clock.UtcNow = start.AddMinutes(expiry == "mfa" ? 6 : 2); }
            });
        };
        var service = data.NewService(new MongoClient(settings));
        Assert.NotNull(await Record.ExceptionAsync(async () => {
            if (recovery) await service.RecoverAsync(data.Approver, B.Prefix + "approve", "approve");
            else await service.DecideAsync(data.Approver, proposal["proposalId"].AsInt64, 1, ReceivingAccountDecision.Approve, observed["recordId"].AsInt64, ReceivingAccountTestData.Meta("approve"));
        }));
        Assert.True(advanced); Assert.Equal(before, await data.StateAsync());
    }
    [Theory]
    [InlineData("policy")] [InlineData("observer")]
    public async Task Concurrent_revocation_retries_old_snapshot_and_preserves_no_approval(string change) {
        await using var data = await ReceivingAccountTestData.CreateAsync(fixture); var proposal = await data.ProposeAsync(); var observed = await data.ObserveAsync(proposal);
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var paused = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI"));
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command => {
            if (command.Command.TryGetValue("update", out var collection) && collection == "identities" && Interlocked.Exchange(ref paused, 1) == 0) {
                entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Receiving account race did not resume");
            }
        });
        var service = data.NewService(new MongoClient(settings));
        var pending = Task.Run(() => service.DecideAsync(data.Approver, proposal["proposalId"].AsInt64, 1, ReceivingAccountDecision.Approve, observed["recordId"].AsInt64, ReceivingAccountTestData.Meta("race")));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try {
            if (change == "policy") await data.Rows(B.Policies).UpdateOneAsync(new BsonDocument("_id", 810L), Builders<BsonDocument>.Update.Set("active", false));
            else await data.Rows("personAccountBindings").UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId), Builders<BsonDocument>.Update.Set("status", "suspended"));
        } finally { release.Set(); }
        Assert.NotNull(await Record.ExceptionAsync(() => pending));
        Assert.Equal(0, await data.Rows(B.Versions).CountDocumentsAsync(new BsonDocument())); Assert.Equal(0, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", "race")));
    }
}
