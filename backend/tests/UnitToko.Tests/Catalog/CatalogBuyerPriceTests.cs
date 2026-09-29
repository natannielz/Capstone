using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform.Authorization;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed class CatalogBuyerPriceTests(MongoFixture fixture)
{
    private static async Task Publish(CatalogPriceTestData data)
    {
        await data.Catalog.AssortmentsAsync();
        await data.Catalog.Service.SetPublicationsAsync(data.Maker, new(new(data.SkuId), 1, [new(501)], [new(new(501), 1)]), CatalogPriceTestData.Meta("publish"));
    }
    [Theory]
    [InlineData("head")] [InlineData("audit")] [InlineData("receipt")] [InlineData("amount")]
    public async Task Entitled_buyer_never_receives_price_with_broken_approval_provenance(string fault)
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        await Publish(data); await data.ApproveAsync(await data.ProposeAsync());
        if (fault == "head") await data.Rows("catalogPriceHeads").DeleteOneAsync(new BsonDocument("_id", data.SkuId));
        if (fault == "audit") await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("operation", "catalog.price.approve"));
        if (fault == "receipt") await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("operation", "catalog.price.approve"));
        if (fault == "amount") await data.Rows("priceVersions").UpdateOneAsync(new BsonDocument("skuId", data.SkuId), Builders<BsonDocument>.Update.Set("unitPrice", new BsonDecimal128(1m)));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Catalog.BuyerService.QueryAsync(data.Catalog.Buyer, 0, 25, null));
    }
    [Theory]
    [InlineData("price")] [InlineData("entitlement")] [InlineData("session")]
    public async Task Buyer_price_read_retries_changed_price_and_denies_lost_authority(string change)
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        await Publish(data); await data.ApproveAsync(await data.ProposeAsync());
        var proposal = await data.ProposeAsync("next-price", "13000.00");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selected = 0; var observed = 0; var reads = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (command.Command.GetValue("find", BsonNull.Value) == "catalogPriceHeads" && Interlocked.Increment(ref reads) == 1) selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (selected != 0 && command.RequestId == selected && Interlocked.CompareExchange(ref observed, 1, 0) == 0) {
                    entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Concurrent buyer-price change did not complete.");
                }
            });
        };
        var pending = Task.Run(() => data.Catalog.NewBuyer(new MongoClient(settings)).QueryAsync(data.Catalog.Buyer, 0, 25, null));
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (change == "price") await data.ApproveAsync(proposal, "next-approval");
            if (change == "entitlement") await data.Rows("catalogEntitlements").UpdateOneAsync(new BsonDocument("_id", 10001L), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (change == "session") await data.Catalog.Sessions.RevokeAsync(data.Catalog.Issued[data.Catalog.Buyer.UserId].OpaqueToken, "Synthetic session revocation");
        } finally { release.Set(); }
        Assert.Equal(1, observed);
        if (change == "price") {
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(13000m, Assert.Single(result.Items).CurrentPrice!.UnitPrice.Value); Assert.True(reads >= 2);
        } else await Assert.ThrowsAsync<AuthorizationDeniedException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(30)));
    }
}
