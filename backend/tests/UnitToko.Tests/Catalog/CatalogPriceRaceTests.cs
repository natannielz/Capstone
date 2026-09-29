using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed class CatalogPriceRaceTests(MongoFixture fixture)
{
    [Theory]
    [InlineData("person", false)] [InlineData("person", true)]
    [InlineData("mfa", false)] [InlineData("mfa", true)]
    [InlineData("grant", false)] [InlineData("grant", true)]
    [InlineData("session", false)] [InlineData("session", true)]
    public async Task Late_expiry_after_price_workflow_fences_aborts_new_command_or_replay(string expiry, bool replay)
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var proposal = await data.ProposeAsync();
        if (replay) await data.ApproveAsync(proposal);
        var start = data.Catalog.Clock.UtcNow;
        if (expiry == "person" || expiry == "grant")
            await data.Rows(expiry == "person" ? "personAccountBindings" : "authorizationGrants").UpdateOneAsync(
                new BsonDocument("_id", data.Checker.UserId),
                Builders<BsonDocument>.Update.Set("expiresAt", start.AddMinutes(1).UtcDateTime));
        if (expiry == "session")
            await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Checker.Session!.Id),
                Builders<BsonDocument>.Update.Set("idleExpiresAt", start.AddMinutes(1).UtcDateTime));
        var before = await data.StateAsync();
        var catalogFenced = false; var selected = 0; var observed = false;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ??
            "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (command.Command.TryGetValue("update", out var collection) && collection == "skus") catalogFenced = true;
                if (catalogFenced && command.Command.TryGetValue("find", out var find) && find == "identitySessions" &&
                    command.Command.TryGetValue("projection", out var projection) && projection.AsBsonDocument.Contains("idleExpiresAt") &&
                    projection.AsBsonDocument.Contains("absoluteExpiresAt")) selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (!observed && selected == command.RequestId) {
                    observed = true;
                    data.Catalog.Clock.UtcNow = start.AddMinutes(expiry == "mfa" ? 6 : 2);
                }
            });
        };
        var service = data.NewService(new MongoClient(settings));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DecideAsync(data.Checker,
            proposal["proposalId"].AsInt64, 1, CatalogPriceDecision.Approve, "Synthetic independent price review",
            CatalogPriceTestData.Meta("price-approve")));
        Assert.True(observed, "Expiry must occur after the workflow's catalog and session fences.");
        Assert.Equal(before, await data.StateAsync());
    }

    [Theory]
    [InlineData("head")] [InlineData("retirement")] [InlineData("grant")] [InlineData("person")] [InlineData("session")]
    public async Task Changed_authority_or_catalog_retries_old_snapshot_without_committing_price(string change)
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var proposal = await data.ProposeAsync();
        var second = change == "head" ? await data.ProposeAsync("second", "13000.00") : null;
        var other = change == "head" ? await data.Catalog.ActorAsync(8000, CatalogAuthoringTestData.Shop, Permissions.PricingApprove) : null;
        if (other is not null) await PersonTestData.SeedDistinctPeopleAsync(data.Catalog.Database, data.Catalog.Clock, other.UserId);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ??
            "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command => {
            if (command.Command.TryGetValue("update", out var collection) && collection == "identities" &&
                Interlocked.Exchange(ref paused, 1) == 0) {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Controlled price race did not resume");
            }
        });
        var service = data.NewService(new MongoClient(settings));
        var pending = Task.Run(() => service.DecideAsync(data.Checker, proposal["proposalId"].AsInt64, 1,
            CatalogPriceDecision.Approve, "Synthetic independent price review", CatalogPriceTestData.Meta("price-approve")));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try {
            if (change == "head") await data.Service.DecideAsync(other!, second!["proposalId"].AsInt64, 1,
                CatalogPriceDecision.Approve, "Other independent review", CatalogPriceTestData.Meta("other-approve"));
            if (change == "retirement") await data.Catalog.Service.RetireSkuAsync(data.Maker, new(new(data.SkuId), 1), CatalogPriceTestData.Meta("retire"));
            if (change == "session") await data.Catalog.Sessions.RevokeAsync(data.Catalog.Issued[data.Checker.UserId].OpaqueToken, "Synthetic concurrent revocation");
            if (change == "grant" || change == "person") await data.Rows(change == "grant" ? "authorizationGrants" : "personAccountBindings")
                .UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId),
                    Builders<BsonDocument>.Update.Set("status", change == "grant" ? "revoked" : "suspended"));
        }
        finally { release.Set(); }
        var error = await Record.ExceptionAsync(() => pending);
        Assert.NotNull(error);
        if (change == "head") Assert.Equal("catalog_price_changed", Assert.IsType<DomainValidationException>(error).Code);
        else Assert.IsType<AuthorizationDeniedException>(error);
        Assert.Equal(change == "head" ? 1 : 0, await data.Rows("priceVersions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", "price-approve")));
        Assert.Equal(0, await data.Rows("catalogPriceDecisions").CountDocumentsAsync(new BsonDocument("proposalId", proposal["proposalId"])));
    }
}
