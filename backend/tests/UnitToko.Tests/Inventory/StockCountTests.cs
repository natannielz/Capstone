using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed partial class StockCountTests(MongoFixture fixture)
{
    [Theory]
    [InlineData(0, 12, 0L)]
    [InlineData(2, 12, 24L)]
    [InlineData(2, 24, 48L)]
    [InlineData(1000000, 1000000, 1000000000000L)]
    public async Task Counts_capture_exact_packages_without_creating_physical_stock(int packages, int contents, long baseUnits)
    {
        await using var data = await Setup(contents);
        var before = await Physical(data);
        var input = Input(data, packages);
        var saved = await Service(data).RecordCountAsync(data.WarehouseActor, input, Metadata("count"));
        Assert.Equal(packages, saved.Packages);
        Assert.Equal(baseUnits, saved.BaseUnits);
        Assert.Equal(contents, saved.Definition.BaseUnitsPerPackage);
        Assert.Equal("unavailable", saved.Baseline);
        Assert.Equal("single-observation", saved.Coverage);
        Assert.Equal("LOT unverified", saved.ObservedLotLabel);
        Assert.Equal(saved, await Service(data).RecoverAsync(data.WarehouseActor, "count"));
        Assert.Equal(before, await Physical(data));
        Assert.Equal(1, await data.CountAsync("stockCountObservations"));
        Assert.Equal(1, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "inventory.stock-count.recorded")));
    }

    [Fact]
    public async Task Exact_replay_is_inert_but_changed_body_conflicts()
    {
        await using var data = await Setup();
        var service = Service(data);
        var input = Input(data);
        var saved = await service.RecordCountAsync(data.WarehouseActor, input, Metadata("replay"));
        Assert.Equal(saved, await service.RecordCountAsync(data.WarehouseActor, input, Metadata("replay") with { CorrelationId = "another-correlation" }));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.RecordCountAsync(data.WarehouseActor, input with { Packages = 7 }, Metadata("replay")));
        Assert.Equal(1, await data.CountAsync("stockCountObservations"));
    }

    [Fact]
    public async Task Concurrent_same_key_creates_one_observation_and_audit()
    {
        await using var data = await Setup();
        var input = Input(data);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Service(data).RecordCountAsync(data.WarehouseActor, input, Metadata("race"))));
        Assert.Single(results.Select(row => row.Id).Distinct());
        Assert.Equal(1, await data.CountAsync("stockCountObservations"));
        Assert.Equal(1, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "inventory.stock-count.recorded")));
    }

    [Fact]
    public async Task Historical_recovery_does_not_require_current_or_active_sku()
    {
        await using var data = await Setup();
        var input = Input(data);
        var service = Service(data);
        var saved = await service.RecordCountAsync(data.WarehouseActor, input, Metadata("history"));
        await data.Service.ReviseDefinitionAsync(data.Maker, new(new(input.SkuId), 1, Definition(label: "Corrected label")), Metadata("revise"));
        await data.Service.RetireSkuAsync(data.Maker, new(new(input.SkuId), 2), Metadata("retire"));
        Assert.Equal(saved, await service.RecoverAsync(data.WarehouseActor, "history"));
        Assert.Equal(saved, await service.RecordCountAsync(data.WarehouseActor, input, Metadata("history")));
        Assert.Equal(saved, await service.ReadAsync(data.WarehouseActor, saved.Id));
        Assert.Equal("Dus", saved.Definition.PackageLabel);
    }

    [Fact]
    public async Task Count_only_actor_cannot_use_stock_read()
    {
        await using var data = await Setup();
        await PermissionsFor(data, Permissions.StockCount);
        var service = Service(data);
        var saved = await service.RecordCountAsync(data.WarehouseActor, Input(data), Metadata("only-count"));
        Assert.Equal(saved, await service.RecoverAsync(data.WarehouseActor, "only-count"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(data.WarehouseActor, saved.Id));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ListAsync(data.WarehouseActor, Warehouse, 0, 10));
    }

    [Fact]
    public async Task Read_scope_filters_list_and_direct_id_without_disclosing_other_location()
    {
        await using var data = await Setup();
        var service = Service(data);
        var saved = await service.RecordCountAsync(data.WarehouseActor, Input(data), Metadata("read"));
        Assert.Equal(saved, Assert.Single((await service.ListAsync(data.WarehouseActor, Warehouse, 0, 10)).Items));
        await data.Base.AddGrantAsync(data.Other, 90001, OtherOwner, [Permissions.StockRead, Permissions.StockCount]);
        Assert.Null(await service.ReadAsync(data.Other, saved.Id));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ListAsync(data.Other, Warehouse, 0, 10));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecordCountAsync(data.Other, Input(data), Metadata("cross")));
        Assert.Null(await service.RecoverAsync(data.Other, "read"));
    }

    [Fact]
    public async Task Rebound_provider_identity_cannot_recover_original_account_receipt()
    {
        await using var data = await Setup();
        var service = Service(data);
        await service.RecordCountAsync(data.WarehouseActor, Input(data), Metadata("bound"));
        var rebound = data.WarehouseActor with { ProviderSubject = "replacement-subject", Session = null };
        await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", rebound.UserId), Builders<BsonDocument>.Update.Set("subject", rebound.ProviderSubject));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverAsync(rebound, "bound"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecordCountAsync(rebound, Input(data), Metadata("bound")));
    }

    [Fact]
    public async Task Audit_failure_rolls_back_observation_and_receipt()
    {
        await using var data = await Setup();
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", new BsonDocument("kind", new BsonDocument("$ne", "inventory.stock-count.recorded")) } });
        await Assert.ThrowsAnyAsync<Exception>(() => Service(data).RecordCountAsync(data.WarehouseActor, Input(data), Metadata("rollback")));
        Assert.Equal(0, await data.CountAsync("stockCountObservations"));
        Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "inventory.stock-count.record")));
    }

    [Fact]
    public async Task Tampered_audit_or_receipt_never_yields_historical_success()
    {
        await using var data = await Setup();
        var service = Service(data);
        var saved = await service.RecordCountAsync(data.WarehouseActor, Input(data), Metadata("tamper"));
        await data.Rows("auditEvents").UpdateOneAsync(new BsonDocument("_id", saved.AuditId), Builders<BsonDocument>.Update.Set("reason", "forged"));
        await Assert.ThrowsAnyAsync<Exception>(() => service.RecoverAsync(data.WarehouseActor, "tamper"));
        await Assert.ThrowsAnyAsync<Exception>(() => service.ReadAsync(data.WarehouseActor, saved.Id));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1000001)]
    public async Task Invalid_count_is_rejected_before_effect(int packages)
    {
        await using var data = await Setup();
        await Assert.ThrowsAsync<DomainValidationException>(() => Service(data).RecordCountAsync(data.WarehouseActor, Input(data, packages), Metadata("invalid")));
        Assert.Equal(0, await data.CountAsync("stockCountObservations"));
    }

    [Fact]
    public async Task Unknown_definition_and_stale_head_are_not_observations()
    {
        await using var data = await Setup();
        var input = Input(data);
        var service = Service(data);
        await Assert.ThrowsAnyAsync<Exception>(() => service.RecordCountAsync(data.WarehouseActor, input with { ExpectedDefinitionId = 999999 }, Metadata("bad-definition")));
        await Assert.ThrowsAnyAsync<Exception>(() => service.RecordCountAsync(data.WarehouseActor, input with { ExpectedSkuRevision = 2 }, Metadata("stale")));
        Assert.Equal(0, await data.CountAsync("stockCountObservations"));
    }

    [Fact]
    public async Task Authorized_warehouse_cannot_count_a_sku_owned_by_another_shop()
    {
        await using var data = await Setup();
        var foreignFamily = await data.FamilyAsync(data.Other, OtherOwner, "FOREIGN");
        var foreign = await data.Service.CreateSkuAsync(data.Other,
            new(foreignFamily.Id, foreignFamily.Version, "FOREIGN-12", "foreign-base", Definition()), Metadata("foreign-sku"));
        var input = new StockCountInput(Warehouse, foreign.Id.Value, 1, foreign.CurrentDefinitionId!.Value.Value, 1);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service(data).RecordCountAsync(data.WarehouseActor, input, Metadata("wrong-owner")));
        Assert.Equal(0, await data.CountAsync("stockCountObservations"));
        Assert.Equal(0, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "inventory.stock-count.recorded")));
        Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "inventory.stock-count.record")));
    }

    [Theory]
    [InlineData("schema", "recover")] [InlineData("number", "recover")] [InlineData("fence", "recover")]
    [InlineData("schema", "read")] [InlineData("number", "read")] [InlineData("fence", "read")]
    [InlineData("schema", "list")] [InlineData("number", "list")] [InlineData("fence", "list")]
    public async Task Malformed_identity_never_authorizes_historical_count_access(string defect, string operation)
    {
        await using var data = await Setup();
        var service = Service(data);
        var saved = await service.RecordCountAsync(data.WarehouseActor, Input(data), Metadata("strict-identity"));
        var update = defect switch
        {
            "schema" => Builders<BsonDocument>.Update.Set("schemaVersion", 2),
            "number" => Builders<BsonDocument>.Update.Set("grantVersion", new BsonInt32(1)),
            _ => Builders<BsonDocument>.Update.Set("fence", long.MaxValue)
        };
        await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", data.WarehouseActor.UserId), update,
            new UpdateOptions { BypassDocumentValidation = true });
        Task Invoke() => operation switch
        {
            "recover" => service.RecoverAsync(data.WarehouseActor, "strict-identity"),
            "read" => service.ReadAsync(data.WarehouseActor, saved.Id),
            _ => service.ListAsync(data.WarehouseActor, Warehouse, 0, 10)
        };
        await Assert.ThrowsAsync<AuthorizationDeniedException>(Invoke);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Missing_or_rewritten_referenced_definition_denies_historical_success(bool rewrite)
    {
        await using var data = await Setup();
        var service = Service(data);
        var input = Input(data);
        var saved = await service.RecordCountAsync(data.WarehouseActor, input, Metadata("definition-integrity"));
        if (rewrite)
            await data.Rows("variantDefinitions").UpdateOneAsync(new BsonDocument("_id", input.ExpectedDefinitionId),
                Builders<BsonDocument>.Update.Set("packageLabel", "forged"), new UpdateOptions { BypassDocumentValidation = true });
        else await data.Rows("variantDefinitions").DeleteOneAsync(new BsonDocument("_id", input.ExpectedDefinitionId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverAsync(data.WarehouseActor, "definition-integrity"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(data.WarehouseActor, saved.Id));
    }
    private readonly Dictionary<CatalogAuthoringTestData, (long Sku, long Definition)> created = [];
    private async Task<CatalogAuthoringTestData> Setup(int contents = 12)
    {
        var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync(contents);
        await StockCountTestMigration.ApplyAsync(data.Database, data.Clock);
        created[data] = (sku.Id.Value, sku.CurrentDefinitionId!.Value.Value);
        await PermissionsFor(data, Permissions.StockCount, Permissions.StockRead);
        return data;
    }
    private StockCountInput Input(CatalogAuthoringTestData data, int packages = 2) =>
        new(Warehouse, created[data].Sku, 1, created[data].Definition, packages, "LOT unverified");
    private static Task PermissionsFor(CatalogAuthoringTestData data, params string[] permissions) =>
        data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.WarehouseActor.UserId),
            Builders<BsonDocument>.Update.Set("permissions", new BsonArray(permissions)));
    private static StockCountService Service(CatalogAuthoringTestData data) => new(data.Database, new(data.Client), new(data.Database), data.Clock);
    private static async Task<BsonDocument> Physical(CatalogAuthoringTestData data)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { "lots", "stockBalances", "movements", "reservations", "journalEntries" })
            result[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument()).ToListAsync());
        return result;
    }
}
