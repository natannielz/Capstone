using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Documents;
using UnitToko.Platform;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed class CatalogPhotoMigrationTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Photo_read_extension_preserves_financial_branch_and_actual_private_original()
    {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture);
        var definition = await DefinitionAsync(data.Database, CatalogEvidenceReadSchemas.Leases);
        var validator = definition["options"]["validator"].AsBsonDocument;
        Assert.Equal(CatalogEvidenceReadSchemas.PreviousValidator(CatalogEvidenceReadSchemas.Leases),
            validator["$or"][0]);
        Assert.Equal(CatalogEvidenceReadSchemas.CatalogLeaseValidator(), validator["$or"][1]);
        Assert.True(SupplierDocumentEvidenceSchemas.Supports(CatalogEvidenceReadSchemas.Leases, validator,
            SupplierDocumentEvidenceSchemas.Validator(CatalogEvidenceReadSchemas.Leases)));
        await data.ValidateAsync();
        await using (var original = await data.OpenAsync()) {
            using var received = new MemoryStream();
            await original.Content.CopyToAsync(received);
            Assert.Equal(data.Bytes, received.ToArray());
        }
        var financial = await data.Rows(CatalogEvidenceReadSchemas.Leases)
            .Find(new BsonDocument("target.kind", "PaymentDeclaration"))
            .Sort(new BsonDocument("_id", -1)).FirstAsync();
        Assert.False(financial.Contains("purpose"));
        Assert.Equal("released", financial["state"].AsString);
        var disguised = financial.DeepClone().AsBsonDocument;
        disguised["target"]["kind"] = "CatalogSku";
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows(CatalogEvidenceReadSchemas.Leases)
            .ReplaceOneAsync(new BsonDocument("_id", financial["_id"]), disguised));
    }

    [Fact]
    public async Task Photo_read_upgrade_retains_stronger_operator_guard_exactly()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        await RestoreV79Async(data.Database);
        var guard = new BsonDocument("operatorGuard", new BsonDocument("$ne", true));
        var previous = CatalogEvidenceReadSchemas.PreviousValidator(CatalogEvidenceReadSchemas.Leases);
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "collMod", CatalogEvidenceReadSchemas.Leases },
            { "validator", new BsonDocument("$and", new BsonArray { previous, guard }) },
            { "validationLevel", "strict" }, { "validationAction", "error" } });
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        var after = await DefinitionAsync(data.Database, CatalogEvidenceReadSchemas.Leases);
        Assert.Equal(CatalogEvidenceReadSchemas.Validator(CatalogEvidenceReadSchemas.Leases),
            after["options"]["validator"]["$and"][0]);
        Assert.Equal(guard, after["options"]["validator"]["$and"][1]);
    }

    [Fact]
    public async Task Empty_interrupted_photo_candidate_resumes_exact_collections_indexes_and_history()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        await RestoreV79Async(data.Database);
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "collMod", CatalogEvidenceReadSchemas.Leases },
            { "validator", CatalogEvidenceReadSchemas.PreviousValidator(CatalogEvidenceReadSchemas.Leases) },
            { "validationLevel", "strict" }, { "validationAction", "error" } });
        var missing = CatalogPhotoSchemas.Keys(CatalogPhotoRecords.Photos)[0];
        await data.Database.GetCollection<BsonDocument>(CatalogPhotoRecords.Photos).Indexes.DropOneAsync(missing.Name);
        var before = await SnapshotAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        var after = await SnapshotAsync(data.Database);
        var expected = before.DeepClone().AsBsonDocument;
        expected[CatalogEvidenceReadSchemas.Leases]["definition"]["options"]["validator"] =
            CatalogEvidenceReadSchemas.Validator(CatalogEvidenceReadSchemas.Leases);
        var index = Assert.Single(after[CatalogPhotoRecords.Photos]["indexes"].AsBsonArray, row => row["name"] == missing.Name);
        expected[CatalogPhotoRecords.Photos]["indexes"] = new BsonArray(expected[CatalogPhotoRecords.Photos]["indexes"].AsBsonArray
            .Append(index).OrderBy(row => row["name"].AsString, StringComparer.Ordinal));
        AssertExact(expected, after);
        Assert.Equal(CollectionMigrations.CurrentVersion, await data.Database.GetCollection<BsonDocument>("schemaMigrations").CountDocumentsAsync(new BsonDocument("state", "applied")));
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        AssertExact(after, await SnapshotAsync(data.Database));
    }

    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Populated_photo_owner_cannot_be_relabelled_as_a_predecessor_or_silently_recreated()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture, sku: true);
        var reservation = await EvidenceValidationTests.Upload(data);
        var processor = EvidenceValidationTests.Processor(data,
            new EvidenceValidationTests.FixtureScanner(signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1)));
        await EvidenceValidationTests.Policy(data, processor.PipelineId);
        Assert.Equal("completed", await processor.RunOnceAsync());
        await ReadPolicyAsync(data);
        var reference = Assert.IsType<EvidenceRef>((await data.StatusAsync(reservation.ReservationId)).Reference);
        var sku = await data.Catalog.RowAsync("skus", data.Target.Id);
        var photo = new CatalogPhotoService(data.DB, data.Unit, new(data.DB), data.Catalog.Clock,
            data.Catalog.Service, data.Service);
        var result = await photo.AssociateAsync(data.Actor,
            new(new(data.Target.Id), sku["revision"].AsInt64, 0, reservation.ReservationId, reference),
            CatalogAuthoringTestData.Metadata("photo-migration-owner"));
        Assert.True(result.AssociationId.Value > 0);
        var photoLease = await data.Rows(CatalogEvidenceReadSchemas.Leases)
            .Find(new BsonDocument("target.kind", "CatalogSku")).SingleAsync();
        Assert.Equal("catalog-photo-association", photoLease["purpose"].AsString);
        var inventedCurrent = photoLease.DeepClone().AsBsonDocument;
        inventedCurrent["purpose"] = "catalog-photo-current";
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows(CatalogEvidenceReadSchemas.Leases)
            .ReplaceOneAsync(new BsonDocument("_id", photoLease["_id"]), inventedCurrent));
        await EvidenceImageRevalidationMigrationTests.RestoreV80Async(data.DB);
        Assert.Equal(1L, (await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument {
            { "_id", 80 }, { "state", "applied" } })).DeletedCount);
        await RefusesAsync(data.DB, data.Catalog.Clock);
        // A missing head and a missing private clean owner cannot make the predecessor safe.
        var head = await data.RowAsync(CatalogPhotoRecords.Heads, data.Target.Id);
        await data.Rows(CatalogPhotoRecords.Heads).DeleteOneAsync(new BsonDocument("_id", data.Target.Id));
        try { await RefusesAsync(data.DB, data.Catalog.Clock); }
        finally { await data.Rows(CatalogPhotoRecords.Heads).InsertOneAsync(head); }
        var validation = await data.RowAsync("evidenceValidationResults", reservation.VersionRowId);
        await data.Rows("evidenceValidationResults").DeleteOneAsync(new BsonDocument("_id", reservation.VersionRowId));
        try { await RefusesAsync(data.DB, data.Catalog.Clock); }
        finally { await data.Rows("evidenceValidationResults").InsertOneAsync(validation); }
        Assert.Equal(result, await photo.LookupAsync(data.Actor, "photo-migration-owner"));
    }

    [Fact]
    public async Task Unsafe_photo_candidate_definition_or_predecessor_refuses_before_any_DDL()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        await RestoreV79Async(data.Database);
        var definition = await DefinitionAsync(data.Database, CatalogPhotoRecords.Heads);
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", CatalogPhotoRecords.Heads },
            { "validator", new BsonDocument("$and", new BsonArray { definition["options"]["validator"],
                new BsonDocument("revision", new BsonDocument("$gt", 0L)) }) } });
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", CatalogPhotoRecords.Heads },
            { "validator", definition["options"]["validator"] } }); }
        var index = new CreateIndexModel<BsonDocument>(new BsonDocument("skuId", 1),
            new CreateIndexOptions<BsonDocument> { Name = "unsafe_photo_ttl", ExpireAfter = TimeSpan.FromSeconds(1) });
        await data.Database.GetCollection<BsonDocument>(CatalogPhotoRecords.Photos).Indexes.CreateOneAsync(index);
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await data.Database.GetCollection<BsonDocument>(CatalogPhotoRecords.Photos).Indexes.DropOneAsync("unsafe_photo_ttl"); }
        var history = await data.Database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument("_id", 79)).SingleAsync();
        await data.Database.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 79));
        try { await RefusesAsync(data.Database, data.Clock,
            "Database has unknown, newer or inconsistent schema migration history; refusing to alter it"); }
        finally { await data.Database.GetCollection<BsonDocument>("schemaMigrations").InsertOneAsync(history); }
    }

    // This is only for predecessor rehearsal with no published photo owner or allocator.
    // Existing photo rows are never reclassified as pre-80 history.
    internal static async Task RestoreV79Async(IMongoDatabase db)
    {
        await EvidenceImageRevalidationMigrationTests.RestoreV80Async(db);
        var history = await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument())
            .Sort(new BsonDocument("_id", 1)).ToListAsync();
        if (history.Count <= 79) return;
        Assert.Equal(80, history.Count);
        Assert.All(history, row => Assert.Equal("applied", row["state"]));
        await CatalogPhotoStorage.RequireAsync(db, default);
        await CatalogEvidenceReadSchemas.RequireStorageAsync(db, default);
        foreach (var table in CatalogPhotoSchemas.Collections)
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
        foreach (var table in new[] { CatalogPhotoRecords.Photos, CatalogPhotoRecords.Audits })
            Assert.False(await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", table)).AnyAsync());
        Assert.False(await db.GetCollection<BsonDocument>(CatalogEvidenceReadSchemas.Leases)
            .Find(new BsonDocument("target.kind", "CatalogSku")).AnyAsync());
        var leaseDefinition = await DefinitionAsync(db, CatalogEvidenceReadSchemas.Leases);
        var installed = leaseDefinition["options"]["validator"].AsBsonDocument;
        var previous = CatalogEvidenceReadSchemas.PreviousValidator(CatalogEvidenceReadSchemas.Leases);
        var reverted = PaymentManualReviewMigration.ExtendValidator(installed,
            CatalogEvidenceReadSchemas.Validator(CatalogEvidenceReadSchemas.Leases), previous,
            CatalogEvidenceReadSchemas.Leases);
        // The test-only predecessor has the actual pre-80 financial validator;
        // stronger operator $and terms remain exact and all rows stay untouched.
        await db.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "collMod", CatalogEvidenceReadSchemas.Leases }, { "validator", reverted },
            { "validationLevel", "strict" }, { "validationAction", "error" } });
        Assert.Equal(1L, (await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument {
            { "_id", 80 }, { "state", "applied" } })).DeletedCount);
    }

    private static async Task RefusesAsync(IMongoDatabase db, IClock clock,
        string expected = "Catalog primary-photo migration found incompatible retained storage")
    {
        var before = await SnapshotAsync(db);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync());
        Assert.Equal(expected, error.Message);
        AssertExact(before, await SnapshotAsync(db));
        Assert.False(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument { { "_id", 80 }, { "state", "applied" } }).AnyAsync());
    }

    [SupportedOSPlatform("windows")]
    private static async Task ReadPolicyAsync(EvidenceAdmissionTestData data) =>
        await data.Rows("evidenceReadPolicies").InsertOneAsync(new BsonDocument {
            { "_id", (await new IdAllocator(data.DB).NextAsync("evidenceReadPolicies")).Value },
            { "schemaVersion", 1 }, { "storeId", data.StoreId }, { "active", true }, { "revision", 1L }, { "fence", 0L },
            { "createdAt", data.Catalog.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Catalog.Clock.UtcNow.UtcDateTime },
            { "maximumBytes", 1024L }, { "verificationDurationMs", 10000L }, { "leaseDurationMs", 5000L },
            { "requestDurationMs", 60000L }, { "maximumValidationAgeMs", 86400000L },
            { "maximumSignatureAgeMs", 604800000L } });

    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase db)
    {
        var result = new BsonDocument();
        foreach (var definition in (await (await db.ListCollectionsAsync()).ToListAsync())
            .OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString;
            if (name is "schemaMigrations" or "migrationLocks") continue;
            result[name] = new BsonDocument { { "definition", definition },
                { "indexes", new BsonArray((await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync())
                    .OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) },
                { "rows", new BsonArray(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument())
                    .Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
        }
        return result;
    }

    private static void AssertExact(BsonDocument expected, BsonDocument actual)
    {
        Assert.Equal(expected.Names.ToArray(), actual.Names.ToArray());
        foreach (var name in expected.Names)
            Assert.True(expected[name].AsBsonDocument.ToBson().SequenceEqual(actual[name].AsBsonDocument.ToBson()),
                "Exact retained rows, indexes or collection UUID changed in " + name);
    }

    private static async Task<BsonDocument> DefinitionAsync(IMongoDatabase db, string table) =>
        await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync();
}
