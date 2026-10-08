using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed class CatalogIdentifierServiceTests(MongoFixture fixture)
{
    [Fact]
    public async Task Original_reservations_survive_forward_upgrade_and_legitimate_gtin_correction_retirement_and_recovery_keep_identity()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var original = await data.SkuAsync(); var other = await data.SkuAsync(code: "OTHER-12", categoryCode: "OTHER");
        var counter = await data.ActorAsync(7000, Warehouse, Permissions.StockCount, Permissions.StockRead);
        var counts = new StockCountService(data.Database, new(data.Client), new(data.Database), data.Clock);
        var count = await counts.RecordCountAsync(counter, new StockCountInput(Warehouse, original.Id.Value, 1,
            original.CurrentDefinitionId!.Value.Value, 4), Metadata("count-before-correction"));
        var oldRows = await Registry(data); var originalSku = await data.RowAsync("skus", original.Id.Value);
        await Upgrade(data); Assert.Equal(oldRows, await Registry(data));
        var service = data.NewService(); var initial = await service.IdentifiersAsync(data.Maker, original.Id.Value, Audit);
        var primary = Assert.Single(initial.Identifiers); Assert.True(primary.IsPrimary); Assert.Null(primary.DefinitionId);
        var assign = new CatalogAssignIdentifier(original.Id, 1, original.CurrentDefinitionId!.Value, "gtin", null, "4006381333931", "Reviewed manufacturer packaging reference for this explicit test item");
        var assigned = await service.AssignIdentifierAsync(data.Maker, assign, Metadata("identifier-gtin")); Assert.Equal(2, assigned.SkuRevision); Assert.Equal(1, assigned.IdentifierRevision);
        Assert.Equal(assigned, await data.NewService().ReadIdentifierReceiptAsync(data.Maker, "catalog.identifier.assign", "identifier-gtin", RecoveryAudit));
        Assert.Equal("catalog_identifier_conflict", (await Assert.ThrowsAsync<PersistenceConflictException>(() => service.AssignIdentifierAsync(data.Maker,
            assign with { SkuId = other.Id, DefinitionId = other.CurrentDefinitionId!.Value, RawValue = "04006381333931" }, Metadata("canonical-duplicate")))).Code);
        var retired = await service.RetireIdentifierAsync(data.Maker, new(original.Id, 2, assigned.IdentifierId, 1, "Supplier confirmed that this label is retired"), Metadata("retire-gtin"));
        Assert.Equal(3, retired.SkuRevision); Assert.Equal(2, retired.IdentifierRevision);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.AssignIdentifierAsync(data.Maker,
            assign with { SkuId = other.Id, DefinitionId = other.CurrentDefinitionId!.Value }, Metadata("retired-duplicate")));
        var correction = new CatalogCorrectSkuCode(original.Id, 3, primary.Id, 1, "PENCIL-CORRECTED", "Reviewed spelling correction; unchanged physical package identity");
        var corrected = await service.CorrectSkuCodeAsync(data.Maker, correction, Metadata("correct-primary"));
        Assert.NotEqual(primary.Id, corrected.IdentifierId); Assert.Equal(1, corrected.IdentifierRevision); Assert.Equal(4, corrected.SkuRevision);
        var current = await service.IdentifiersAsync(data.Maker, original.Id.Value, Audit);
        Assert.Equal("PENCIL-12", current.Catalog.Sku.Code); Assert.Equal(originalSku["publicId"].AsString, current.Catalog.Sku.PublicId.ToString("D"));
        Assert.Equal(original.CurrentDefinitionId, current.Catalog.Sku.CurrentDefinitionId);
        var alias = current.Identifiers.Single(item => item.Id == primary.Id); Assert.False(alias.IsPrimary); Assert.Null(alias.RetiredAt); Assert.Equal(2, alias.Revision);
        Assert.Equal(corrected.IdentifierId, current.Identifiers.Single(item => item.IsPrimary).Id);
        Assert.Equal("PENCIL-CORRECTED", current.Identifiers.Single(item => item.IsPrimary).CanonicalValue);
        Assert.Equal(count, await counts.RecoverAsync(counter, "count-before-correction"));
        var freshSku = await service.SkuAsync(data.Maker, original.Id.Value, Audit); Assert.Equal("PENCIL-12", freshSku.Sku.Code);
        async Task<ScanResolution> Scan(string raw)
        {
            var request = new ScanRequest("stocktake", "sku", raw, null, null, Warehouse, null, null, null, null, null);
            return ScanRules.Resolve(request, await service.StocktakeScanChoicesAsync(counter, request, "synthetic-identifier-scan", default));
        }
        var oldScan = Assert.Single((await Scan("PENCIL-12")).Candidates); var newScan = Assert.Single((await Scan("PENCIL-CORRECTED")).Candidates);
        Assert.Equal(original.Id, oldScan.SkuId); Assert.Equal(original.Id, newScan.SkuId);
        Assert.Equal(original.CurrentDefinitionId, newScan.DefinitionId); Assert.Equal("PENCIL-CORRECTED", newScan.CurrentPrimaryCode);
        Assert.Equal(primary.Id, oldScan.Selection!.Identifier!.IdentifierId); Assert.Equal(corrected.IdentifierId, newScan.Selection!.Identifier!.IdentifierId);
        Assert.Equal("4006381333931", current.Identifiers.Single(item => item.Id == assigned.IdentifierId).RawValue);
        await service.RetireIdentifierAsync(data.Maker, new(original.Id, 4, alias.Id, 2, "Retain the old spelling only for historical traceability"), Metadata("retire-alias"));
        Assert.Equal("unknown", (await Scan("PENCIL-12")).Kind); Assert.Equal("match", (await Scan("PENCIL-CORRECTED")).Kind);
        Assert.Equal(count, await counts.RecoverAsync(counter, "count-before-correction"));
        Assert.Equal(corrected, await data.NewService().CorrectSkuCodeAsync(data.Maker, correction, Metadata("correct-primary")));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.CorrectSkuCodeAsync(data.Maker,
            correction with { NewCode = "OTHER-CORRECTION" }, Metadata("correct-primary")));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.CreateSkuAsync(data.Maker,
            new(original.FamilyId!.Value, 1, "PENCIL-12", "another-physical-identity", Definition()), Metadata("cannot-recycle-original")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.IdentifiersAsync(data.Other, original.Id.Value, Audit));
        var row = await data.RowAsync(CatalogIdentifierRecords.Collection, assigned.IdentifierId.Value);
        await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("_id", row["auditId"]));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.IdentifiersAsync(data.Maker, original.Id.Value, Audit));
    }
    [Fact]
    public async Task Concurrent_equivalent_gtin_assignments_reserve_only_one_sku()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var first = await data.SkuAsync(); var second = await data.SkuAsync(code: "SECOND", categoryCode: "SECOND"); await Upgrade(data);
        async Task<bool> Assign(CatalogMutationResult sku, string code, string key)
        {
            try { await data.NewService().AssignIdentifierAsync(data.Maker, new(sku.Id, 1, sku.CurrentDefinitionId!.Value, "gtin", null, code,
                "Explicit source package identifier checked by the operator"), Metadata(key)); return true; }
            catch (PersistenceConflictException) { return false; }
        }
        var results = await Task.WhenAll(Assign(first, "4006381333931", "concurrent-a"), Assign(second, "04006381333931", "concurrent-b"));
        Assert.Equal(1, results.Count(result => result));
        Assert.Equal(1, await data.Rows(CatalogIdentifierRecords.Collection).CountDocumentsAsync(new BsonDocument("kind", "gtin")));
        Assert.Equal(2, await data.Rows(CatalogIdentifierRecords.Collection).CountDocumentsAsync(new BsonDocument("isPrimary", true)));
    }
    [Fact]
    public async Task Interrupted_primary_index_transition_resumes_without_changing_legacy_reservation_bytes()
    {
        await using var data = await Legacy(); await data.SkuAsync();
        var before = await Registry(data); var gate = new MigrationGate(data.Database, data.Clock, 110);
        var lease = await gate.AcquireAsync(110, default); Assert.NotNull(lease);
        foreach (var name in new[] { CatalogIdentifierRecords.Collection, CatalogIdentifierRecords.History })
            await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", CatalogIdentifierSchemas.Validator(name) },
                { "validationLevel", "strict" }, { "validationAction", "error" } });
        await data.Rows(CatalogIdentifierRecords.Collection).Indexes.DropOneAsync("ux_sku_primary"); await lease.ReleaseAsync();
        await Upgrade(data); Assert.Equal(before, await Registry(data));
        await CatalogIdentifierRecords.RequireStorageAsync(data.Database, default);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inconsistent_history_or_unreviewed_index_aborts_before_any_registry_metadata_changes(bool extraIndex)
    {
        await using var data = await Legacy(); await data.SkuAsync();
        if (extraIndex) await data.Rows(CatalogIdentifierRecords.Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument("rawValue", 1), new CreateIndexOptions { Name = "unreviewed_index" }));
        else await data.Rows(CatalogIdentifierRecords.History).DeleteOneAsync(new BsonDocument());
        var before = await MetadataSnapshot(data); var rows = await Registry(data);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Upgrade(data));
        Assert.Equal(before, await MetadataSnapshot(data)); Assert.Equal(rows, await Registry(data));
    }
    private static async Task Upgrade(CatalogAuthoringTestData data)
    {
        if (await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 110 }, { "state", "applied" } }).AnyAsync()) return;
        var lease = await new MigrationGate(data.Database, data.Clock, 110).AcquireAsync(110, default); if (lease is null) return;
        try { await new CatalogIdentifierMigration(data.Database).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }
    private async Task<CatalogAuthoringTestData> Legacy() => await CatalogAuthoringTestData.AttachAsync(
        await StoreTestData.CreateWithMigrationTargetAsync(fixture, 109), fixture.Client);
    private static async Task<BsonDocument> Registry(CatalogAuthoringTestData data) => new()
    {
        { "identifiers", new BsonArray(await data.Rows(CatalogIdentifierRecords.Collection).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) },
        { "history", new BsonArray(await data.Rows(CatalogIdentifierRecords.History).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) }
    };
    private static async Task<BsonDocument> MetadataSnapshot(CatalogAuthoringTestData data)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { CatalogIdentifierRecords.Collection, CatalogIdentifierRecords.History })
        {
            result[name + ":definition"] = await AccountContactStorage.DefinitionAsync(data.Database, name, default) ?? throw new InvalidOperationException("Fixture collection absent");
            result[name + ":indexes"] = new BsonArray(await (await data.Rows(name).Indexes.ListAsync()).ToListAsync());
        }
        return result;
    }
}
