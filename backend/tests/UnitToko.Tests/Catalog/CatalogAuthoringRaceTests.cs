using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Catalog;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Catalog;

public sealed partial class CatalogAuthoringServiceTests
{
    [Theory]
    [InlineData("audit", "grant")]
    [InlineData("outbox", "membership")]
    [InlineData("session", "session")]
    [InlineData("session", "grant")]
    public async Task Authority_expiry_after_successful_final_await_rolls_back_master_audit_receipt_and_fences(string stage, string expiry)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var family = await data.FamilyAsync();
        var deadline = data.Clock.UtcNow.AddSeconds(1);
        if (expiry == "grant") await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Maker.UserId), U.Set("expiresAt", deadline.UtcDateTime));
        if (expiry == "membership") await data.Rows("memberships").UpdateOneAsync(new BsonDocument("_id", data.Maker.UserId), U.Set("expiresAt", deadline.UtcDateTime));
        if (expiry == "session") await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Maker.Session!.Id), U.Set("idleExpiresAt", deadline.UtcDateTime));
        var before = await data.StateAsync();
        var requestId = 0; var observed = 0;
        var settings = Settings();
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                var insert = command.Command.GetValue("insert", BsonNull.Value);
                if (stage == "audit" && insert == "auditEvents" && command.Command["documents"].AsBsonArray.Any(row => row["kind"] == "catalog.command")) requestId = command.RequestId;
                if (stage == "outbox" && insert == "outboxEvents") requestId = command.RequestId;
                if (stage == "session" && command.Command.GetValue("find", BsonNull.Value) == "identitySessions" &&
                    command.Command.TryGetValue("projection", out var projection) && projection.AsBsonDocument.Contains("idleExpiresAt") &&
                    projection.AsBsonDocument.Contains("absoluteExpiresAt")) requestId = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (requestId != 0 && command.RequestId == requestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0)
                    data.Clock.UtcNow = deadline;
            });
        };
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.NewService(new MongoClient(settings))
            .CreateSkuAsync(data.Maker, new(family.Id, 1, "EXPIRING", "base", Definition()), Metadata("expiring")));
        Assert.Equal(1, Volatile.Read(ref observed)); Assert.Equal(before, await data.StateAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Buyer_projection_retries_real_snapshot_when_publication_or_definition_changes(bool definitionChanges)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync(); await data.AssortmentsAsync();
        await data.Service.SetPublicationsAsync(data.Maker, new(sku.Id, 1, [new(501)], [new(new(501), 1)]), Metadata("initial"));
        using var snapshotReady = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var selected = 0; var observed = 0; var reads = 0;
        var settings = Settings();
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.Command.GetValue("find", BsonNull.Value) == "skus" && Interlocked.Increment(ref reads) == 1)
                    selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (selected != 0 && command.RequestId == selected && Interlocked.CompareExchange(ref observed, 1, 0) == 0)
                {
                    snapshotReady.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Writer did not commit before buyer snapshot resumed.");
                }
            });
        };
        var pending = Task.Run(() => data.NewBuyer(new MongoClient(settings)).QueryAsync(data.Buyer, 0, 25, null));
        try
        {
            Assert.True(snapshotReady.Wait(TimeSpan.FromSeconds(15)));
            if (definitionChanges) await data.Service.ReviseDefinitionAsync(data.Maker, new(sku.Id, 2, Definition(label: "Karton baru")), Metadata("revise"));
            else await data.Service.SetPublicationsAsync(data.Maker,
                new(sku.Id, 2, [new(502)], [new(new(501), 2), new(new(502), 1)]), Metadata("move"));
        }
        finally { release.Set(); }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, Volatile.Read(ref observed)); Assert.True(Volatile.Read(ref reads) >= 2);
        if (definitionChanges) Assert.Equal("Karton baru", Assert.Single(result.Items).Definition.PackageLabel);
        else { Assert.Equal(0, result.Total); Assert.Empty(result.Items); }
    }
    [Fact]
    public async Task Actual_uncertain_commit_receipt_recovers_one_complete_catalog_identity_and_original_result()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var family = await data.FamilyAsync();
        var input = new CatalogCreateSku(family.Id, 1, "UNCERTAIN", "base", Definition());
        var settings = Settings(); settings.ApplicationName = "unittoko-catalog-" + Guid.NewGuid().ToString("N");
        var commits = 0;
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command =>
        {
            if (command.CommandName == "commitTransaction") Interlocked.Increment(ref commits);
        });
        var uncertain = data.NewService(new MongoClient(settings));
        CatalogMutationResult original;
        try
        {
            await fixture.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "configureFailPoint", "failCommand" }, { "mode", "alwaysOn" },
                { "data", new BsonDocument
                    {
                        { "appName", settings.ApplicationName }, { "failCommands", new BsonArray { "commitTransaction" } },
                        { "writeConcernError", new BsonDocument { { "code", 64 }, { "errmsg", "synthetic uncertain catalog commit reply" } } },
                        { "errorLabels", new BsonArray { "UnknownTransactionCommitResult" } }
                    }
                }
            });
            await Assert.ThrowsAsync<UncertainCommitException>(() => uncertain.CreateSkuAsync(data.Maker, input, Metadata("uncertain")));
            Assert.True(Volatile.Read(ref commits) >= MongoUnitOfWork.MaximumAttempts);
            original = Assert.IsType<CatalogMutationResult>(await data.Service.ReadOperationReceiptAsync(data.Maker, "catalog.sku.create", "uncertain", RecoveryAudit));
            Assert.Equal(1, await data.CountAsync("skus")); Assert.Equal(1, await data.CountAsync("variantDefinitions"));
            Assert.Equal(1, await data.CountAsync("catalogIdentifiers")); Assert.Equal(1, await data.CountAsync("catalogIdentifierHistory"));
        }
        finally { await fixture.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument { { "configureFailPoint", "failCommand" }, { "mode", "off" } }); }
        Assert.Equal(original, await uncertain.CreateSkuAsync(data.Maker, input, Metadata("uncertain")));
        Assert.Equal(1, await data.CountAsync("skus"));
        Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "catalog.sku.create")));
    }
    private static MongoClientSettings Settings()
    {
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10); return settings;
    }
}
