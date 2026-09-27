using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class HistoricalReadStoreTests(MongoFixture fixture)
{
    [Theory]
    [InlineData("orders")]
    [InlineData("invoices")]
    [InlineData("shipments")]
    public async Task Canonical_branch_and_division_scope_precedes_all_counts_pages_search_details_and_metadata_references(string sourceKey)
    {
        await using var data = await HistoricalStoreData.CreateAsync(fixture, sourceKey);
        var list = await data.Reads.QueryAsync(data.Actor, data.Source, data.Request, "test", limit: 2);
        Assert.Equal(3, list.Total);
        Assert.Equal(new long[] { 1, 2 }, list.Items.Select(item => item.Id));
        Assert.Equal(3, await data.Reads.CountAsync(data.Actor, data.Source, data.Request, "test"));
        Assert.Equal(3, Assert.Single((await data.Reads.QueryAsync(data.Actor, data.Source, data.Request, "test", 2, 1)).Items).Id);
        Assert.Empty((await data.Reads.QueryAsync(data.Actor, data.Source, data.Request, "test", 3, 1)).Items);
        Assert.Equal(2, await data.Reads.CountAsync(data.Actor, data.Source, data.Request, "test", "Coffee"));
        Assert.Equal(1, (await data.Reads.QueryAsync(data.Actor, data.Source, data.Request, "test", search: ".*")).Total);
        Assert.Equal(0, await data.Reads.CountAsync(data.Actor, data.Source, data.Request, "test", "Hidden"));
        var visible = await data.Reads.ReadAsync(data.Actor, data.Source, data.Request, "test", 1);
        Assert.Equal(list.Items[0], visible);
        Assert.Equal(visible, await data.Reads.ReadPublicAsync(data.Actor, data.Source, data.Request, "test", data.PublicId(1)));
        Assert.Null(await data.Reads.ReadAsync(data.Actor, data.Source, data.Request, "test", 4));
        Assert.Null(await data.Reads.ReadAsync(data.Actor, data.Source, data.Request, "test", 999));
        Assert.Null(await data.Reads.ReadPublicAsync(data.Actor, data.Source, data.Request, "test", data.PublicId(4)));
        foreach (var purpose in Enum.GetValues<HistoricalMetadataPurpose>())
        {
            Assert.Equal(new HistoricalMetadataReference(purpose, visible!), await data.Reads.ReadReferenceAsync(data.Actor, data.Source, data.Request, "test", 1, purpose));
            Assert.Null(await data.Reads.ReadReferenceAsync(data.Actor, data.Source, data.Request, "test", 4, purpose));
        }
        var division = data.Request with { TargetScope = new(GrantScopeKind.BuyingDivision, 10, 11, 13) };
        Assert.Equal(new long[] { 2 }, (await data.Reads.QueryAsync(data.Actor, data.Source, division, "test")).Items.Select(item => item.Id));
        var report = data.Request with { Permission = Permissions.ReportRead, Purpose = "reporting" };
        Assert.Equal(3, await data.Reads.CountAsync(data.Actor, data.Source, report, "test"));
        Assert.Equal(new[] { "DisplayName", "Id", "PublicId" }, typeof(HistoricalMetadata).GetProperties().Select(property => property.Name).Order());
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(visible), StringComparison.OrdinalIgnoreCase);
        var audits = await data.Events.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        Assert.All(audits, row =>
        {
            Assert.Equal(sourceKey, row["source"].AsString);
            Assert.Equal(data.Actor.UserId, row["actorId"].AsInt64);
            Assert.Equal(new BsonArray { data.Actor.UserId }, row["grantIds"]);
            Assert.DoesNotContain("secret", row.ToJson(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Coffee", row.ToJson());
            Assert.DoesNotContain("search", row.ToJson(), StringComparison.OrdinalIgnoreCase);
        });
        Assert.NotNull(await data.Reads.ReadAsync(data.Actor, data.Source, data.Request, "test", 3)); // dual buying + fulfillment scope
        await data.Base.Database.GetCollection<BsonDocument>("authorizationScopes").UpdateManyAsync(
            Builders<BsonDocument>.Filter.In("_id", new long[] { 100, 101, 103 }), Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
        Assert.NotNull(await data.Reads.ReadAsync(data.Actor, data.Source, data.Request, "test", 3));
    }

    [Theory]
    [InlineData("id-int")]
    [InlineData("buyer-array")]
    [InlineData("branch-int")]
    [InlineData("division-array")]
    [InlineData("fulfillment-array")]
    [InlineData("negative-owner")]
    [InlineData("orphan-shop")]
    [InlineData("orphan-warehouse")]
    [InlineData("orphan-channel")]
    [InlineData("assigned-array")]
    [InlineData("administrative-array")]
    [InlineData("public-array")]
    [InlineData("uuid-v1")]
    [InlineData("uuid-newline")]
    [InlineData("name-newline")]
    [InlineData("name-control")]
    [InlineData("name-array")]
    [InlineData("missing-buyer")]
    [InlineData("division-wrong-parent")]
    [InlineData("division-revoked")]
    [InlineData("division-pending")]
    public async Task Malformed_and_foreign_scope_rows_are_excluded_before_counts_and_every_lookup(string malformed)
    {
        await using var data = await HistoricalStoreData.CreateAsync(fixture);
        var row = data.Resource(30, 11, 13, "Malformed secret search");
        switch (malformed)
        {
            case "id-int": row["_id"] = 30; break;
            case "buyer-array": row["buyingOrganizationId"] = new BsonArray { 10L }; break;
            case "branch-int": row["branchId"] = 11; break;
            case "division-array": row["divisionId"] = new BsonArray { 13L }; break;
            case "fulfillment-array": row["fulfillmentOrganizationId"] = new BsonArray { 100L }; break;
            case "negative-owner": row["createdBy"] = -1L; break;
            case "orphan-shop": row["shopId"] = 101L; break;
            case "orphan-warehouse": row["warehouseId"] = 102L; break;
            case "orphan-channel": row["channelId"] = 103L; break;
            case "assigned-array": row["assignedUserIds"] = new BsonArray { new BsonArray { 1000L } }; break;
            case "administrative-array": row["isAdministrative"] = new BsonArray { false }; break;
            case "public-array": row["publicId"] = new BsonArray { data.PublicId(30) }; break;
            case "uuid-v1": row["publicId"] = "00000000-0000-1000-8000-000000000030"; break;
            case "uuid-newline": row["publicId"] = data.PublicId(30) + "\n"; break;
            case "name-newline": row["displayName"] = "Malformed\n"; break;
            case "name-control": row["displayName"] = "Malformed\u0085"; break;
            case "name-array": row["displayName"] = new BsonArray { "Malformed" }; break;
            case "missing-buyer": row.Remove("buyingOrganizationId"); break;
            case "division-wrong-parent": row["divisionId"] = 15L; break;
            case "division-revoked": row["divisionId"] = 16L; break;
            case "division-pending": row["divisionId"] = 17L; break;
        }
        await data.Resources.InsertOneAsync(row, new InsertOneOptions { BypassDocumentValidation = true });
        Assert.Equal(3, await data.Reads.CountAsync(data.Actor, data.Source, data.Request, "test"));
        Assert.Equal(0, (await data.Reads.QueryAsync(data.Actor, data.Source, data.Request, "test", search: "Malformed")).Total);
        Assert.Null(await data.Reads.ReadAsync(data.Actor, data.Source, data.Request, "test", 30));
        Assert.Null(await data.Reads.ReadPublicAsync(data.Actor, data.Source, data.Request, "test", data.PublicId(30)));
        foreach (var purpose in Enum.GetValues<HistoricalMetadataPurpose>())
            Assert.Null(await data.Reads.ReadReferenceAsync(data.Actor, data.Source, data.Request, "test", 30, purpose));
    }

    [Fact]
    public async Task Duplicate_public_ids_fail_closed_and_optional_metadata_remains_optional()
    {
        await using var data = await HistoricalStoreData.CreateAsync(fixture);
        var minimal = data.Resource(30, 11, null, "Temporary"); minimal.Remove("publicId"); minimal.Remove("displayName");
        await data.Resources.InsertOneAsync(minimal);
        Assert.Equal(new HistoricalMetadata(30, null, null), await data.Reads.ReadAsync(data.Actor, data.Source, data.Request, "test", 30));
        var duplicate = data.Resource(31, 12, null, "Hidden duplicate"); duplicate["publicId"] = data.PublicId(1);
        await data.Resources.InsertOneAsync(duplicate);
        Assert.Equal(1, (await data.Reads.ReadPublicAsync(data.Actor, data.Source, data.Request, "test", data.PublicId(1)))!.Id);
        await data.Resources.UpdateOneAsync(new BsonDocument("_id", 31L), Builders<BsonDocument>.Update.Set("branchId", 11L));
        var before = await data.Events.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        Assert.Equal("ambiguous_historical_identifier", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Reads.ReadPublicAsync(data.Actor, data.Source, data.Request, "test", data.PublicId(1)))).Code);
        Assert.Equal(before, await data.Events.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("grant")]
    [InlineData("membership")]
    [InlineData("parent")]
    [InlineData("branch")]
    [InlineData("division")]
    [InlineData("division-reparent")]
    [InlineData("grant-expiry")]
    [InlineData("membership-expiry")]
    [InlineData("approval-provenance")]
    public async Task Transaction_fences_retry_current_actor_authority_and_target_before_returning_or_auditing(string change)
    {
        await using var data = await HistoricalStoreData.CreateAsync(fixture);
        if (change.EndsWith("-expiry", StringComparison.Ordinal))
        {
            await data.Base.Database.GetCollection<BsonDocument>(change == "grant-expiry" ? "authorizationGrants" : "memberships")
                .UpdateOneAsync(new BsonDocument("_id", data.Actor.UserId), Builders<BsonDocument>.Update.Set("expiresAt", data.Base.Clock.UtcNow.AddMinutes(1).UtcDateTime));
            if (change == "membership-expiry")
                await data.Base.Database.GetCollection<BsonDocument>("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Actor.UserId),
                    Builders<BsonDocument>.Update.Set("expiresAt", data.Base.Clock.UtcNow.AddMinutes(1).UtcDateTime));
        }
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = data.CreateReads(async token => { read.TrySetResult(); await resume.Task.WaitAsync(token); });
        var request = change.StartsWith("division", StringComparison.Ordinal) ? data.Request with { TargetScope = new(GrantScopeKind.BuyingDivision, 10, 11, 13) } : data.Request;
        var pending = store.QueryAsync(data.Actor, data.Source, request, "race");
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (change.EndsWith("-expiry", StringComparison.Ordinal)) data.Base.Clock.UtcNow += TimeSpan.FromMinutes(1);
            else if (change == "actor") await data.Base.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", data.Actor.UserId), Builders<BsonDocument>.Update.Inc("grantVersion", 1L));
            else if (change is "parent" or "branch" or "division" or "division-reparent")
                await data.Base.Database.GetCollection<BsonDocument>("authorizationScopes").UpdateOneAsync(
                    new BsonDocument("_id", change == "parent" ? 10L : change == "branch" ? 11L : 13L),
                    change == "division-reparent" ? Builders<BsonDocument>.Update.Set("parentId", 12L).Inc("version", 1L) :
                        Builders<BsonDocument>.Update.Set("status", "revoked").Inc("version", 1L));
            else if (change == "approval-provenance") await data.Base.Database.GetCollection<BsonDocument>("authorizationGrants")
                .UpdateOneAsync(new BsonDocument("_id", data.Actor.UserId), Builders<BsonDocument>.Update.Set("approvedBy", data.Actor.UserId).Inc("version", 1L));
            else await data.Base.Database.GetCollection<BsonDocument>(change == "grant" ? "authorizationGrants" : "memberships")
                .UpdateOneAsync(new BsonDocument("_id", data.Actor.UserId), Builders<BsonDocument>.Update.Set("status", "revoked").Set("revokedAt", data.Base.Clock.UtcNow.UtcDateTime).Inc("version", 1L));
        }
        finally { resume.TrySetResult(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending);
        Assert.Equal(0, await data.Events.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(0, (await data.Base.Database.GetCollection<BsonDocument>("identities").Find(new BsonDocument("_id", data.Actor.UserId)).SingleAsync())["fence"].AsInt64);
    }

    [Fact]
    public async Task Branch_count_fences_child_division_and_retries_to_exclude_newly_revoked_descendant()
    {
        await using var data = await HistoricalStoreData.CreateAsync(fixture);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var store = data.CreateReads(async token => { if (Interlocked.Increment(ref calls) == 1) { read.TrySetResult(); await resume.Task.WaitAsync(token); } });
        var pending = store.CountAsync(data.Actor, data.Source, data.Request, "race");
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await data.Base.Database.GetCollection<BsonDocument>("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 13L), Builders<BsonDocument>.Update.Set("status", "revoked").Inc("version", 1L)); }
        finally { resume.TrySetResult(); }
        Assert.Equal(2, await pending);
        Assert.True(calls >= 2);
        Assert.Equal(2, (await data.Events.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync())["resultCount"].AsInt64);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("expire")]
    public async Task Paused_and_deferred_reference_reads_revalidate_live_session(string change)
    {
        await using var data = await HistoricalStoreData.CreateAsync(fixture);
        var unit = new MongoUnitOfWork(fixture.Client);
        var sessions = new MongoIdentitySessionStore(data.Base.Database, unit, data.Base.Clock,
            new(data.Actor.ProviderIssuer, "fixture", "fixture"), SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        await data.Base.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", data.Actor.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
        var proof = new ProviderIdentityProof(data.Actor.ProviderIssuer, data.Actor.ProviderSubject, "fixture@example.test", true, "fixture", data.Base.Clock.UtcNow, data.Base.Clock.UtcNow, "otp", "fixture");
        var issued = await unit.ExecuteAsync((tx, token) => sessions.CreateAsync(tx, new(data.Actor.UserId, 1, data.Actor.GrantVersion, data.Actor.ProviderIssuer, data.Actor.ProviderSubject), proof, "fixture", token));
        var actor = data.Actor with { Session = issued.Session.Reference };
        Assert.NotNull(await data.Reads.ReadReferenceAsync(actor, data.Source, data.Request, "initial", 1, HistoricalMetadataPurpose.Evidence));
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = data.CreateReads(async token => { read.TrySetResult(); await resume.Task.WaitAsync(token); });
        var pending = store.ReadReferenceAsync(actor, data.Source, data.Request, "race", 1, HistoricalMetadataPurpose.Evidence);
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { if (change == "revoke") await sessions.RevokeAsync(issued.OpaqueToken, "owner_logout"); else data.Base.Clock.UtcNow += SessionPolicy.IdleTimeout; }
        finally { resume.TrySetResult(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending);
        foreach (var purpose in Enum.GetValues<HistoricalMetadataPurpose>())
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Reads.ReadReferenceAsync(actor, data.Source, data.Request, "delivery", 1, purpose));
        Assert.Single(await data.Events.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());
    }

    [Fact]
    public async Task Historical_grant_never_authorizes_ordinary_business_writes()
    {
        await using var data = await HistoricalStoreData.CreateAsync(fixture);
        var actor = data.Actor;
        Assert.Equal(3, await data.Reads.CountAsync(actor, data.Source, data.Request, "test"));
        var invoked = false;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Base.Store.ExecuteAuthorizedAsync(actor, "fixture.write", Permissions.OrderSubmit,
            new("unique-write", "Synthetic attempted write", "test"), new { Id = 1L },
            (_, _) => Task.FromResult(new AuthorizationTarget(new(BuyingOrganizationId: 10, BranchId: 11))),
            (_, _) => { invoked = true; return Task.FromResult(new BsonDocument()); }));
        Assert.False(invoked);
        Assert.Equal(5, await data.Resources.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("session")]
    public async Task Expiry_during_audit_insert_is_revalidated_and_rolls_back_the_inserted_event(string expiry)
    {
        await using var data = await HistoricalStoreData.CreateAsync(fixture);
        var actor = data.Actor;
        if (expiry == "grant")
            await data.Base.Database.GetCollection<BsonDocument>("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", actor.UserId),
                Builders<BsonDocument>.Update.Set("expiresAt", data.Base.Clock.UtcNow.AddMinutes(1).UtcDateTime));
        else
        {
            var unit = new MongoUnitOfWork(fixture.Client);
            var sessions = new MongoIdentitySessionStore(data.Base.Database, unit, data.Base.Clock,
                new(actor.ProviderIssuer, "fixture", "fixture"), SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
            await data.Base.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
            var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "fixture@example.test", true,
                "fixture", data.Base.Clock.UtcNow, data.Base.Clock.UtcNow, "otp", "fixture");
            var issued = await unit.ExecuteAsync((tx, token) => sessions.CreateAsync(tx,
                new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "fixture", token));
            actor = actor with { Session = issued.Session.Reference };
        }
        var auditRequestId = 0;
        var observedAudit = false;
        var committedFence = (await data.Base.Database.GetCollection<BsonDocument>("identities")
            .Find(new BsonDocument("_id", actor.UserId)).SingleAsync())["fence"].AsInt64;
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.Command.TryGetValue("insert", out var collection) && collection == "businessReadEvents")
                    auditRequestId = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (command.CommandName == "insert" && command.RequestId == auditRequestId)
                {
                    observedAudit = true;
                    data.Base.Clock.UtcNow += expiry == "grant" ? TimeSpan.FromMinutes(1) : SessionPolicy.IdleTimeout;
                }
            });
        };
        var client = new MongoClient(settings);
        var database = client.GetDatabase(data.Base.Database.DatabaseNamespace.DatabaseName);
        var store = new HistoricalReadStore(database, new(database, new(client), data.Base.Clock), new(database), data.Base.Clock);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => store.CountAsync(actor, data.Source, data.Request, "audit-race"));
        Assert.True(observedAudit);
        Assert.Equal(0, await data.Events.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(committedFence, (await data.Base.Database.GetCollection<BsonDocument>("identities").Find(new BsonDocument("_id", actor.UserId)).SingleAsync())["fence"].AsInt64);
    }

    [Fact]
    public async Task More_than_256_current_authority_alternatives_fail_explicitly_before_audit_or_fences()
    {
        await using var data = await HistoricalStoreData.CreateAsync(fixture);
        var grants = data.Base.Database.GetCollection<BsonDocument>("authorizationGrants");
        var grant = await grants.Find(new BsonDocument("_id", data.Actor.UserId)).SingleAsync();
        await grants.InsertManyAsync(Enumerable.Range(1, 256).Select(index =>
        {
            var alternative = grant.DeepClone().AsBsonDocument; alternative["_id"] = 2000L + index; return alternative;
        }));
        Assert.Equal("historical_authority_limit", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Reads.CountAsync(data.Actor, data.Source, data.Request, "test"))).Code);
        Assert.Equal(0, await data.Events.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Invalid_inputs_and_failed_audit_write_release_no_data_and_leave_no_committed_fence()
    {
        await using var data = await HistoricalStoreData.CreateAsync(fixture);
        Assert.Throws<DomainValidationException>(() => HistoricalReadSource.Resolve("identities"));
        Assert.Empty(typeof(HistoricalReadSource).GetConstructors());
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Reads.QueryAsync(data.Actor, data.Source, data.Request, "test", -1));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Reads.QueryAsync(data.Actor, data.Source, data.Request, "test", limit: 101));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Reads.QueryAsync(data.Actor, data.Source, data.Request, "test", search: new string('x', 129)));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Reads.CountAsync(data.Actor, data.Source, data.Request, "test", "bad\nsearch"));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Reads.CountAsync(data.Actor, data.Source, data.Request with { Purpose = "secret purpose" }, "test"));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Reads.CountAsync(data.Actor, data.Source, data.Request, "bad\ncorrelation"));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Reads.ReadAsync(data.Actor, data.Source, data.Request, "test", 0));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Reads.ReadPublicAsync(data.Actor, data.Source, data.Request, "test", "00000000-0000-1000-8000-000000000001"));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Reads.ReadReferenceAsync(data.Actor, data.Source, data.Request, "test", 1, (HistoricalMetadataPurpose)99));
        var definition = await (await data.Base.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "businessReadEvents") })).SingleAsync();
        await data.Base.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "collMod", "businessReadEvents" }, { "validator", new BsonDocument("$and", new BsonArray { definition["options"]["validator"], new BsonDocument("actorId", 99999L) }) },
            { "validationLevel", "strict" }, { "validationAction", "error" }
        });
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Reads.CountAsync(data.Actor, data.Source, data.Request, "test"));
        Assert.Equal(0, await data.Events.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(0, (await data.Base.Database.GetCollection<BsonDocument>("identities").Find(new BsonDocument("_id", data.Actor.UserId)).SingleAsync())["fence"].AsInt64);
    }
}

internal sealed class HistoricalStoreData : IAsyncDisposable
{
    internal StoreTestData Base { get; }
    internal HistoricalReadSource Source { get; }
    internal ActorContext Actor { get; private set; } = null!;
    internal HistoricalReadRequest Request { get; } = new(Permissions.HistoryRead, new(GrantScopeKind.BuyingBranch, 10, 11), "historical-review");
    internal HistoricalReadStore Reads { get; }
    internal IMongoCollection<BsonDocument> Resources => Base.Database.GetCollection<BsonDocument>(Source.Key);
    internal IMongoCollection<BsonDocument> Events => Base.Database.GetCollection<BsonDocument>("businessReadEvents");
    private HistoricalStoreData(StoreTestData data, string source)
    {
        Base = data; Source = HistoricalReadSource.Resolve(source); Reads = CreateReads();
    }
    internal HistoricalReadStore CreateReads(Func<CancellationToken, Task>? afterRead = null) => new(Base.Database, Base.Store, new(Base.Database), Base.Clock, afterRead);
    internal static async Task<HistoricalStoreData> CreateAsync(MongoFixture fixture, string source = "orders")
    {
        var data = new HistoricalStoreData(await StoreTestData.CreateAsync(fixture), source);
        data.Base.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        data.Actor = await data.Base.AddActorAsync(1000, new(GrantScopeKind.BuyingOrganization, 10), Permissions.HistoryRead, Permissions.ReportRead);
        await data.Base.Database.GetCollection<BsonDocument>("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Actor.UserId),
            Builders<BsonDocument>.Update.Set("templateId", "historical-read").Set("scope", AuthorizationBson.Scope(data.Request.TargetScope)));
        await data.Base.Database.GetCollection<BsonDocument>("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 11L), Builders<BsonDocument>.Update.Set("status", "suspended"));
        await data.Base.Database.GetCollection<BsonDocument>("authorizationScopes").InsertManyAsync(new[]
        {
            new ScopeNode(13, ScopeNodeKind.Division, 11, AuthorizationStatus.Active, 1),
            new(14, ScopeNodeKind.Division, 11, AuthorizationStatus.Suspended, 1),
            new(15, ScopeNodeKind.Division, 12, AuthorizationStatus.Active, 1),
            new(16, ScopeNodeKind.Division, 11, AuthorizationStatus.Revoked, 1),
            new(17, ScopeNodeKind.Division, 11, AuthorizationStatus.Pending, 1)
        }.Select(AuthorizationBson.Node));
        var dual = data.Resource(3, 11, 14, "Tea"); dual["fulfillmentOrganizationId"] = 100L; dual["shopId"] = 101L; dual["channelId"] = 103L;
        await data.Resources.InsertManyAsync(new[] { data.Resource(1, 11, null, "Coffee .*"), data.Resource(2, 11, 13, "Coffee division"), dual,
            data.Resource(4, 12, null, "Hidden outsider"), data.Resource(5, 11, 15, "Hidden foreign division") });
        return data;
    }
    internal string PublicId(long id) => $"00000000-0000-4000-8000-{id:000000000000}";
    internal BsonDocument Resource(long id, long branchId, long? divisionId, string name) => new()
    {
        { "_id", id }, { "schemaVersion", 1 }, { "version", 1L }, { "buyingOrganizationId", 10L }, { "branchId", branchId },
        { "divisionId", divisionId.HasValue ? new BsonInt64(divisionId.Value) : BsonNull.Value }, { "createdBy", Actor.UserId },
        { "publicId", PublicId(id) }, { "displayName", name }, { "financialAmount", "secret amount" }, { "bankEvidence", "secret evidence" },
        { "customerEmail", "secret person" }
    };
    public ValueTask DisposeAsync() => Base.DisposeAsync();
}
