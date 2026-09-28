using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Catalog;

public sealed partial class CatalogEvidenceUploadTargetTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Nonpositive_expected_revision_cannot_reach_callback(long revision)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var created = await data.FamilyAsync(); var called = false;
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => new MongoUnitOfWork(data.Client).ExecuteAsync((tx, ct) =>
            data.Service.WithCatalogImageUploadTargetAsync(data.Database, tx, data.Maker,
                new(EvidenceTargetKind.CatalogFamily, created.Id.Value), revision, (_, _) => { called = true; return Task.FromResult(1); }, ct)));
        Assert.Equal("invalid_catalog_request", error.Code); Assert.False(called);
    }

    [Fact]
    public async Task Definition_from_another_sku_is_not_accepted_as_upload_target_graph()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var created = await data.SkuAsync(); var called = false;
        await data.Rows("variantDefinitions").UpdateOneAsync(new BsonDocument("_id", created.CurrentDefinitionId!.Value.Value), U.Set("skuId", 900000L));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new MongoUnitOfWork(data.Client).ExecuteAsync((tx, ct) =>
            data.Service.WithCatalogImageUploadTargetAsync(data.Database, tx, data.Maker,
                new(EvidenceTargetKind.CatalogSku, created.Id.Value), 1, (_, _) => { called = true; return Task.FromResult(1); }, ct)));
        Assert.False(called);
    }

    [Theory]
    [InlineData("definition")]
    [InlineData("session")]
    public async Task Concurrent_definition_change_or_session_revocation_rolls_back_upload_metadata(string change)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var created = await data.SkuAsync();
        await data.Rows("evidenceUploadProbe").InsertOneAsync(new BsonDocument { { "_id", 1L }, { "value", 0 } });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var pending = new MongoUnitOfWork(data.Client).ExecuteAsync((tx, ct) =>
            data.Service.WithCatalogImageUploadTargetAsync(data.Database, tx, data.Maker,
                new(EvidenceTargetKind.CatalogSku, created.Id.Value), 1, async (_, token) =>
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        entered.SetResult();
                        await release.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
                    }
                    await data.Rows("evidenceUploadProbe").UpdateOneAsync(tx, new BsonDocument("_id", 1L), U.Inc("value", 1), cancellationToken: token);
                    return 1;
                }, ct));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (change == "definition")
                await data.Service.ReviseDefinitionAsync(data.Maker, new(created.Id, 1, Definition(label: "Kemasan baru")), Metadata("concurrent-definition"));
            else await data.Sessions.RevokeAsync(data.Issued[data.Maker.UserId].OpaqueToken, "synthetic evidence race");
        }
        finally { release.TrySetResult(); }
        if (change == "definition")
        {
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal("catalog_revision_conflict", error.Code);
        }
        else await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, attempts);
        Assert.Equal(0, (await data.RowAsync("evidenceUploadProbe", 1))["value"].AsInt32);
    }

    [Fact]
    public async Task Session_expiry_after_last_database_await_still_aborts_metadata()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var created = await data.FamilyAsync(); var deadline = data.Clock.UtcNow.AddSeconds(1);
        await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Maker.Session!.Id), U.Set("idleExpiresAt", deadline.UtcDateTime));
        await data.Rows("evidenceUploadProbe").InsertOneAsync(new BsonDocument { { "_id", 1L }, { "value", 0 } });
        var selected = 0; var observed = 0;
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.Command.GetValue("find", BsonNull.Value) == "identitySessions" && command.Command.TryGetValue("projection", out var projection) &&
                    projection.AsBsonDocument.Contains("idleExpiresAt") && projection.AsBsonDocument.Contains("absoluteExpiresAt")) selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (selected != 0 && selected == command.RequestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) data.Clock.UtcNow = deadline;
            });
        };
        var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        using var tx = await client.StartSessionAsync(); tx.StartTransaction();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.NewService(client).WithCatalogImageUploadTargetAsync(db, tx, data.Maker,
            new(EvidenceTargetKind.CatalogFamily, created.Id.Value), 1, async (_, ct) =>
            {
                await db.GetCollection<BsonDocument>("evidenceUploadProbe").UpdateOneAsync(tx, new BsonDocument("_id", 1L), U.Inc("value", 1), cancellationToken: ct);
                return 1;
            }));
        Assert.Equal(1, observed); Assert.False(tx.IsInTransaction);
        Assert.Equal(0, (await data.RowAsync("evidenceUploadProbe", 1))["value"].AsInt32);
    }

    [Fact]
    public async Task Cancelled_request_cannot_prevent_independent_abort_of_caller_writes()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var created = await data.FamilyAsync();
        await data.Rows("evidenceUploadProbe").InsertOneAsync(new BsonDocument { { "_id", 1L }, { "value", 0 } });
        using var cancellation = new CancellationTokenSource();
        using var tx = await data.Client.StartSessionAsync(); tx.StartTransaction();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => data.Service.WithCatalogImageUploadTargetAsync(data.Database, tx, data.Maker,
            new(EvidenceTargetKind.CatalogFamily, created.Id.Value), null, async (_, ct) =>
            {
                await data.Rows("evidenceUploadProbe").UpdateOneAsync(tx, new BsonDocument("_id", 1L), U.Inc("value", 1), cancellationToken: ct);
                cancellation.Cancel(); ct.ThrowIfCancellationRequested(); return 1;
            }, cancellation.Token));
        Assert.False(tx.IsInTransaction);
        Assert.Equal(0, (await data.RowAsync("evidenceUploadProbe", 1))["value"].AsInt32);
    }
}
