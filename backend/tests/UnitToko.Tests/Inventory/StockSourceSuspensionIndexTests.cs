using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceSuspensionTests
{
    [Theory]
    [InlineData("auditEvents", "ix_stock_source_suspension_audit")]
    [InlineData("idempotencyRecords", "ix_stock_source_suspension_receipt")]
    public async Task Missing_pool_scoped_provenance_lookup_index_prevents_suspension_reads(string collection, string name)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (_, _, actor, pool) = await Setup(data);
        var indexes = await (await Rows(data, collection).Indexes.ListAsync()).ToListAsync();
        if (indexes.Any(index => index["name"] == name)) await Rows(data, collection).Indexes.DropOneAsync(name);
        var before = await Pool(data, pool);
        await Assert.ThrowsAsync<DomainValidationException>(() => Service(data).PreviewSuspensionAsync(actor, pool));
        Assert.Equal(before, await Pool(data, pool)); Assert.Equal(2L, await Count(data, "idempotencyRecords"));
    }
    [Theory]
    [InlineData("hidden")] [InlineData("sparse")] [InlineData("partial")] [InlineData("unique")] [InlineData("equivalent-name")]
    public async Task Required_lookup_index_must_be_complete_visible_nonrestrictive_and_can_keep_its_reviewed_name(string kind)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (_, _, actor, pool) = await Setup(data);
        var index = StockSourceSuspensionSchemas.LookupIndexes().Single(item => item.Collection == "idempotencyRecords");
        await Rows(data, index.Collection).Indexes.DropOneAsync(index.Name);
        var options = new CreateIndexOptions<BsonDocument> { Name = "reviewed_pool_receipt_lookup", Hidden = kind == "hidden", Sparse = kind == "sparse", Unique = kind == "unique" };
        if (kind == "partial") options.PartialFilterExpression = new BsonDocument("operation", "stock.source.suspend");
        await Rows(data, index.Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(index.Key, options));
        if (kind == "equivalent-name")
        {
            var service = Service(data);
            var result = await service.SuspendAsync(actor, pool, Expected(await service.PreviewSuspensionAsync(actor, pool)), Meta("indexed-stop"));
            Assert.Equal(result, await service.RecoverSuspensionAsync(actor, pool, "indexed-stop", "recover"));
        }
        else await Assert.ThrowsAsync<DomainValidationException>(() => Service(data).PreviewSuspensionAsync(actor, pool));
    }
}
