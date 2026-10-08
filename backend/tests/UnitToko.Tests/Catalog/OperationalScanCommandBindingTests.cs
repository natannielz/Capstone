using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;
using B = UnitToko.Platform.Catalog.OperationalScanCommandBindings;
using C = UnitToko.Platform.Inventory.StockCountBson;

namespace UnitToko.Tests.Catalog;

/// <summary>Focused sidecar boundaries on actual isolated Mongo transactions and genuine catalog
/// history. The observation/receipt rows below are explicit low-level fixture records, not API acceptance.</summary>
[Collection("Mongo")]
public sealed class OperationalScanCommandBindingTests(MongoFixture fixture)
{
    private static readonly OperationalScanSelection Identifier = new(new(new(1), 1, new(2), new(3), 1, "skuCode", "internal", "EXACT-12"), null);
    [Fact]
    public void Null_and_empty_are_one_manual_mode_but_identifier_revision_and_manual_scan_switch_change_digest()
    {
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var manual = B.Normalize(C.Operation, null); Assert.Equal(manual, B.Normalize(C.Operation, []));
        var selected = B.Normalize(C.Operation, [Identifier]);
        Assert.NotEqual(B.SelectionHash(hash, manual), B.SelectionHash(hash, selected));
        Assert.NotEqual(B.SelectionHash(hash, selected), B.SelectionHash(hash, B.Normalize(C.Operation,
            [Identifier with { Identifier = Identifier.Identifier! with { IdentifierRevision = 2 } }])));
        Assert.NotEqual(B.SelectionHash(hash, selected), B.SelectionHash(new string('b', 64), selected));
        Assert.Throws<PersistenceConflictException>(() => B.Normalize(C.Operation, [Identifier, Identifier]));
        Assert.Throws<PersistenceConflictException>(() => B.Normalize(C.Operation, Enumerable.Repeat(Identifier, 101).ToArray()));
        Assert.Throws<PersistenceConflictException>(() => B.Normalize(C.Operation, [new(null, null)]));
    }
    [Fact]
    public void Approved_document_codes_have_their_own_receiving_only_branch_and_exact_source_tuple()
    {
        var source = new ApprovedSourceScanSelection(new(10), 1, new(20), 1, 2, new(30), new(40), 1, "omi", "ACCOUNT-A", "00123");
        var choice = new OperationalScanSelection(null, source);
        var first = B.Normalize("procurement.stockreceipt.receive", [choice]);
        Assert.NotEqual(first, B.Normalize("procurement.stockreceipt.receive", [choice with { Source = source with { SourceAccount = "ACCOUNT-B" } }]));
        Assert.NotEqual(first, B.Normalize("procurement.stockreceipt.receive", [choice with { Source = source with { SourceCode = "123" } }]));
        foreach (var operation in new[] { C.Operation, "fulfillment.preparation.save", "fulfillment.shipment.dispatch" })
            Assert.Throws<PersistenceConflictException>(() => B.Normalize(operation, [choice]));
        Assert.Throws<PersistenceConflictException>(() => B.Normalize("procurement.stockreceipt.receive", [choice, choice]));
        Assert.Throws<PersistenceConflictException>(() => B.Normalize("procurement.stockreceipt.receive", [choice with { Source = source with { InspectionRevision = 2 } }]));
        Assert.Throws<PersistenceConflictException>(() => B.Normalize("procurement.stockreceipt.receive", [new(Identifier.Identifier, source)]));
    }
    [Fact]
    public async Task Scanned_original_is_exactly_once_changed_replays_refuse_and_retired_mapping_recovers_historical_evidence()
    {
        await using var data = await Setup(); await Upgrade(data);
        var made = await data.SkuAsync(); var assignment = await data.Service.AssignIdentifierAsync(data.Maker,
            new(made.Id, 1, made.CurrentDefinitionId!.Value, "gtin", null, "4006381333931", "Explicit synthetic package provenance"), Metadata("binding-gtin"));
        var actor = await data.ActorAsync(4000, Warehouse, Permissions.StockCount);
        var row = await Observation(data, actor, made.Id.Value, "scanned-original"); var receipt = C.Receipt(row, ObjectId.GenerateNewId());
        var revised = await data.Service.ReviseDefinitionAsync(data.Maker,
            new(made.Id, 2, Definition(label: "Karton")), Metadata("binding-label-revision"));
        var identifier = await data.RowAsync(CatalogIdentifierRecords.Collection, assignment.IdentifierId.Value);
        var sku = await data.RowAsync("skus", made.Id.Value); var definition = await data.RowAsync("variantDefinitions", made.CurrentDefinitionId.Value.Value);
        Assert.NotEqual(definition["_id"], sku["currentDefinitionId"]);
        var selection = new OperationalScanSelection(new(assignment.IdentifierId, 1, made.Id, made.CurrentDefinitionId.Value, 1, "gtin",
            identifier["namespace"].AsString, "04006381333931"), null);
        var proof = new OperationalScanEvidence(selection, identifier, sku, definition, null);
        var helper = new B(data.Database, new(data.Database), data.Clock); await helper.RequireStorageAsync(default);
        var unit = new MongoUnitOfWork(data.Client);
        await unit.ExecuteAsync(async (tx, ct) => {
            var attempt = await helper.BeginAsync(tx, actor, C.Operation, "scanned-original", row["bodyHash"].AsString, [selection], ct); Assert.False(attempt.IsReplay);
            await Insert(data, tx, row, receipt, ct); await helper.CompleteAsync(tx, attempt, receipt, C.Audit(row), [proof], ct); return true;
        });
        await unit.ExecuteAsync(async (tx, ct) => {
            var attempt = await helper.BeginAsync(tx, actor, C.Operation, "scanned-original", row["bodyHash"].AsString, [selection], ct); Assert.True(attempt.IsReplay);
            await helper.CompleteAsync(tx, attempt, receipt, C.Audit(row), [], ct); return true;
        });
        var capturedCurrent = await data.RowAsync("variantDefinitions", revised.CurrentDefinitionId!.Value.Value);
        await data.Rows("variantDefinitions").UpdateOneAsync(new BsonDocument("_id", capturedCurrent["_id"]),
            Builders<BsonDocument>.Update.Set("baseUnitsPerPackage", 13));
        var material = await Assert.ThrowsAsync<PersistenceConflictException>(() => unit.ExecuteAsync(async (tx, ct) => {
            await helper.VerifyRecoveryAsync(tx, actor.UserId, C.Operation, "scanned-original", receipt, ct); return true;
        })); Assert.Equal("operational_scan_binding_unavailable", material.Code);
        await data.Rows("variantDefinitions").ReplaceOneAsync(new BsonDocument("_id", capturedCurrent["_id"]), capturedCurrent);
        foreach (IReadOnlyList<OperationalScanSelection>? changed in new IReadOnlyList<OperationalScanSelection>?[] {
            null, [], [selection with { Identifier = selection.Identifier! with { IdentifierRevision = 2 } }],
            [selection with { Identifier = selection.Identifier! with { CanonicalValue = "00012345600012" } }] }) {
            var failure = await Assert.ThrowsAsync<PersistenceConflictException>(() => unit.ExecuteAsync(async (tx, ct) => {
                _ = await helper.BeginAsync(tx, actor, C.Operation, "scanned-original", row["bodyHash"].AsString, changed, ct); return true;
            })); Assert.Equal("operational_scan_selection_changed", failure.Code);
        }
        var before = await Business(data);
        await data.Service.RetireIdentifierAsync(data.Maker, new(made.Id, 3, assignment.IdentifierId, 1,
            "Retire this supplied label without erasing historical proof"), Metadata("binding-retire"));
        Assert.NotEqual(BsonNull.Value, (await data.RowAsync(CatalogIdentifierRecords.Collection, assignment.IdentifierId.Value))["retiredAt"]);
        await unit.ExecuteAsync(async (tx, ct) => { await helper.VerifyRecoveryAsync(tx, actor.UserId, C.Operation, "scanned-original", receipt, ct); return true; });
        Assert.Equal(before, await Business(data)); Assert.Equal(1L, await data.CountAsync(B.Collection));
        var originalHistory = await data.Rows(CatalogIdentifierRecords.History).Find(new BsonDocument { { "identifierId", assignment.IdentifierId.Value }, { "assignmentVersion", 1L } }).SingleAsync();
        await data.Rows(CatalogIdentifierRecords.History).DeleteOneAsync(new BsonDocument("_id", originalHistory["_id"]));
        var missing = await Assert.ThrowsAsync<PersistenceConflictException>(() => unit.ExecuteAsync(async (tx, ct) => {
            await helper.VerifyRecoveryAsync(tx, actor.UserId, C.Operation, "scanned-original", receipt, ct); return true;
        })); Assert.Equal("operational_scan_binding_unavailable", missing.Code);
    }
    [Fact]
    public async Task Missing_corrupt_or_orphan_pairs_and_audits_refuse_without_rewriting_the_original_business_receipt()
    {
        await using var data = await Setup(); await Upgrade(data); var made = await data.SkuAsync();
        var actor = await data.ActorAsync(4000, Warehouse, Permissions.StockCount);
        var row = await Observation(data, actor, made.Id.Value, "manual-original"); var receipt = C.Receipt(row, ObjectId.GenerateNewId()); var audit = C.Audit(row);
        var helper = new B(data.Database, new(data.Database), data.Clock); var unit = new MongoUnitOfWork(data.Client);
        await unit.ExecuteAsync(async (tx, ct) => {
            var attempt = await helper.BeginAsync(tx, actor, C.Operation, "manual-original", row["bodyHash"].AsString, null, ct);
            await Insert(data, tx, row, receipt, ct); await helper.CompleteAsync(tx, attempt, receipt, audit, [], ct); return true;
        });
        var binding = await data.Rows(B.Collection).Find(new BsonDocument()).SingleAsync(); Assert.Equal("manual", binding["mode"]);
        var switched = await Assert.ThrowsAsync<PersistenceConflictException>(() => unit.ExecuteAsync(async (tx, ct) => {
            _ = await helper.BeginAsync(tx, actor, C.Operation, "manual-original", row["bodyHash"].AsString, [Identifier], ct); return true;
        })); Assert.Equal("operational_scan_selection_changed", switched.Code);
        var originalReceiptBytes = receipt.ToBson();
        async Task Refused(BsonDocument? supplied) {
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => unit.ExecuteAsync(async (tx, ct) => {
                await helper.VerifyRecoveryAsync(tx, actor.UserId, C.Operation, "manual-original", supplied, ct); return true;
            })); Assert.Equal("operational_scan_binding_unavailable", error.Code);
        }
        await data.Rows(B.Collection).DeleteOneAsync(new BsonDocument("_id", binding["_id"])); await Refused(receipt);
        await data.Rows(B.Collection).InsertOneAsync(binding);
        await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("_id", receipt["_id"])); await Refused(null);
        await data.Rows(B.Collection).DeleteOneAsync(new BsonDocument("_id", binding["_id"])); await Refused(null); // Remaining owner/audit is still an orphan.
        await data.Rows("idempotencyRecords").InsertOneAsync(receipt); await data.Rows(B.Collection).InsertOneAsync(binding);
        await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("_id", audit["_id"])); await Refused(receipt);
        await data.Rows("auditEvents").InsertOneAsync(audit);
        var forged = binding.DeepClone().AsBsonDocument; forged["businessHash"] = new string('b', 64);
        forged["selectionHash"] = B.SelectionHash(forged["businessHash"].AsString, []); forged["fingerprint"] = B.Fingerprint(forged);
        await data.Rows(B.Collection).ReplaceOneAsync(new BsonDocument("_id", binding["_id"]), forged); await Refused(receipt);
        await data.Rows(B.Collection).ReplaceOneAsync(new BsonDocument("_id", binding["_id"]), binding);
        var differentActor = await Assert.ThrowsAsync<PersistenceConflictException>(() => unit.ExecuteAsync(async (tx, ct) => {
            await helper.VerifyRecoveryAsync(tx, actor.UserId + 1, C.Operation, "manual-original", receipt, ct); return true;
        })); Assert.Equal("operational_scan_binding_unavailable", differentActor.Code);
        await unit.ExecuteAsync(async (tx, ct) => { await helper.VerifyRecoveryAsync(tx, actor.UserId, C.Operation, "manual-original", receipt, ct); return true; });
        Assert.Equal(originalReceiptBytes, (await data.Rows("idempotencyRecords").Find(new BsonDocument("_id", receipt["_id"])).SingleAsync()).ToBson());
        Assert.Equal(1L, await data.CountAsync(C.Collection)); Assert.Equal(1L, await data.CountAsync(B.Collection));
    }
    [Fact]
    public async Task Migration_refuses_orphans_before_DDL_then_backfills_only_legacy_manual_and_resumes_exactly()
    {
        await using var data = await Setup(); var made = await data.SkuAsync(); var actor = await data.ActorAsync(4000, Warehouse, Permissions.StockCount);
        var row = await Observation(data, actor, made.Id.Value, "pre-binding-original"); var receipt = C.Receipt(row, ObjectId.GenerateNewId());
        await new MongoUnitOfWork(data.Client).ExecuteAsync(async (tx, ct) => { await Insert(data, tx, row, receipt, ct); return true; });
        var before = await Business(data); var gate = new MigrationGate(data.Database, data.Clock, 111);
        var lease = await gate.AcquireAsync(111, default); Assert.NotNull(lease);
        try {
            await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("_id", receipt["_id"]));
            await Assert.ThrowsAsync<InvalidOperationException>(() => new OperationalScanBindingMigration(data.Database).ApplyAsync(lease, default));
            Assert.Null(await UnitToko.Platform.Identity.AccountContactStorage.DefinitionAsync(data.Database, B.Collection, default));
            await data.Rows("idempotencyRecords").InsertOneAsync(receipt);
            await new OperationalScanBindingMigration(data.Database).ApplyAsync(lease, default);
            var original = await data.Rows(B.Collection).Find(new BsonDocument()).SingleAsync();
            Assert.Equal("legacy-manual", original["mode"]); Assert.Empty(original["selections"].AsBsonArray); Assert.Empty(original["evidence"].AsBsonArray);
            await new OperationalScanBindingMigration(data.Database).ApplyAsync(lease, default);
            Assert.Equal(original, await data.Rows(B.Collection).Find(new BsonDocument()).SingleAsync());
            Assert.Equal(before, await Business(data)); await lease.CompleteAsync(default);
            var helper = new B(data.Database, new(data.Database), data.Clock);
            await new MongoUnitOfWork(data.Client).ExecuteAsync(async (tx, ct) => {
                var attempt = await helper.BeginAsync(tx, actor, C.Operation, "pre-binding-original", row["bodyHash"].AsString, null, ct);
                Assert.True(attempt.IsReplay); await helper.CompleteAsync(tx, attempt, receipt, C.Audit(row), [], ct); return true;
            });
        } catch { await lease.ReleaseAsync(); throw; }
    }
    private async Task<CatalogAuthoringTestData> Setup()
    {
        var source = await StoreTestData.CreateWithMigrationTargetAsync(fixture, 110);
        return await CatalogAuthoringTestData.AttachAsync(source, fixture.Client);
    }
    private static async Task Upgrade(CatalogAuthoringTestData data)
    {
        var lease = await new MigrationGate(data.Database, data.Clock, 111).AcquireAsync(111, default); if (lease is null) return;
        try { await new OperationalScanBindingMigration(data.Database).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }
    private static async Task<BsonDocument> Observation(CatalogAuthoringTestData data, ActorContext actor, long skuId, string key)
    {
        var sku = await data.RowAsync("skus", skuId); var definition = await data.RowAsync("variantDefinitions", sku["currentDefinitionId"].AsInt64);
        var family = await data.RowAsync("productFamilies", sku["familyId"].AsInt64); var category = await data.RowAsync("catalogCategories", family["categoryId"].AsInt64);
        var snapshot = new StockCountDefinitionSnapshot(Shop, skuId, sku["revision"].AsInt64, family["_id"].AsInt64, family["revision"].AsInt64,
            category["_id"].AsInt64, category["revision"].AsInt64, definition["_id"].AsInt64, definition["version"].AsInt64,
            sku["sku"].AsString, sku["physicalItemKey"].AsString, definition["packageLabel"].AsString, definition["packageKind"].AsString,
            definition["baseUnitCode"].AsString, definition["baseUnitsPerPackage"].AsInt32);
        var ids = new IdAllocator(data.Database); var id = await ids.NextAsync(C.Collection); var audit = await ids.NextAsync("auditEvents");
        return C.Create(id.Value, audit.Value, actor, new(Warehouse, skuId, snapshot.SkuRevision, snapshot.DefinitionId, 2), snapshot,
            Metadata(key, "Explicit helper boundary observation fixture"), data.Clock.UtcNow);
    }
    private static async Task Insert(CatalogAuthoringTestData data, IClientSessionHandle tx, BsonDocument row, BsonDocument receipt, CancellationToken ct)
    {
        await data.Rows(C.Collection).InsertOneAsync(tx, row, cancellationToken: ct);
        await data.Rows("auditEvents").InsertOneAsync(tx, C.Audit(row), cancellationToken: ct);
        await data.Rows("idempotencyRecords").InsertOneAsync(tx, receipt, cancellationToken: ct);
    }
    private static async Task<BsonDocument> Business(CatalogAuthoringTestData data) => new() {
        { "observations", new BsonArray(await data.Rows(C.Collection).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) },
        { "audits", new BsonArray(await data.Rows("auditEvents").Find(new BsonDocument("kind", C.AuditKind)).Sort(new BsonDocument("_id", 1)).ToListAsync()) },
        { "receipts", new BsonArray(await data.Rows("idempotencyRecords").Find(new BsonDocument("operation", C.Operation)).Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
}
