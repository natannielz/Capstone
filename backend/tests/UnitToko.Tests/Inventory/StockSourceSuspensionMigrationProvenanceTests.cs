using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Inventory;
using UnitToko.Tests.Authorization;
namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceSuspensionMigrationTests
{
    [Theory]
    [InlineData("missing-channel")] [InlineData("replaced-mapping")] [InlineData("replaced-settings")]
    public async Task Coherent_copies_cannot_claim_an_obsolete_review_or_omit_an_already_mapped_channel(string fault)
    {
        await using var data = await StoreTestData.CreateForStockSourceSuspensionMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data); var service = Service(data);
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1);
        if (fault == "replaced-mapping") await Change(data, actors, 1);
        if (fault == "replaced-settings")
        {
            var proposal = await service.ProposeRevisionAsync(actors.Maker, actors.Pool, 1, actors.Input with { Label = "Next configuration" }, Meta("replacement"));
            await service.DecideRevisionAsync(actors.Reviewer, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("replaced"));
        }
        var row = await Rows(data, Collection).Find(new BsonDocument()).SingleAsync();
        row["suspendedAt"] = data.Clock.UtcNow.AddSeconds(1).UtcDateTime;
        if (fault == "missing-channel") row["configuration"]["mappings"] = new BsonArray();
        await ReplaceCoherently(data, row);
        await Reject(data);
    }
    [Theory]
    [InlineData("hidden")] [InlineData("sparse")] [InlineData("partial")] [InlineData("nonunique")]
    public async Task Unsafe_version_index_cannot_be_silently_replaced(string fault)
    {
        await using var data = await StoreTestData.CreateForStockSourceSuspensionMigrationAsync(fixture);
        await InstallDraft(data); await Seed(data);
        await Rows(data, Collection).Indexes.DropOneAsync("reviewed_suspension_version");
        var options = new CreateIndexOptions<BsonDocument>
            { Name = "altered_suspension_version", Unique = fault != "nonunique", Hidden = fault == "hidden", Sparse = fault == "sparse" };
        if (fault == "partial") options.PartialFilterExpression = new BsonDocument("poolId", new BsonDocument("$gt", 0L));
        await Rows(data, Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(StockSourceSuspensionSchemas.Key(), options));
        await Reject(data);
    }
    [Theory]
    [InlineData("audit")] [InlineData("receipt")]
    public async Task Suspension_references_cannot_hide_under_a_foreign_operation_namespace(string kind)
    {
        await using var data = await StoreTestData.CreateForStockSourceSuspensionMigrationAsync(fixture);
        await InstallDraft(data); await Seed(data);
        var collection = kind == "audit" ? "auditEvents" : "idempotencyRecords";
        var row = await Rows(data, collection).Find(new BsonDocument("operation", "stock.source.suspend")).SingleAsync();
        row["operation"] = "foreign.unrelated";
        if (kind == "audit") { row["_id"] = 99001L; row["kind"] = "foreign.audit"; }
        else { row["_id"] = ObjectId.GenerateNewId(); row["key"] = "foreign-key"; }
        await Rows(data, collection).InsertOneAsync(row);
        await Reject(data);
    }    [Fact]
    public async Task Populated_exact_stock_and_lots_survive_upgrade_without_any_quantity_change()
    {
        await using var data = await StoreTestData.CreateForStockSourceSuspensionMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data);
        await InventoryGraphTests.InstallBalances(data);
        await Rows(data, "lots").InsertOneAsync(InventoryGraphTests.Lot());
        var stock = InventoryBson.Balance(new InventoryBalanceDocument(7000, actors.Pool,
            new(new(Owner, new(200), new(201), new(202)), 9007199254740993, 2, 3, 4, 1)));
        await Rows(data, "stockBalances").InsertOneAsync(stock);
        var before = await Business(data);
        await new UnitToko.Platform.Mongo.CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(before, await Business(data));
        Assert.Equal(stock, await Rows(data, "stockBalances").Find(new BsonDocument("_id", 7000L)).SingleAsync());
    }}