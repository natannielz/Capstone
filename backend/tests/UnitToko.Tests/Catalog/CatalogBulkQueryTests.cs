using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;

namespace UnitToko.Tests.Catalog;

/// <summary>Actual catalog publication, definition and independent price owners. No substitute
/// query implementation or fake catalog result is registered.</summary>
[Collection("Mongo")]
public sealed class CatalogBulkQueryTests(MongoFixture fixture)
{
    private const string FirstCode = "PENCIL-12", SecondCode = "BULK-SECOND-12";

    [Fact]
    public async Task Exact_codes_preserve_requested_order_and_read_current_reviewed_definition_and_price()
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var second = await PreparePair(data);
        var before = await Business(data);
        var selected = await data.Catalog.BuyerService.ReadExactSkuCodesAsync(data.Catalog.Buyer, [" bulk-second-12 ", "pencil-12"]);
        Assert.Equal(new[] { SecondCode, FirstCode }, selected.Select(item => item.Sku.Code));
        Assert.Equal(new[] { second.Id.Value, data.SkuId }, selected.Select(item => item.Sku.Id.Value));
        Assert.Equal(second.CurrentDefinitionId, selected[0].Definition.Id);
        Assert.Equal(1, selected[0].Definition.Version); Assert.Equal(12, selected[0].Definition.BaseUnitsPerPackage);
        Assert.Equal(24000.50m, selected[0].CurrentPrice!.UnitPrice.Value);
        Assert.Equal(12000.25m, selected[1].CurrentPrice!.UnitPrice.Value);
        Assert.Equal(before, await Business(data));
        var originalFirstDefinition = selected[1].Definition;
        var originalSecondPrice = selected[0].CurrentPrice!;

        var sku = await data.Catalog.RowAsync("skus", second.Id.Value);
        var revised = await data.Catalog.Service.ReviseDefinitionAsync(data.Maker,
            new(second.Id, sku["revision"].AsInt64, CatalogAuthoringTestData.Definition(label: "Karton terbaru")),
            CatalogPriceTestData.Meta("bulk-definition-revised"));
        await Price(data, second.Id.Value, "26000.75", "bulk-price-revised");
        before = await Business(data);
        var current = await data.Catalog.BuyerService.ReadExactSkuCodesAsync(data.Catalog.Buyer, [FirstCode, SecondCode]);
        Assert.Equal(new[] { FirstCode, SecondCode }, current.Select(item => item.Sku.Code));
        AssertDefinitionEqual(originalFirstDefinition, current[0].Definition);
        Assert.Equal(revised.CurrentDefinitionId, current[1].Definition.Id);
        Assert.Equal(2, current[1].Definition.Version); Assert.Equal("Karton terbaru", current[1].Definition.PackageLabel);
        Assert.Equal(2, current[1].CurrentPrice!.Version); Assert.Equal(26000.75m, current[1].CurrentPrice!.UnitPrice.Value);
        Assert.NotEqual(originalSecondPrice.Id, current[1].CurrentPrice!.Id);
        Assert.Equal(before, await Business(data));
    }

    [Fact]
    public async Task Missing_retired_and_unentitled_codes_refuse_the_whole_batch_with_one_unavailable_code()
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var retired = await PreparePair(data);
        // Warm a real visible result before withdrawing it through the catalog owner.
        Assert.Equal(2, (await data.Catalog.BuyerService.ReadExactSkuCodesAsync(data.Catalog.Buyer, [FirstCode, SecondCode])).Length);
        var retiredSku = await data.Catalog.RowAsync("skus", retired.Id.Value);
        await data.Catalog.Service.RetireSkuAsync(data.Maker, new(retired.Id, retiredSku["revision"].AsInt64), CatalogPriceTestData.Meta("bulk-retired"));
        var hidden = await data.Catalog.SkuAsync(code: "BULK-UNENTITLED-12", categoryCode: "BULKHIDDEN");
        await Publish(data, hidden.Id.Value, 502, "bulk-hidden-published");
        Assert.True((await data.Catalog.RowAsync("skus", hidden.Id.Value))["active"].AsBoolean);
        var before = await Business(data);
        foreach (var unavailable in new[] { "BULK-NOT-FOUND", SecondCode, "BULK-UNENTITLED-12" })
        {
            foreach (var codes in new[] { ImmutableArray.Create(FirstCode, unavailable), ImmutableArray.Create(unavailable, FirstCode) })
            {
                var error = await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
                    data.Catalog.BuyerService.ReadExactSkuCodesAsync(data.Catalog.Buyer, codes));
                Assert.Equal("order_package_unavailable", error.Code);
                Assert.Equal(before, await Business(data));
            }
        }
        Assert.Equal(FirstCode, Assert.Single(await data.Catalog.BuyerService.ReadExactSkuCodesAsync(data.Catalog.Buyer, [FirstCode])).Sku.Code);
    }

    [Fact]
    public async Task Empty_duplicate_oversized_and_malformed_batches_are_refused_before_any_catalog_read_effect()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var before = await data.StateAsync();
        foreach (var codes in new[] { default(ImmutableArray<string>), ImmutableArray<string>.Empty,
            ImmutableArray.Create("PENCIL-12", " pencil-12 "),
            Enumerable.Range(0, 101).Select(index => "BULK-" + index).ToImmutableArray() })
        {
            var error = await Assert.ThrowsAsync<DomainValidationException>(() => data.BuyerService.ReadExactSkuCodesAsync(data.Buyer, codes));
            Assert.Equal("invalid_bulk_sku_query", error.Code);
        }
        foreach (var malformed in new string?[] { null, "", " ", "PENCIL-12\n", "PENCIL\t12", "PENCIL/12", "PENCİL-12", new('A', 65) })
        {
            var error = await Assert.ThrowsAsync<DomainValidationException>(() => data.BuyerService.ReadExactSkuCodesAsync(data.Buyer, [malformed!]));
            Assert.Equal("invalid_sku_code", error.Code);
        }
        Assert.Equal(before, await data.StateAsync());
    }

    [Fact]
    public async Task Warm_batch_cannot_return_a_second_price_with_missing_original_approval_receipt_or_changed_amount()
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var second = await PreparePair(data);
        var original = await data.Catalog.BuyerService.ReadExactSkuCodesAsync(data.Catalog.Buyer, [FirstCode, SecondCode]);
        var price = await data.Catalog.RowAsync("priceVersions", original[1].CurrentPrice!.Id.Value);
        var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument("key", "bulk-second-price-approved")).SingleAsync();
        await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("_id", receipt["_id"]));
        var before = await data.StateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Catalog.BuyerService.ReadExactSkuCodesAsync(data.Catalog.Buyer, [FirstCode, SecondCode]));
        Assert.Equal(before, await data.StateAsync());
        await data.Rows("idempotencyRecords").InsertOneAsync(receipt);
        await data.Rows("priceVersions").UpdateOneAsync(new BsonDocument("_id", price["_id"]), Builders<BsonDocument>.Update.Set("unitPrice", new BsonDecimal128(1m)));
        before = await data.StateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Catalog.BuyerService.ReadExactSkuCodesAsync(data.Catalog.Buyer, [FirstCode, SecondCode]));
        Assert.Equal(before, await data.StateAsync());
        await data.Rows("priceVersions").ReplaceOneAsync(new BsonDocument("_id", price["_id"]), price);
        var restored = await data.Catalog.BuyerService.ReadExactSkuCodesAsync(data.Catalog.Buyer, [FirstCode, SecondCode]);
        Assert.Equal(second.Id, restored[1].Sku.Id); Assert.Equal(original[1].CurrentPrice, restored[1].CurrentPrice);
    }

    [Theory]
    [InlineData("session")] [InlineData("publication")] [InlineData("price")]
    public async Task Inflight_multi_sku_read_fences_current_authority_publication_and_price(string change)
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var second = await PreparePair(data);
        Assert.Equal(2, (await data.Catalog.BuyerService.ReadExactSkuCodesAsync(data.Catalog.Buyer, [FirstCode, SecondCode])).Length);
        BsonDocument? nextPrice = null;
        if (change == "price") nextPrice = await data.ProposeAsync("bulk-race-price", "13000.00");
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
                    entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Concurrent bulk catalog change did not complete");
                }
            });
        };
        var pending = Task.Run(() => data.Catalog.NewBuyer(new MongoClient(settings)).ReadExactSkuCodesAsync(data.Catalog.Buyer, [FirstCode, SecondCode]));
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (change == "session") await data.Catalog.Sessions.RevokeAsync(data.Catalog.Issued[data.Catalog.Buyer.UserId].OpaqueToken, "Synthetic bulk read revocation");
            if (change == "publication") {
                var sku = await data.Catalog.RowAsync("skus", second.Id.Value); var assortment = await data.Catalog.RowAsync("catalogAssortments", 501);
                await data.Catalog.Service.SetPublicationsAsync(data.Maker,
                    new(second.Id, sku["revision"].AsInt64, [], [new(new(501), assortment["version"].AsInt64)]), CatalogPriceTestData.Meta("bulk-race-withdraw"));
            }
            if (change == "price") await data.ApproveAsync(nextPrice!, "bulk-race-price-approved");
        } finally { release.Set(); }
        Assert.Equal(1, observed);
        if (change == "price") {
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(new[] { FirstCode, SecondCode }, result.Select(item => item.Sku.Code));
            Assert.Equal(13000m, result[0].CurrentPrice!.UnitPrice.Value); Assert.Equal(24000.50m, result[1].CurrentPrice!.UnitPrice.Value);
            Assert.True(reads >= 3);
        } else await Assert.ThrowsAsync<AuthorizationDeniedException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    private static void AssertDefinitionEqual(VariantDefinitionVersion expected, VariantDefinitionVersion actual)
    {
        // Mongo materializes new immutable collections; compare their contents and retain
        // record equality for every scalar fact, including IDs, versions and effective time.
        Assert.Equal(expected.Attributes.ToArray(), actual.Attributes.ToArray());
        Assert.Equal(expected.ImageAssetIds.ToArray(), actual.ImageAssetIds.ToArray());
        Assert.Equal(expected, actual with { Attributes = expected.Attributes, ImageAssetIds = expected.ImageAssetIds });
    }

    private static async Task<CatalogMutationResult> PreparePair(CatalogPriceTestData data)
    {
        await data.Catalog.AssortmentsAsync();
        await Publish(data, data.SkuId, 501, "bulk-first-published");
        await data.ApproveAsync(await data.ProposeAsync());
        var second = await data.Catalog.SkuAsync(code: SecondCode, categoryCode: "BULKSECOND");
        await Publish(data, second.Id.Value, 501, "bulk-second-published");
        await Price(data, second.Id.Value, "24000.50", "bulk-second-price");
        return second;
    }
    private static async Task Publish(CatalogPriceTestData data, long skuId, long assortmentId, string key)
    {
        var sku = await data.Catalog.RowAsync("skus", skuId); var assortment = await data.Catalog.RowAsync("catalogAssortments", assortmentId);
        await data.Catalog.Service.SetPublicationsAsync(data.Maker, new(new(skuId), sku["revision"].AsInt64,
            [new(assortmentId)], [new(new(assortmentId), assortment["version"].AsInt64)]), CatalogPriceTestData.Meta(key));
    }
    private static async Task Price(CatalogPriceTestData data, long skuId, string amount, string key)
    {
        var preview = await data.Service.PreviewAsync(data.Maker, skuId, CatalogPriceAccess.Prepare);
        var proposal = await data.Service.ProposeAsync(data.Maker, skuId, preview.Expected, new("IDR", Money.Parse(amount)), CatalogPriceTestData.Meta(key));
        await data.Service.DecideAsync(data.Checker, proposal["proposalId"].AsInt64, 1, CatalogPriceDecision.Approve,
            "Synthetic independent bulk catalog price review", CatalogPriceTestData.Meta(key + "-approved"));
    }
    private static async Task<BsonDocument> Business(CatalogPriceTestData data)
    {
        var state = await data.StateAsync();
        foreach (var collection in state) foreach (var row in collection.Value.AsBsonArray) row.AsBsonDocument.Remove("fence");
        return state;
    }
}
