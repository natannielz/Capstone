using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed class CatalogPriceServiceTests(MongoFixture fixture)
{
    [Fact]
    public async Task First_and_second_approved_prices_preserve_exact_history_and_all_command_records()
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        Assert.Null(await data.Service.ReadCurrentAsync(data.Maker, data.SkuId, CatalogPriceAccess.Prepare));
        var proposed = await data.ProposeAsync();
        Assert.Null(await data.Service.ReadCurrentAsync(data.Checker, data.SkuId, CatalogPriceAccess.Review));
        var accepted = await data.ApproveAsync(proposed);
        var first = await data.Service.ReadCurrentAsync(data.Maker, data.SkuId, CatalogPriceAccess.Prepare);
        Assert.NotNull(first); Assert.Equal("12000.25", first.UnitPrice.ToString()); Assert.Equal(1, first.Version);
        var originalRow = await data.Rows("priceVersions").Find(new BsonDocument("_id", first.Id.Value)).SingleAsync();
        Assert.True(originalRow["unitPrice"].IsDecimal128);
        var secondProposal = await data.ProposeAsync("second-price", "999999999999999.99");
        var secondDecision = await data.ApproveAsync(secondProposal, "second-approve");
        var second = await data.Service.ReadCurrentAsync(data.Checker, data.SkuId, CatalogPriceAccess.Review);
        Assert.NotNull(second); Assert.Equal(2, second.Version); Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("999999999999999.99", second.UnitPrice.ToString());
        Assert.Equal(originalRow, await data.Rows("priceVersions").Find(new BsonDocument("_id", first.Id.Value)).SingleAsync());
        Assert.Equal(accepted, await data.ApproveAsync(proposed));
        Assert.Equal(secondDecision, await data.ApproveAsync(secondProposal, "second-approve"));
        Assert.Equal(2, await data.Rows("priceVersions").CountDocumentsAsync(new BsonDocument()));
        var history = await data.Service.ReadHistoryAsync(data.Maker, data.SkuId, CatalogPriceAccess.Prepare);
        Assert.Equal(new long[] { 2, 1 }, history.Select(row => row["version"].AsInt64));
        Assert.Equal(4, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "catalog.price.command")));
        Assert.Equal(4, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "catalog.price.changed")));
        Assert.Equal(4, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", new BsonDocument("$regex", "^catalog.price\\."))));
    }

    [Theory]
    [InlineData("manage-only")] [InlineData("other-owner")] [InlineData("buyer")] [InlineData("same-maker")]
    public async Task Unauthorized_decisions_leave_no_business_or_receipt_change(string fault)
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var proposal = await data.ProposeAsync();
        var actor = fault switch { "other-owner" => data.Catalog.Other, "buyer" => data.Catalog.Buyer, _ => data.Maker };
        if (fault == "same-maker")
            await data.Catalog.Base.AddGrantAsync(data.Maker, 3200, CatalogAuthoringTestData.Shop, [UnitToko.Modules.Identity.Permissions.PricingApprove]);
        if (fault == "other-owner") await data.Catalog.Base.AddGrantAsync(actor, 5200, CatalogAuthoringTestData.OtherOwner, [UnitToko.Modules.Identity.Permissions.PricingApprove]);
        var before = await data.StateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.DecideAsync(actor, proposal["proposalId"].AsInt64,
            1, CatalogPriceDecision.Approve, "Synthetic review", CatalogPriceTestData.Meta("denied")));
        Assert.Equal(before, await data.StateAsync());
    }

    [Theory]
    [InlineData("definition")] [InlineData("retired")] [InlineData("price")]
    public async Task Stale_reviews_cannot_approve_but_maker_can_withdraw(string change)
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var proposal = await data.ProposeAsync();
        var preview = await data.Service.PreviewAsync(data.Maker, data.SkuId, CatalogPriceAccess.Prepare);
        if (change == "definition") await data.Catalog.Service.ReviseDefinitionAsync(data.Maker,
            new(new(data.SkuId), preview.Expected.SkuRevision, CatalogAuthoringTestData.Definition(label: "Carton")),
            CatalogPriceTestData.Meta("change-package-label"));
        if (change == "retired") await data.Catalog.Service.RetireSkuAsync(data.Maker,
            new(new(data.SkuId), preview.Expected.SkuRevision), CatalogPriceTestData.Meta("retire"));
        if (change == "price") await data.ApproveAsync(await data.ProposeAsync("other-proposal", "13000.00"), "other-approve");
        var before = await data.StateAsync();
        if (change == "retired") await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.ApproveAsync(proposal));
        else Assert.Equal("catalog_price_changed", (await Assert.ThrowsAsync<DomainValidationException>(() => data.ApproveAsync(proposal))).Code);
        Assert.Equal(before, await data.StateAsync());
        var withdrawn = await data.Service.DecideAsync(data.Maker, proposal["proposalId"].AsInt64, 1,
            CatalogPriceDecision.Withdraw, "Withdraw obsolete review", CatalogPriceTestData.Meta("withdraw"));
        Assert.Equal("withdraw", withdrawn["status"].AsString);
    }

    [Fact]
    public async Task Same_key_replays_original_proposal_and_different_input_conflicts()
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var preview = await data.Service.PreviewAsync(data.Maker, data.SkuId, CatalogPriceAccess.Prepare);
        var input = new CatalogPriceInput("IDR", Money.Parse("1.25"));
        var first = await data.Service.ProposeAsync(data.Maker, data.SkuId, preview.Expected, input, CatalogPriceTestData.Meta("same"));
        await data.ApproveAsync(first);
        Assert.Equal(first, await data.Service.ProposeAsync(data.Maker, data.SkuId, preview.Expected, input, CatalogPriceTestData.Meta("same")));
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Service.ProposeAsync(data.Maker, data.SkuId, preview.Expected, input with { UnitPrice = Money.Parse("2.00") }, CatalogPriceTestData.Meta("same")));
        Assert.Equal("idempotency_key_reused", error.Code);
    }

    [Theory]
    [InlineData("auditEvents")] [InlineData("outboxEvents")]
    public async Task Failed_command_record_aborts_price_head_history_decision_and_receipt(string collection)
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var proposal = await data.ProposeAsync();
        var before = await data.StateAsync();
        await data.Catalog.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", collection },
            { "validator", new BsonDocument("_id", -1L) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        await Assert.ThrowsAsync<MongoWriteException>(() => data.ApproveAsync(proposal));
        Assert.Equal(before, await data.StateAsync());
    }

    [Fact]
    public async Task Foundation_only_storage_does_not_enable_the_price_producer()
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture, install: false);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.PreviewAsync(data.Maker, data.SkuId, CatalogPriceAccess.Prepare));
    }

    [Fact]
    public async Task Two_accounts_for_one_verified_person_cannot_approve_a_price()
    {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        var proposal = await data.ProposeAsync();
        var maker = data.Maker.UserId; var checker = data.Checker.UserId;
        await new MongoUnitOfWork(data.Catalog.Client).ExecuteAsync(async (tx, ct) => {
            // Synthetic registry equivalence, not a claim of a production identity review.
            await data.Rows("personRecords").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.PersonId(checker)),
                Builders<BsonDocument>.Update.Set("currentEquivalenceId", PersonTestData.GroupId(maker)).Inc("version", 1L), cancellationToken: ct);
            await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.GroupId(maker)),
                Builders<BsonDocument>.Update.Set("memberPersonIds", new BsonArray { PersonTestData.PersonId(maker), PersonTestData.PersonId(checker) })
                    .Set("memberUserIds", new BsonArray { maker, checker }).Inc("version", 1L), cancellationToken: ct);
            await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.GroupId(checker)),
                Builders<BsonDocument>.Update.Set("status", "superseded").Inc("version", 1L), cancellationToken: ct);
            return true;
        });
        var before = await data.StateAsync();
        var error = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.ApproveAsync(proposal));
        Assert.Equal("independent_person_required", error.Code);
        Assert.Equal(before, await data.StateAsync());
    }

}
