using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceAliasMigrationTests
{
    [Theory]
    [InlineData("stock.source.approve", "stock.source.reject")]
    [InlineData("stock.source.revision.approve", "stock.source.revision.reject")]
    [InlineData("stock.source.alias.approve", "stock.source.alias.reject")]
    public async Task Extra_receipt_cannot_borrow_a_different_terminal_operation(string original, string forged)
    {
        await using var data = await StoreTestData.CreateForStockSourceAliasMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data);
        if (original.Contains(".revision.", StringComparison.Ordinal))
        {
            var service = Service(data);
            var proposal = await service.ProposeRevisionAsync(actors.Maker, actors.Pool, 1,
                actors.Input with { Label = "Next settings" }, Meta("next-settings"));
            await service.DecideRevisionAsync(actors.Reviewer, proposal["proposalId"].AsInt64, 1,
                StockSourceSetupDecision.Approve, "synthetic-review", Meta("next-settings-approve"));
        }
        var receipt = await Rows(data, "idempotencyRecords").Find(new BsonDocument("operation", original)).SingleAsync();
        receipt["_id"] = ObjectId.GenerateNewId(); receipt["operation"] = forged; receipt["key"] = "borrowed-outcome";
        await Rows(data, "idempotencyRecords").InsertOneAsync(receipt);
        await RejectWithoutMutation(data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Alias_creation_or_approval_after_bound_settings_were_replaced_is_rejected(bool moveProposal)
    {
        await using var data = await StoreTestData.CreateForStockSourceAliasMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data); var service = Service(data);
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1);
        var revision = await service.ProposeRevisionAsync(actors.Maker, actors.Pool, 1,
            actors.Input with { Label = "Next settings" }, Meta("next-settings"));
        await service.DecideRevisionAsync(actors.Reviewer, revision["proposalId"].AsInt64, 1,
            StockSourceSetupDecision.Approve, "synthetic-review", Meta("next-settings-approve"));
        var forgedAt = data.Clock.UtcNow.AddSeconds(1).UtcDateTime;
        var decision = await Rows(data, "stockSourceAliasDecisions").Find(new BsonDocument()).SingleAsync();
        decision["decidedAt"] = forgedAt; await ReplaceHistoryRecord(data, decision, false);
        if (moveProposal)
        {
            var proposal = await Rows(data, "stockSourceAliasProposals").Find(new BsonDocument()).SingleAsync();
            proposal["proposedAt"] = forgedAt; await ReplaceHistoryRecord(data, proposal, true);
        }
        await RejectWithoutMutation(data);
    }

    [Fact]
    public async Task Proposal_cannot_claim_an_alias_base_already_replaced_when_it_was_created()
    {
        await using var data = await StoreTestData.CreateForStockSourceAliasMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data);
        var result = await Service(data).ProposeAliasAsync(actors.Maker, actors.Pool, 1, Requested, 1, false, Meta("pending"));
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1); await AddAlias(data, actors, 1, false, 1);
        var proposal = await Rows(data, "stockSourceAliasProposals").Find(new BsonDocument("_id", result["proposalId"])).SingleAsync();
        proposal["proposedAt"] = data.Clock.UtcNow.AddSeconds(1).UtcDateTime; await ReplaceHistoryRecord(data, proposal, true);
        await RejectWithoutMutation(data);
    }

    [Fact]
    public async Task Full_history_preflight_continues_past_the_first_cursor_batch()
    {
        await using var data = await StoreTestData.CreateForStockSourceAliasMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data);
        for (var i = 0; i < 64; i++)
            await Service(data).ProposeAliasAsync(actors.Maker, actors.Pool, 1, Requested, 1, false, Meta("pending-" + i));
        Assert.Equal(65, await Rows(data, "stockSourceAliasProposals").CountDocumentsAsync(new BsonDocument()));
        var last = await Rows(data, "stockSourceAliasProposals").Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).FirstAsync();
        await Rows(data, "auditEvents").UpdateOneAsync(new BsonDocument("_id", last["auditId"]),
            Builders<BsonDocument>.Update.Set("record.reason", "Unexplained change beyond the first batch"));
        await RejectWithoutMutation(data);
    }
}