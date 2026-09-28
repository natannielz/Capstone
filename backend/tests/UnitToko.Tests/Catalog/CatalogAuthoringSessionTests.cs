using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Catalog;

public sealed partial class CatalogAuthoringServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_and_joined_buyer_reads_deny_session_expiry_after_final_session_await(bool joined)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync(); await data.AssortmentsAsync();
        await data.Service.SetPublicationsAsync(data.Maker, new(sku.Id, 1, [new(501)], [new(new(501), 1)]), Metadata("session-list"));
        var deadline = data.Clock.UtcNow.AddSeconds(1);
        await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Buyer.Session!.Id), U.Set("idleExpiresAt", deadline.UtcDateTime));
        var before = await data.StateAsync(); var selected = 0; var observed = 0; var settings = Settings();
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.Command.GetValue("find", BsonNull.Value) == "identitySessions" && command.Command.TryGetValue("projection", out var projection) &&
                    projection.AsBsonDocument.Contains("idleExpiresAt") && projection.AsBsonDocument.Contains("absoluteExpiresAt")) selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (selected != 0 && selected == command.RequestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) data.Clock.UtcNow = deadline;
            });
        };
        var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
        {
            if (joined) await data.NewBuyer(client).QueryAsync(data.Buyer, 0, 25, null);
            else await new CatalogVisibilityStore(db, new(db, new(client), data.Clock), data.Clock).QueryAsync(data.Buyer);
        });
        Assert.Equal(1, Volatile.Read(ref observed)); Assert.Equal(before, await data.StateAsync());
    }
}
