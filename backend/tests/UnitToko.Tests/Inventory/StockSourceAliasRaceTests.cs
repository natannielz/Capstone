using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceAliasTests
{
    [Fact]
    public async Task Two_approvals_from_the_same_snapshot_create_exactly_one_mapping()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var first = await Propose(service, maker, pool); var second = await Propose(service, maker, pool, "second");
        using var barrier = new Barrier(2); var arrivals = 0;
        StockSourceSetupService Racing()
        {
            var captured = 0; var held = 0;
            var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "AliasRace-" + Guid.NewGuid().ToString("N");
            settings.ClusterConfigurator = builder =>
            {
                builder.Subscribe<CommandStartedEvent>(e =>
                {
                    if (e.CommandName == "find" && e.Command.GetValue("find", "") == "stockSourceAliasProposals" && e.Command.Contains("projection")) captured = e.RequestId;
                });
                builder.Subscribe<CommandSucceededEvent>(e =>
                {
                    if (e.RequestId != captured || Interlocked.CompareExchange(ref held, 1, 0) != 0) return;
                    Interlocked.Increment(ref arrivals);
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Both alias approvals must load their original snapshots.");
                });
            };
            var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
            return new(database, new(client), new(database), data.Clock);
        }
        async Task<bool> Attempt(StockSourceSetupService racing, long id, string key)
        {
            try { await Approve(racing, reviewer, id, key); return true; }
            catch (PersistenceConflictException) { return false; }
        }
        var results = await Task.WhenAll(Task.Run(() => Attempt(Racing(), first["proposalId"].AsInt64, "approve-first")),
            Task.Run(() => Attempt(Racing(), second["proposalId"].AsInt64, "approve-second")));
        Assert.Equal(2, arrivals); Assert.Single(results, x => x);
        Assert.Equal(1L, await Count(data, "stockSourceAliases")); Assert.Equal(1L, await Count(data, "stockSourceAliasDecisions"));
        Assert.Equal(5L, await Count(data, "auditEvents")); Assert.Equal(5L, await Count(data, "idempotencyRecords"));
        Assert.Equal(1L, (await service.ReadAliasAsync(maker, pool, Requested))["currentSource"]["pool"]["revision"].AsInt64);
    }

    [Theory]
    [InlineData("requested-grant")] [InlineData("owner-grant")] [InlineData("mfa-expiry")]
    public async Task Final_authority_checks_roll_back_already_written_alias_and_decision(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var proposal = await Propose(service, maker, pool);
        var before = await Rows(data, "inventoryPools").Find(new BsonDocument("_id", pool)).SingleAsync();
        var captured = 0; var changed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "AliasFinalFence-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == "insert" && e.Command.GetValue("insert", "") == "auditEvents" &&
                    e.Command["documents"].AsBsonArray[0]["operation"] == "stock.source.alias.approve") captured = e.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.RequestId != captured || Interlocked.CompareExchange(ref changed, 1, 0) != 0) return;
                if (fault == "mfa-expiry") data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(5).AddMilliseconds(1);
                else Rows(data, "authorizationGrants").UpdateOne(new BsonDocument("_id", fault == "requested-grant" ? 2001L : 2000L),
                    Builders<BsonDocument>.Update.Set("status", "revoked"));
            });
        };
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Approve(new(database, new(client), new(database), data.Clock),
            reviewer, proposal["proposalId"].AsInt64));
        Assert.Equal(1, changed);
        Assert.Equal(before, await Rows(data, "inventoryPools").Find(new BsonDocument("_id", pool)).SingleAsync());
        Assert.Equal(0L, await Count(data, "stockSourceAliases")); Assert.Equal(0L, await Count(data, "stockSourceAliasDecisions"));
        Assert.Equal(3L, await Count(data, "auditEvents")); Assert.Equal(3L, await Count(data, "idempotencyRecords"));
    }

    [Fact]
    public async Task Replayed_approval_still_requires_current_requested_channel_grant()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var proposal = await Propose(service, maker, pool); await Approve(service, reviewer, proposal["proposalId"].AsInt64);
        await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 2001L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Approve(service, reviewer, proposal["proposalId"].AsInt64));
        Assert.Equal(4L, await Count(data, "auditEvents")); Assert.Equal(4L, await Count(data, "idempotencyRecords"));
    }

    [Theory]
    [InlineData("missing")] [InlineData("warning")] [InlineData("unique")] [InlineData("optional-validator")]
    public async Task Unenforced_storage_prevents_alias_changes(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, _, pool) = await Setup(data); var service = Service(data);
        if (fault == "missing") await data.Database.DropCollectionAsync("stockSourceAliases");
        if (fault == "warning") await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "stockSourceAliasProposals" }, { "validationAction", "warn" } });
        if (fault == "unique") await Rows(data, "stockSourceAliases").Indexes.DropOneAsync("ux_stock_source_alias_version");
        if (fault == "optional-validator") await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        { { "collMod", "stockSourceAliasProposals" }, { "validator", new BsonDocument("$or", new BsonArray { StockSourceAliasSchemas.Validator("stockSourceAliasProposals"), new BsonDocument("_id", new BsonDocument("$gt", 0)) }) } });
        await Assert.ThrowsAsync<DomainValidationException>(() => Propose(service, maker, pool));
        Assert.Equal(0L, await Count(data, "stockSourceAliasProposals")); Assert.Equal(2L, await Count(data, "idempotencyRecords"));
    }

    [Fact]
    public async Task Strict_database_schema_rejects_transfers_unknown_fields_numeric_coercion_and_invalid_decisions()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var proposed = await Propose(service, maker, pool); await Approve(service, reviewer, proposed["proposalId"].AsInt64);
        var original = await Rows(data, "stockSourceAliasProposals").Find(new BsonDocument()).SingleAsync();
        foreach (var fault in new[] { "same-channel", "warehouse", "organization", "fractional", "unknown", "disable-absent", "overflow", "bad-time" })
        {
            var row = original.DeepClone().AsBsonDocument; row["_id"] = 9999L;
            switch (fault)
            {
                case "same-channel": row["requestedLocation"]["channelId"] = 103L; break;
                case "warehouse": row["requestedLocation"]["warehouseId"] = 111L; break;
                case "organization": row["requestedLocation"]["organizationId"] = 111L; break;
                case "fractional": row["poolRevision"] = 1.5; break;
                case "unknown": row.Add("unsafe", true); break;
                case "disable-absent": row["enabled"] = false; break;
                case "overflow": row["baseAliasRevision"] = long.MaxValue; break;
                case "bad-time": row["proposedAt"] = new BsonDateTime(long.MaxValue); break;
            }
            var failed = await Assert.ThrowsAsync<MongoWriteException>(() => Rows(data, "stockSourceAliasProposals").InsertOneAsync(row));
            Assert.Equal(121, failed.WriteError.Code);
        }
        var decision = await Rows(data, "stockSourceAliasDecisions").Find(new BsonDocument()).SingleAsync();
        decision["_id"] = 9999L; decision["proposalId"] = 9999L; decision["aliasRevision"] = BsonNull.Value;
        Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => Rows(data, "stockSourceAliasDecisions").InsertOneAsync(decision))).WriteError.Code);
        Assert.Equal(1L, await Count(data, "stockSourceAliasProposals")); Assert.Equal(1L, await Count(data, "stockSourceAliasDecisions"));
    }

    [Fact]
    public async Task Pending_alias_cannot_approve_after_settings_change_and_old_history_stays_readable()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var proposal = await Propose(service, maker, pool);
        var settings = await service.ProposeRevisionAsync(maker, pool, 1, Input() with { Label = "New current settings" }, Meta("settings"));
        await service.DecideRevisionAsync(reviewer, settings["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, "synthetic-review", Meta("approve-settings"));
        Assert.Equal("stock_source_revision_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(
            () => Approve(service, reviewer, proposal["proposalId"].AsInt64))).Code);
        await service.DecideAliasAsync(maker, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Withdraw, "stale proposal", Meta("withdraw"));
        Assert.Single((await service.ListAliasProposalsAsync(maker, pool, Requested, 0, 25)).Items);
        Assert.Equal(0L, await Count(data, "stockSourceAliases"));
    }
}
