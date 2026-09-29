using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourcePolicyServiceTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Revocation_after_loading_policy_hides_latest_and_individual_history(bool individual)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data, true); var service = Service(data);
        var proposed = await Propose(data, pool, maker);
        await service.DecideOperatingPolicyAsync(reviewer, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "Synthetic review", Meta("approve"));
        var before = await Effects(data); var captured = 0; var changed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "PolicyReadFinal-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == "find" && e.Command.GetValue("find", "") == StockSourcePolicyBson.Policies) captured = e.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.RequestId != captured || Interlocked.CompareExchange(ref changed, 1, 0) != 0) return;
                Rows(data, "authorizationGrants").UpdateOne(new BsonDocument("_id", 1101L), Builders<BsonDocument>.Update.Set("status", "revoked"));
            });
        };
        var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var candidate = new StockSourceSetupService(db, new(client), new(db), data.Clock);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
        {
            if (individual) await candidate.ReadOperatingPolicyProposalAsync(maker, pool, proposed["proposalId"].AsInt64);
            else await candidate.ReadOperatingPolicyAsync(maker, pool);
        });
        Assert.Equal(1, changed); Assert.Equal(before, await Effects(data));
    }
}