using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceRevisionTests
{
    [Fact]
    public async Task Revision_recovery_uses_original_receipts_after_later_versions_without_requiring_history_permission()
    {
        await using var data = await Data(); var service = Service(data); var (maker, reviewer, pool) = await Setup(data, service);
        var proposed = await Propose(service, maker, pool);
        var approved = await Approve(service, reviewer, proposed["proposalId"].AsInt64);
        var next = await service.ProposeRevisionAsync(maker, pool, 2, Changed() with { Label = "Later configuration" }, Meta("next"));
        await Approve(service, reviewer, next["proposalId"].AsInt64, "next-approval");
        await Rows(data, "authorizationGrants").UpdateManyAsync(new BsonDocument(),
            Builders<BsonDocument>.Update.Pull("permissions", Permissions.StockSourceRead));
        var before = new BsonArray(await Rows(data, "inventoryPools").Find(new BsonDocument()).ToListAsync());
        Assert.Equal(proposed, await service.RecoverRevisionAsync(maker, Location, "stock.source.revision.propose", "revision", "recovery"));
        Assert.Equal(approved, await service.RecoverRevisionAsync(reviewer, Location, "stock.source.revision.approve", "approval", "recovery"));
        Assert.Null(await service.RecoverRevisionAsync(maker, Location, "stock.source.revision.propose", "unknown", "recovery"));
        Assert.Equal(before, new BsonArray(await Rows(data, "inventoryPools").Find(new BsonDocument()).ToListAsync()));
        Assert.Equal(2, await Count(data, "stockSourceRevisionProposals"));
        Assert.Equal(2, await Count(data, "stockSourceRevisionDecisions"));
        Assert.Equal(6, await Count(data, "auditEvents")); Assert.Equal(6, await Count(data, "idempotencyRecords"));
    }

    [Fact]
    public async Task Source_and_revision_lists_filter_before_totals_and_page_in_stable_identity_order()
    {
        await using var data = await Data(); var service = Service(data); var (maker, reviewer, pool) = await Setup(data, service);
        var first = await Propose(service, maker, pool);
        await Approve(service, reviewer, first["proposalId"].AsInt64);
        var second = await service.ProposeRevisionAsync(maker, pool, 2, Changed() with { Label = "Pending change" }, Meta("pending"));
        var sources = await service.ListSourcesAsync(maker, Location, 0, 1);
        Assert.Equal(1, sources.Total); Assert.Single(sources.Items);
        Assert.Equal(2, sources.Items[0]["pool"]["revision"].AsInt64);
        var history = await service.ListRevisionsAsync(maker, Location, pool, 1, 1);
        Assert.Equal(2, history.Total); Assert.Single(history.Items);
        Assert.Equal(second["proposalId"], history.Items[0]["proposal"]["_id"]);
        Assert.Equal(0, (await service.ListRevisionsAsync(maker, Location, pool + 99, 0, 10)).Total);
        await Assert.ThrowsAsync<DomainValidationException>(() => service.ListSourcesAsync(maker, Location, -1, 10));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.ListRevisionsAsync(maker, Location, pool, 0, 501));
    }
}
