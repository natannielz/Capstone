using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Platform.Authorization;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed class CatalogPriceIntegrityTests(MongoFixture fixture)
{
    [Theory]
    [InlineData("amount")] [InlineData("head")] [InlineData("missing-head")]
    [InlineData("missing-price")] [InlineData("missing-decision")] [InlineData("proposal-audit")]
    [InlineData("approval-person")] [InlineData("proposal-receipt")] [InlineData("approval-result")]
    public async Task Corrupted_history_or_provenance_denies_reads_and_replay(string fault)
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var proposal = await data.ProposeAsync(); var decision = await data.ApproveAsync(proposal);
        var proposalId = proposal["proposalId"];
        if (fault == "amount") await data.Rows("priceVersions").UpdateOneAsync(new BsonDocument("_id", decision["priceId"]),
            Builders<BsonDocument>.Update.Set("unitPrice", new BsonDecimal128(999m)));
        if (fault == "head") await data.Rows("catalogPriceHeads").UpdateOneAsync(new BsonDocument("_id", data.SkuId),
            Builders<BsonDocument>.Update.Set("version", 2L));
        if (fault == "missing-head") await data.Rows("catalogPriceHeads").DeleteOneAsync(new BsonDocument("_id", data.SkuId));
        if (fault == "missing-price") await data.Rows("priceVersions").DeleteOneAsync(new BsonDocument("_id", decision["priceId"]));
        if (fault == "missing-decision") await data.Rows("catalogPriceDecisions").DeleteOneAsync(new BsonDocument("_id", decision["decisionId"]));
        if (fault == "proposal-audit") {
            var row = await data.Rows("catalogPriceProposals").Find(new BsonDocument("_id", proposalId)).SingleAsync();
            await data.Rows("auditEvents").UpdateOneAsync(new BsonDocument("_id", row["auditId"]),
                Builders<BsonDocument>.Update.Set("record.input.unitPrice", new BsonDecimal128(2m)));
        }
        if (fault == "approval-person") await data.Rows("idempotencyRecords").UpdateOneAsync(new BsonDocument("key", "price-approve"),
            Builders<BsonDocument>.Update.Set("personApproval.approval.proposedBy", 999L));
        if (fault == "proposal-receipt") await data.Rows("idempotencyRecords").UpdateOneAsync(new BsonDocument("key", "price-propose"),
            Builders<BsonDocument>.Update.Set("bodyHash", new string('b', 64)));
        if (fault == "approval-result") await data.Rows("idempotencyRecords").UpdateOneAsync(new BsonDocument("key", "price-approve"),
            Builders<BsonDocument>.Update.Set("result.priceVersion", 2L));
        var before = await data.StateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadCurrentAsync(data.Maker, data.SkuId, CatalogPriceAccess.Prepare));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.ApproveAsync(proposal));
        Assert.Equal(before, await data.StateAsync());
    }

    [Theory]
    [InlineData("double")] [InlineData("string")] [InlineData("negative")] [InlineData("fraction")]
    [InlineData("too-large")] [InlineData("unknown-field")] [InlineData("int32-id")]
    public async Task Installed_shapes_reject_lossy_or_unsupported_price_facts(string fault)
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var accepted = await data.ApproveAsync(await data.ProposeAsync());
        var row = await data.Rows("priceVersions").Find(new BsonDocument("_id", accepted["priceId"])).SingleAsync();
        var changed = row.DeepClone().AsBsonDocument;
        if (fault == "double") changed["unitPrice"] = new BsonDouble(12.34);
        if (fault == "string") changed["unitPrice"] = "12.34";
        if (fault == "negative") changed["unitPrice"] = new BsonDecimal128(-0.01m);
        if (fault == "fraction") changed["unitPrice"] = new BsonDecimal128(0.001m);
        if (fault == "too-large") changed["unitPrice"] = new BsonDecimal128(1000000000000000m);
        if (fault == "unknown-field") changed["clientOverride"] = true;
        if (fault == "int32-id") changed["proposalId"] = 1;
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows("priceVersions").ReplaceOneAsync(new BsonDocument("_id", row["_id"]), changed));
        Assert.Equal(row, await data.Rows("priceVersions").Find(new BsonDocument("_id", row["_id"])).SingleAsync());
    }

    [Fact]
    public async Task Explicit_zero_is_reviewed_and_rejection_does_not_publish_or_allow_a_second_decision()
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var proposal = await data.ProposeAsync(amount: "0.00");
        await data.ApproveAsync(proposal);
        var price = await data.Service.ReadCurrentAsync(data.Maker, data.SkuId, CatalogPriceAccess.Prepare);
        Assert.NotNull(price); Assert.Equal("0.00", price.UnitPrice.ToString());
        var rejected = await data.ProposeAsync("reject-proposal", "1000.00");
        await data.Service.DecideAsync(data.Checker, rejected["proposalId"].AsInt64, 1, CatalogPriceDecision.Reject,
            "Synthetic rejected price", CatalogPriceTestData.Meta("reject"));
        var view = await data.Service.ReadProposalAsync(data.Maker, data.SkuId, rejected["proposalId"].AsInt64, CatalogPriceAccess.Prepare);
        Assert.Equal("reject", view["decision"]["decision"].AsString); Assert.True(view["price"].IsBsonNull);
        await Assert.ThrowsAsync<UnitToko.Platform.Mongo.PersistenceConflictException>(() => data.ApproveAsync(rejected, "approve-rejected"));
        Assert.Equal(price, await data.Service.ReadCurrentAsync(data.Maker, data.SkuId, CatalogPriceAccess.Prepare));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.ReadHistoryAsync(data.Catalog.Buyer, data.SkuId, CatalogPriceAccess.Prepare));
    }

    [Fact]
    public async Task Historical_price_preserves_original_definition_after_reviewed_label_revision()
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var accepted = await data.ApproveAsync(await data.ProposeAsync());
        var preview = await data.Service.PreviewAsync(data.Maker, data.SkuId, CatalogPriceAccess.Prepare);
        await data.Catalog.Service.ReviseDefinitionAsync(data.Maker,
            new(new(data.SkuId), preview.Expected.SkuRevision, CatalogAuthoringTestData.Definition(label: "Carton")),
            CatalogPriceTestData.Meta("rename"));
        var history = await data.Service.ReadHistoryAsync(data.Checker, data.SkuId, CatalogPriceAccess.Review);
        Assert.Single(history);
        Assert.Equal(accepted["priceId"], history[0]["_id"]);
        var old = await data.Service.ReadProposalAsync(data.Checker, data.SkuId, accepted["proposalId"].AsInt64, CatalogPriceAccess.Review);
        Assert.Equal("Dus", old["proposal"]["definition"]["packageLabel"].AsString);
    }
}
