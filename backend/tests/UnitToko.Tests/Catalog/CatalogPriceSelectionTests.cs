using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed class CatalogPriceSelectionTests(MongoFixture fixture)
{
    [Fact]
    public async Task Price_only_selection_is_scoped_and_keyset_pages_preserve_original_proposals()
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var family = await data.Catalog.FamilyAsync(data.Catalog.Other, CatalogAuthoringTestData.OtherOwner, "OTHER");
        var otherSku = await data.Catalog.Service.CreateSkuAsync(data.Catalog.Other,
            new(family.Id, family.Version, "PENCIL-OTHER", "other-pencil", CatalogAuthoringTestData.Definition()), CatalogPriceTestData.Meta("other-sku"));
        var choices = await data.Service.SelectSkusAsync(data.Checker, CatalogPriceAccess.Review, "PENCIL");
        Assert.Equal(data.SkuId, Assert.Single(choices).Sku.Id.Value);
        Assert.Equal("Pensil", choices[0].FamilyName);
        Assert.Equal(12, choices[0].Definition.BaseUnitsPerPackage);
        Assert.Empty(await data.Service.SelectSkusAsync(data.Checker, CatalogPriceAccess.Review, "PENCIL.*"));
        Assert.Empty(await data.Service.SelectSkusAsync(data.Checker, CatalogPriceAccess.Review, afterId: data.SkuId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.SelectSkusAsync(data.Catalog.Buyer, CatalogPriceAccess.Review));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ListProposalsAsync(data.Checker, otherSku.Id.Value, CatalogPriceAccess.Review));
        var first = await data.ProposeAsync("first"); var second = await data.ProposeAsync("second", "13000.00");
        await data.ApproveAsync(first);
        var page = await data.Service.ListProposalsAsync(data.Checker, data.SkuId, CatalogPriceAccess.Review, limit: 1);
        Assert.Equal(second["proposalId"], Assert.Single(page)["proposal"]["_id"]);
        var prior = await data.Service.ListProposalsAsync(data.Checker, data.SkuId, CatalogPriceAccess.Review, second["proposalId"].AsInt64, 1);
        Assert.Equal("approve", Assert.Single(prior)["decision"]["decision"]);
        Assert.Empty(await data.Service.ListProposalsAsync(data.Checker, data.SkuId, CatalogPriceAccess.Review, first["proposalId"].AsInt64, 1));
    }

    [Theory]
    [InlineData("mfa", false)] [InlineData("mfa", true)]
    [InlineData("grant", false)] [InlineData("grant", true)]
    [InlineData("session", false)] [InlineData("session", true)]
    public async Task Selection_cannot_return_after_late_expiry_even_for_empty_pages(string expiry, bool empty)
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var start = data.Catalog.Clock.UtcNow;
        if (expiry == "grant") await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId),
            Builders<BsonDocument>.Update.Set("expiresAt", start.AddMinutes(1).UtcDateTime));
        if (expiry == "session") await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Checker.Session!.Id),
            Builders<BsonDocument>.Update.Set("idleExpiresAt", start.AddMinutes(1).UtcDateTime));
        var before = await data.StateAsync(); var auditWritten = false; var selected = 0; var observed = false;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ??
            "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (command.Command.TryGetValue("insert", out var collection) && collection == "auditEvents") auditWritten = true;
                if (auditWritten && command.Command.TryGetValue("find", out var find) && find == "identitySessions" &&
                    command.Command.TryGetValue("projection", out var projection) && projection.AsBsonDocument.Contains("idleExpiresAt")) selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (!observed && selected == command.RequestId) {
                    observed = true; data.Catalog.Clock.UtcNow = start.AddMinutes(expiry == "mfa" ? 6 : 2);
                }
            });
        };
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.NewService(new MongoClient(settings))
            .SelectSkusAsync(data.Checker, CatalogPriceAccess.Review, empty ? "no-such-product" : "PENCIL"));
        Assert.True(observed); Assert.Equal(before, await data.StateAsync());
    }
}
