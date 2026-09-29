using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Catalog;

/// <summary>Exercises the new permission through real transactional authority and receipt
/// machinery. The private probe is not a catalog-price producer or installed price schema.</summary>
[Collection("Mongo")]
public sealed class CatalogPriceAuthorityStoreTests(MongoFixture fixture)
{
    private static readonly GrantScope Shop = new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
    private static readonly ResourceScope Resource = new(FulfillmentOrganizationId: 100, ShopId: 101, ChannelId: 103);
    private static readonly AuthorizationCommandMetadata Metadata = new("price-authority-probe", "Synthetic price review", "price-authority-test");
    private const string Probe = "catalogPriceAuthorityProbes";

    [Fact]
    public async Task Independent_verified_checker_commits_once_and_replay_requires_current_person_authority()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await PrepareAsync(data);
        var first = await ApproveAsync(data, actor);
        Assert.Equal(first, await ApproveAsync(data, actor));
        Assert.Equal(2L, (await RowAsync(data))["version"].AsInt64);
        Assert.Equal(1, await Receipts(data).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var receipt = await Receipts(data).Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal(Permissions.PricingApprove, receipt["personApproval"]["permission"].AsString);
        Assert.Equal(1000L, receipt["personApproval"]["approval"]["proposedBy"].AsInt64);
        await data.Database.GetCollection<BsonDocument>("personAccountBindings").UpdateOneAsync(
            new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("status", "suspended"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => ApproveAsync(data, actor));
        Assert.Equal(2L, (await RowAsync(data))["version"].AsInt64);
        Assert.Equal(1, await Receipts(data).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Theory]
    [InlineData("same-account")] [InlineData("no-person-proof")] [InlineData("wrong-scope")]
    [InlineData("manage-only")] [InlineData("expired-mfa")] [InlineData("missing-proposer")]
    public async Task Missing_independent_current_authority_cannot_write_or_issue_receipt(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await PrepareAsync(data, people: fault != "no-person-proof");
        if (fault == "same-account")
            await Rows(data).UpdateOneAsync(new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Set("proposedBy", actor.UserId));
        if (fault == "missing-proposer")
            await Rows(data).UpdateOneAsync(new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Unset("proposedBy"));
        if (fault == "manage-only")
            await data.Database.GetCollection<BsonDocument>("authorizationGrants").UpdateOneAsync(
                new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("permissions", new BsonArray { Permissions.PricingManage }));
        if (fault == "expired-mfa") data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => ApproveAsync(data, actor, wrongScope: fault == "wrong-scope"));
        await UnchangedAsync(data);
    }

    [Theory]
    [InlineData("mfa")] [InlineData("person")] [InlineData("grant")]
    public async Task Authority_expiring_after_write_rolls_back_business_and_receipt(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await PrepareAsync(data);
        if (fault is "person" or "grant")
            await data.Database.GetCollection<BsonDocument>(fault == "person" ? "personAccountBindings" : "authorizationGrants")
                .UpdateOneAsync(new BsonDocument("_id", actor.UserId),
                    Builders<BsonDocument>.Update.Set("expiresAt", data.Clock.UtcNow.AddMinutes(1).UtcDateTime));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => ApproveAsync(data, actor,
            afterWrite: () => data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(fault == "mfa" ? 6 : 2)));
        await UnchangedAsync(data);
    }

    [Fact]
    public async Task Reusing_key_with_a_different_review_does_not_replay()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await PrepareAsync(data);
        await ApproveAsync(data, actor);
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => ApproveAsync(data, actor, reviewedVersion: 2));
        Assert.Equal("idempotency_key_reused", error.Code);
        Assert.Equal(2L, (await RowAsync(data))["version"].AsInt64);
        Assert.Equal(1, await Receipts(data).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    private static async Task<ActorContext> PrepareAsync(StoreTestData data, bool people = true)
    {
        await data.AddActorAsync(1000, Shop, Permissions.PricingManage);
        var actor = await data.AddActorAsync(2000, Shop, Permissions.PricingApprove);
        if (people) await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, 1000, 2000);
        await Rows(data).InsertOneAsync(new BsonDocument { { "_id", 1L }, { "version", 1L }, { "proposedBy", 1000L } });
        return actor;
    }

    private static Task<BsonDocument> ApproveAsync(StoreTestData data, ActorContext actor,
        bool wrongScope = false, Action? afterWrite = null, long reviewedVersion = 1) =>
        data.Store.ExecuteAuthorizedAsync(actor, "test.catalog.price.approve", Permissions.PricingApprove, Metadata,
            new { proposalId = 1L, expectedVersion = reviewedVersion },
            async (tx, ct) =>
            {
                var row = await Rows(data).Find(tx, new BsonDocument("_id", 1L)).SingleAsync(ct);
                return new(wrongScope ? Resource with { ShopId = 999 } : Resource,
                    new(row["version"].AsInt64, reviewedVersion,
                        ProposedBy: row.TryGetValue("proposedBy", out var proposer) ? proposer.AsInt64 : null));
            },
            async (tx, ct) =>
            {
                await MongoUnitOfWork.RequireVersionAsync(Rows(data), tx, new BsonDocument("_id", 1L), reviewedVersion,
                    Builders<BsonDocument>.Update.Set("approved", true), ct);
                afterWrite?.Invoke();
                return new BsonDocument("version", 2L);
            });
    private static IMongoCollection<BsonDocument> Rows(StoreTestData data) => data.Database.GetCollection<BsonDocument>(Probe);
    private static IMongoCollection<BsonDocument> Receipts(StoreTestData data) => data.Database.GetCollection<BsonDocument>("idempotencyRecords");
    private static Task<BsonDocument> RowAsync(StoreTestData data) => Rows(data).Find(new BsonDocument("_id", 1L)).SingleAsync();
    private static async Task UnchangedAsync(StoreTestData data)
    {
        Assert.Equal(1L, (await RowAsync(data))["version"].AsInt64);
        Assert.False((await RowAsync(data)).Contains("approved"));
        Assert.Equal(0, await Receipts(data).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }
}
