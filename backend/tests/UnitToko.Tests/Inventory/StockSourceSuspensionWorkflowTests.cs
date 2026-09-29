using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceSuspensionTests
{
    [Fact]
    public async Task Command_only_operator_can_preview_and_recover_without_configuration_history_permission()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (_, _, actor, pool) = await Setup(data); var service = Service(data);
        await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", actor.UserId),
            Builders<BsonDocument>.Update.Set("permissions", new BsonArray { SuspendPermission }));
        var expected = Expected(await service.PreviewSuspensionAsync(actor, pool));
        var result = await service.SuspendAsync(actor, pool, expected, Meta("lost"));
        Assert.Equal(result, await service.RecoverSuspensionAsync(actor, pool, "lost", "recovery"));
        Assert.Null(await service.RecoverSuspensionAsync(actor, pool, "never-sent", "recovery"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadSuspensionAsync(actor, pool));
        Assert.Equal(1L, await Count(data, StockSourceSuspensionSchemas.Collection));
        Assert.Equal(3L, await Count(data, "idempotencyRecords"));
    }
    [Theory]
    [InlineData("owner")] [InlineData("linked")] [InlineData("mfa")] [InlineData("read-only")]
    public async Task Every_linked_scope_and_fresh_action_permission_are_required(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, _, actor, pool) = await Setup(data, true); var service = Service(data);
        var expected = Expected(await service.PreviewSuspensionAsync(actor, pool)); var before = await Pool(data, pool);
        if (fault == "mfa") data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(5).AddMilliseconds(1);
        else if (fault == "read-only") actor = maker;
        else await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", fault == "owner" ? 3000L : 3001L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.SuspendAsync(actor, pool, expected, Meta("denied")));
        Assert.Equal(before, await Pool(data, pool)); Assert.Equal(0L, await Count(data, StockSourceSuspensionSchemas.Collection));
        Assert.Equal(4L, await Count(data, "idempotencyRecords"));
    }
    [Theory]
    [InlineData("settings")] [InlineData("mapping")]
    public async Task Changed_configuration_requires_new_review_and_historical_suspension_retains_original_binding(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, actor, pool) = await Setup(data, true); var service = Service(data);
        var firstExpected = Expected(await service.PreviewSuspensionAsync(actor, pool));
        await service.SuspendAsync(actor, pool, firstExpected, Meta("first"));
        var historical = await service.ReadSuspensionAsync(actor, pool);
        var stale = Expected(await service.PreviewSuspensionAsync(actor, pool));
        if (fault == "settings")
        {
            var change = await service.ProposeRevisionAsync(maker, pool, 1, Input() with { SafetyBufferBaseUnits = 4 }, Meta("change-settings"));
            await service.DecideRevisionAsync(reviewer, change["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-settings", Meta("approve-settings"));
        }
        else
        {
            var change = await service.ProposeAliasAsync(maker, pool, 1, Linked, 1, false, Meta("change-mapping"));
            await service.DecideAliasAsync(reviewer, change["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-disable", Meta("approve-mapping"));
        }
        Assert.Equal(historical, await service.ReadSuspensionAsync(actor, pool));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.SuspendAsync(actor, pool, stale, Meta("stale")));
        Assert.Equal(1L, await Count(data, StockSourceSuspensionSchemas.Collection));
        var current = Expected(await service.PreviewSuspensionAsync(actor, pool));
        var accepted = await service.SuspendAsync(actor, pool, current, Meta("changed-stop"));
        Assert.Equal(2L, accepted["suspensionRevision"].AsInt64);
        Assert.Equal(historical, await Rows(data, StockSourceSuspensionSchemas.Collection).Find(new BsonDocument("suspensionRevision", 1L)).SingleAsync());
        Assert.Equal(2L, await Count(data, StockSourceSuspensionSchemas.Collection));
        Assert.Equal(historical!["_id"], (await service.RecoverSuspensionAsync(actor, pool, "first", "historical-recovery"))!["suspensionId"]);
    }
    [Fact]
    public async Task Disabling_a_mapping_is_not_proof_that_its_channel_obligations_are_drained()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, actor, pool) = await Setup(data, true); var service = Service(data);
        var change = await service.ProposeAliasAsync(maker, pool, 1, Linked, 1, false, Meta("disable"));
        await service.DecideAliasAsync(reviewer, change["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-disabled", Meta("disabled"));
        var preview = await service.PreviewSuspensionAsync(actor, pool);
        Assert.Equal(2, preview["affectedLocations"].AsBsonArray.Count);
        await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 3001L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.SuspendAsync(actor, pool, Expected(preview), Meta("denied")));
    }
    [Fact]
    public async Task Unrelated_pool_fence_changes_do_not_change_the_reviewed_configuration()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (_, _, actor, pool) = await Setup(data); var service = Service(data);
        var expected = Expected(await service.PreviewSuspensionAsync(actor, pool));
        await Rows(data, "inventoryPools").UpdateOneAsync(new BsonDocument("_id", pool), Builders<BsonDocument>.Update.Inc("fence", 1L));
        Assert.Equal(expected, Expected(await service.PreviewSuspensionAsync(actor, pool)));
        await service.SuspendAsync(actor, pool, expected, Meta("stop"));
        Assert.Equal(2L, (await Pool(data, pool))["fence"].AsInt64);
    }
    [Theory]
    [InlineData("missing")] [InlineData("warning")] [InlineData("index")]
    public async Task Unenforced_storage_prevents_suspension(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (_, _, actor, pool) = await Setup(data); var service = Service(data);
        var expected = Expected(await service.PreviewSuspensionAsync(actor, pool)); var before = await Pool(data, pool);
        if (fault == "missing") await data.Database.DropCollectionAsync(StockSourceSuspensionSchemas.Collection);
        if (fault == "warning") await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", StockSourceSuspensionSchemas.Collection }, { "validationAction", "warn" } });
        if (fault == "index") await Rows(data, StockSourceSuspensionSchemas.Collection).Indexes.DropOneAsync("ux_stock_source_suspension_revision");
        await Assert.ThrowsAsync<DomainValidationException>(() => service.SuspendAsync(actor, pool, expected, Meta("unsafe")));
        Assert.Equal(before, await Pool(data, pool)); Assert.Equal(2L, await Count(data, "idempotencyRecords"));
    }
    [Theory]
    [InlineData("audit")] [InlineData("receipt")] [InlineData("hash")] [InlineData("tail")] [InlineData("source")]
    public async Task Corrupted_history_is_not_returned_as_a_verified_stop(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (_, _, actor, pool) = await Setup(data); var service = Service(data);
        await service.SuspendAsync(actor, pool, Expected(await service.PreviewSuspensionAsync(actor, pool)), Meta("stop"));
        var row = await Rows(data, StockSourceSuspensionSchemas.Collection).Find(new BsonDocument()).SingleAsync();
        if (fault == "audit") await Rows(data, "auditEvents").DeleteOneAsync(new BsonDocument("_id", row["auditId"]));
        if (fault == "receipt") await Rows(data, "idempotencyRecords").DeleteOneAsync(new BsonDocument("operation", "stock.source.suspend"));
        if (fault == "tail") await Rows(data, StockSourceSuspensionSchemas.Collection).DeleteOneAsync(new BsonDocument("_id", row["_id"]));
        if (fault is "hash" or "source")
        {
            if (fault == "hash") row["configurationHash"] = new string('a', 64);
            else
            {
                row["configuration"]["source"]["input"]["label"] = "Substituted source configuration";
                row["configurationHash"] = StockSourceSuspensionBson.Hash(row["configuration"].AsBsonDocument);
                await Rows(data, "auditEvents").UpdateOneAsync(new BsonDocument("_id", row["auditId"]), Builders<BsonDocument>.Update.Set("record", row.DeepClone()));
            }
            await Rows(data, StockSourceSuspensionSchemas.Collection).ReplaceOneAsync(new BsonDocument("_id", row["_id"]), row);
        }
        await Assert.ThrowsAsync<DomainValidationException>(() => service.ReadSuspensionAsync(actor, pool));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.RecoverSuspensionAsync(actor, pool, "stop", "recover"));
        Assert.False((await Pool(data, pool))["active"].AsBoolean);
    }
    [Fact]
    public async Task Recovery_never_reads_another_actors_receipt_or_resubmits_a_missing_command()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (_, _, actor, pool) = await Setup(data); var service = Service(data);
        var expected = Expected(await service.PreviewSuspensionAsync(actor, pool));
        var result = await service.SuspendAsync(actor, pool, expected, Meta("stop"));
        var second = await data.AddActorAsync(4000, Owner.ToScope(), SuspendPermission);
        Assert.Null(await service.RecoverSuspensionAsync(second, pool, "stop", "recover"));
        Assert.Null(await service.RecoverSuspensionAsync(actor, pool, "missing", "recover"));
        Assert.Equal(result, await service.RecoverSuspensionAsync(actor, pool, "stop", "recover"));
        Assert.Equal(1L, await Count(data, StockSourceSuspensionSchemas.Collection));
        Assert.Equal(3L, await Count(data, "idempotencyRecords"));
    }
}
