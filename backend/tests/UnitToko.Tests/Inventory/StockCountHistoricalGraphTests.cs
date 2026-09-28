using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Tests.Catalog;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockCountTests
{
    public static TheoryData<string> HistoricalIdentityDefects => new()
    {
        "snapshot-owner", "snapshot-code", "snapshot-physical-key", "snapshot-family",
        "missing-sku", "missing-family", "missing-category",
        "owner-sku", "owner-family", "owner-category",
        "future-sku-revision", "future-family-revision", "future-category-revision",
        "malformed-sku", "malformed-family", "malformed-category"
    };

    [Theory]
    [MemberData(nameof(HistoricalIdentityDefects))]
    public async Task Historical_graph_rejects_forged_or_orphan_catalog_identity(string defect)
    {
        await using var data = await Setup();
        var service = Service(data);
        var saved = await service.RecordCountAsync(data.WarehouseActor, Input(data), Metadata("historical-identity"));
        await CorruptHistoricalIdentity(data, saved, defect);

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverAsync(data.WarehouseActor, "historical-identity"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(data.WarehouseActor, saved.Id));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ListAsync(data.WarehouseActor, Warehouse, 0, 10));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecordCountAsync(data.WarehouseActor, Input(data), Metadata("historical-identity")));
    }

    [Theory]
    [MemberData(nameof(HistoricalIdentityDefects))]
    public async Task Migration_rejects_forged_or_orphan_catalog_identity_before_ddl(string defect)
    {
        await using var data = await Setup();
        var saved = await Service(data).RecordCountAsync(data.WarehouseActor, Input(data), Metadata("historical-identity"));
        await CorruptHistoricalIdentity(data, saved, defect);
        // A successful preflight would recreate this required index. Failure must leave it absent.
        await data.Rows("auditEvents").Indexes.DropOneAsync("ux_stock_count_audit_observation");
        var before = await HistoricalTargetState(data);

        await Assert.ThrowsAsync<InvalidOperationException>(() => StockCountTestMigration.ApplyAsync(data.Database, data.Clock));
        Assert.Equal(before, await HistoricalTargetState(data));
    }

    [Fact]
    public async Task Historical_graph_allows_same_owner_category_reclassification_and_retired_sku()
    {
        await using var data = await Setup();
        var service = Service(data);
        var input = Input(data);
        var saved = await service.RecordCountAsync(data.WarehouseActor, input, Metadata("reclassified-history"));
        var replacementCategory = await data.CategoryAsync(code: "OFFICE");
        await data.Service.EditFamilyAsync(data.Maker,
            new(new(saved.Definition.FamilyId), saved.Definition.FamilyRevision, replacementCategory.Id,
                "Reclassified family", "Current description", []), Metadata("reclassify"));
        await data.Service.SaveCategoryAsync(data.Maker,
            new(Shop, new(saved.Definition.CategoryId), saved.Definition.CategoryRevision, "ATK", "Historical category renamed"),
            Metadata("rename-old-category"));
        await data.Service.ReviseDefinitionAsync(data.Maker,
            new(new(input.SkuId), input.ExpectedSkuRevision, Definition(12, "Later package")), Metadata("later-definition"));
        await data.Service.RetireSkuAsync(data.Maker, new(new(input.SkuId), 2), Metadata("later-retirement"));

        Assert.Equal(saved, await service.RecoverAsync(data.WarehouseActor, "reclassified-history"));
        Assert.Equal(saved, await service.ReadAsync(data.WarehouseActor, saved.Id));
        Assert.Equal(saved, Assert.Single((await service.ListAsync(data.WarehouseActor, Warehouse, 0, 10)).Items));
        Assert.Equal(saved, await service.RecordCountAsync(data.WarehouseActor, input, Metadata("reclassified-history")));
        await StockCountTestMigration.ApplyAsync(data.Database, data.Clock);
        Assert.Equal(saved, await service.RecoverAsync(data.WarehouseActor, "reclassified-history"));
    }

    [Theory]
    [InlineData("packages")]
    [InlineData("actorId")]
    public async Task Migration_rejects_extra_unique_observation_constraints_before_ddl(string field)
    {
        await using var data = await Setup();
        await Service(data).RecordCountAsync(data.WarehouseActor, Input(data), Metadata("extra-unique"));
        await data.Rows(StockCountBson.Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument(field, 1), new CreateIndexOptions { Name = "ux_unsupported_" + field, Unique = true }));
        await data.Rows("auditEvents").Indexes.DropOneAsync("ux_stock_count_audit_observation");
        var before = await HistoricalTargetState(data);

        await Assert.ThrowsAsync<InvalidOperationException>(() => StockCountTestMigration.ApplyAsync(data.Database, data.Clock));
        Assert.Equal(before, await HistoricalTargetState(data));
    }
    private static async Task CorruptHistoricalIdentity(CatalogAuthoringTestData data, StockCountObservation saved, string defect)
    {
        var snapshot = saved.Definition;
        if (defect.StartsWith("snapshot-", StringComparison.Ordinal) || defect.StartsWith("future-", StringComparison.Ordinal))
        {
            snapshot = defect switch
            {
                "snapshot-owner" => snapshot with { Owner = Warehouse },
                "snapshot-code" => snapshot with { SkuCode = "FORGED-CODE" },
                "snapshot-physical-key" => snapshot with { PhysicalItemKey = "forged-physical-item" },
                "snapshot-family" => snapshot with { FamilyId = (await data.FamilyAsync(code: "UNRELATED")).Id.Value },
                "future-sku-revision" => snapshot with { SkuRevision = snapshot.SkuRevision + 1 },
                "future-family-revision" => snapshot with { FamilyRevision = snapshot.FamilyRevision + 1 },
                "future-category-revision" => snapshot with { CategoryRevision = snapshot.CategoryRevision + 1 },
                _ => throw new ArgumentOutOfRangeException(nameof(defect))
            };
            var original = await data.RowAsync(StockCountBson.Collection, saved.Id);
            var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument
            {
                { "actorId", saved.CountedBy }, { "operation", StockCountBson.Operation }, { "key", "historical-identity" }
            }).SingleAsync();
            var input = new StockCountInput(saved.Location, snapshot.SkuId, snapshot.SkuRevision, snapshot.DefinitionId,
                saved.Packages, saved.ObservedLotLabel);
            var forged = StockCountBson.Create(saved.Id, saved.AuditId, data.WarehouseActor, input, snapshot,
                new("historical-identity", saved.Reason, original["correlationId"].AsString), saved.RecordedAt);
            // Recompute every dependent hash/link: this isolates catalog identity from local tuple integrity.
            StockCountBson.Graph(forged, StockCountBson.Audit(forged), StockCountBson.Receipt(forged, receipt["_id"].AsObjectId));
            await data.Rows(StockCountBson.Collection).ReplaceOneAsync(new BsonDocument("_id", saved.Id), forged);
            await data.Rows("auditEvents").ReplaceOneAsync(new BsonDocument("_id", saved.AuditId), StockCountBson.Audit(forged));
            await data.Rows("idempotencyRecords").ReplaceOneAsync(new BsonDocument("_id", receipt["_id"]),
                StockCountBson.Receipt(forged, receipt["_id"].AsObjectId));
            return;
        }

        var (collection, id) = defect.Split('-')[1] switch
        {
            "sku" => ("skus", snapshot.SkuId),
            "family" => ("productFamilies", snapshot.FamilyId),
            "category" => ("catalogCategories", snapshot.CategoryId),
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        if (defect.StartsWith("missing-", StringComparison.Ordinal))
            await data.Rows(collection).DeleteOneAsync(new BsonDocument("_id", id));
        else
        {
            var update = defect.StartsWith("owner-", StringComparison.Ordinal)
                ? Builders<BsonDocument>.Update.Set("authoringOwner", StockCountBson.Scope(Warehouse))
                : Builders<BsonDocument>.Update.Set("schemaVersion", 2);
            await data.Rows(collection).UpdateOneAsync(new BsonDocument("_id", id), update,
                new UpdateOptions { BypassDocumentValidation = true });
        }
    }

    private static async Task<BsonDocument> HistoricalTargetState(CatalogAuthoringTestData data)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { StockCountBson.Collection, "auditEvents", "idempotencyRecords" })
        {
            var definition = await (await data.Database.ListCollectionsAsync(
                new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
            var indexes = await (await data.Rows(name).Indexes.ListAsync()).ToListAsync();
            result[name] = new BsonDocument
            {
                { "definition", definition },
                { "indexes", new BsonArray(indexes.OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) }
            };
        }
        result["counters"] = new BsonArray(await data.Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
}
