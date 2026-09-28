using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using System.Collections.Concurrent;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed class CatalogMigrationTests(MongoFixture fixture)
{
    private static readonly string[] Collections = ["catalogCategories", "productFamilies", "skus", "variantDefinitions", "catalogIdentifiers", "catalogIdentifierHistory"];
    private readonly IClock clock = new SystemClock();
    private static readonly DateTime At = new(2026, 9, 28, 1, 0, 0, DateTimeKind.Utc);
    private static readonly UpdateDefinitionBuilder<BsonDocument> U = Builders<BsonDocument>.Update;

    [Fact]
    public async Task Fresh_catalog_migration_adds_empty_strict_collections_without_catalog_or_authority_seeds()
    {
        await InDatabase(async db =>
        {
            await ApplyV10(db);
            var history = await History(db);
            await ApplyV11(db);
            foreach (var name in Collections)
            {
                var definition = Assert.Single(await Definitions(db), row => row["name"] == name);
                Assert.Equal("strict", definition["options"]["validationLevel"]);
                Assert.Equal("error", definition["options"]["validationAction"]);
                Assert.Equal(0, await Rows(db, name).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            }
            Assert.Equal(history, (await History(db)).Take(10));
            Assert.Equal(11, (await History(db)).Count);
            foreach (var name in new[] { "authorizationGrants", "memberships", "catalogEntitlements", "catalogAssortments" })
                Assert.Equal(0, await Rows(db, name).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            var before = await Definitions(db); await ApplyV11(db); Assert.Equal(before, await Definitions(db));
        });
    }

    [Fact]
    public async Task Incomplete_legacy_catalog_is_rejected_before_any_ddl_counter_or_prior_history_change()
    {
        await InDatabase(async db =>
        {
            await ApplyV10(db);
            await Rows(db, "skus").InsertOneAsync(new BsonDocument
            {
                { "_id", 400L }, { "schemaVersion", 1 }, { "organizationId", 100L }, { "sku", "OLD-PACK" },
                { "publicId", "77000000-0000-4000-8000-000000000001" }, { "displayName", "Legacy carton with unknown contents" }
            });
            var before = await Definitions(db); var indexes = await Indexes(db, "skus"); var history = await History(db);
            var counters = await Rows(db, "idCounters").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyV11(db));
            Assert.Contains("skus", error.Message); Assert.Contains("reconcile", error.Message);
            Assert.Equal(before, await Definitions(db)); Assert.Equal(indexes, await Indexes(db, "skus"));
            Assert.Equal(history, (await History(db)).Take(10));
            Assert.Equal(counters, await Rows(db, "idCounters").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());
        });
    }

    [Theory]
    [InlineData("uuid-pair")]
    [InlineData("uuid-subtype")]
    [InlineData("duplicate-uuid")]
    [InlineData("normalized-code")]
    [InlineData("owner-parent")]
    [InlineData("family-missing")]
    [InlineData("category-owner")]
    [InlineData("current-pointer")]
    [InlineData("history-missing")]
    [InlineData("history-snapshot")]
    [InlineData("audit-link")]
    [InlineData("audit-kind")]
    [InlineData("audit-shape")]
    [InlineData("audit-snapshot")]
    [InlineData("audit-immutable-sku")]
    [InlineData("audit-definition-time")]
    [InlineData("audit-owner")]
    [InlineData("audit-result-time")]
    [InlineData("audit-family-category")]
    [InlineData("audit-family-code")]
    [InlineData("audit-before-family")]
    [InlineData("legacy-sku-index")]
    [InlineData("audit-counter-type")]
    [InlineData("audit-counter-overflow")]
    [InlineData("audit-id-overflow")]
    [InlineData("publication-missing")]
    [InlineData("publication-owner")]
    [InlineData("definition-attributes-order")]
    [InlineData("actor-missing")]
    [InlineData("identifier-code")]
    [InlineData("predecessor-missing")]
    [InlineData("predecessor-self")]
    [InlineData("historical-definition-type")]
    [InlineData("definition-material")]
    [InlineData("definition-gap")]
    [InlineData("definition-backdated")]
    [InlineData("counter-type")]
    [InlineData("counter-overflow")]
    [InlineData("id-overflow")]
    public async Task Every_legacy_graph_and_counter_is_preflighted_before_any_schema_or_sequence_change(string fault)
    {
        await InDatabase(async db =>
        {
            await ApplyV10(db); await Seed(db, historical: fault.StartsWith("definition", StringComparison.Ordinal) || fault == "historical-definition-type");
            switch (fault)
            {
                case "uuid-pair": await Change("productFamilies", 20, U.Set("publicUuid", Uuid("55000000-0000-4000-8000-000000000999"))); break;
                case "uuid-subtype": await Change("productFamilies", 20, U.Set("publicUuid", new BsonBinaryData(new byte[16]))); break;
                case "duplicate-uuid": var duplicate = Family(); duplicate["_id"] = 21L; await Rows(db, "productFamilies").InsertOneAsync(duplicate); break;
                case "normalized-code": await Change("skus", 30, U.Set("sku", "pack-12")); break;
                case "owner-parent": await Change("authorizationScopes", 101, U.Set("parentId", 999L)); break;
                case "family-missing": await Rows(db, "productFamilies").DeleteOneAsync(Id(20)); break;
                case "category-owner": await Change("catalogCategories", 10, U.Set("authoringOwner.channelId", 104L)); break;
                case "current-pointer": await Change("skus", 30, U.Set("currentDefinitionId", 999L)); break;
                case "history-missing": await Rows(db, "catalogIdentifierHistory").DeleteOneAsync(Id(60)); break;
                case "history-snapshot": await Change("catalogIdentifierHistory", 60, U.Set("assignment.rawValue", "DIFFERENT")); break;
                case "audit-link": await Change("auditEvents", 70, U.Set("result.id", 999L)); break;
                case "audit-kind": await Change("auditEvents", 70, U.Set("kind", "catalog.read")); break;
                case "audit-shape": await Change("auditEvents", 70, U.Set("unknown", "private")); break;
                case "audit-snapshot": await Change("auditEvents", 70, U.Set("after.family.name", " invalid ")); break;
                case "audit-immutable-sku": await Change("auditEvents", 70, U.Set("after.sku.physicalItemKey", "other-item")); break;
                case "audit-definition-time": await Change("auditEvents", 70, U.Set("after.definition.effectiveAt", At.AddSeconds(-1))); break;
                case "audit-owner": var otherOwner = Owner(); otherOwner["channelId"] = 104L; await Change("auditEvents", 70, U.Set("owners", new BsonArray { otherOwner })); break;
                case "audit-result-time": await Change("auditEvents", 70, U.Set("result.recordedAt", At.AddSeconds(-1))); break;
                case "audit-family-category": await Change("auditEvents", 70, U.Set("after.family.categoryId", 999L)); break;
                case "audit-family-code": await Change("auditEvents", 70, U.Set("after.family.categoryCode", "OTHER")); break;
                case "audit-before-family":
                    var otherFamily = Family(); otherFamily["_id"] = 21L; otherFamily["publicId"] = "55000000-0000-4000-8000-000000000002"; otherFamily["publicUuid"] = Uuid(otherFamily["publicId"].AsString);
                    await Rows(db, "productFamilies").InsertOneAsync(otherFamily); await Change("auditEvents", 70, U.Set("before.family", otherFamily)); break;
                case "legacy-sku-index":
                    await Rows(db, "skus").Indexes.DropOneAsync("uq_organizationId_sku");
                    await Rows(db, "skus").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument { { "organizationId", 1 }, { "sku", 1 } }, new CreateIndexOptions<BsonDocument> { Name = "uq_organizationId_sku", Unique = true, PartialFilterExpression = new BsonDocument("active", true) })); break;
                case "audit-counter-type": await Rows(db, "idCounters").InsertOneAsync(new BsonDocument { { "_id", "auditEvents" }, { "value", 1 } }, new InsertOneOptions { BypassDocumentValidation = true }); break;
                case "audit-counter-overflow": await Rows(db, "idCounters").InsertOneAsync(new BsonDocument { { "_id", "auditEvents" }, { "value", long.MaxValue } }); break;
                case "audit-id-overflow": await Rows(db, "auditEvents").InsertOneAsync(new BsonDocument { { "_id", long.MaxValue }, { "schemaVersion", 1 }, { "kind", "unrelated-event" } }); break;
                case "publication-missing": await Change("skus", 30, U.Set("catalogListings", new BsonArray { new BsonDocument { { "assortmentId", 90L }, { "ownerScope", Owner() } } })); break;
                case "publication-owner":
                    var publicationOwner = Owner(); publicationOwner["channelId"] = 104L;
                    await Rows(db, "catalogAssortments").InsertOneAsync(Assortment());
                    await Change("skus", 30, U.Set("catalogListings", new BsonArray { new BsonDocument { { "assortmentId", 90L }, { "ownerScope", publicationOwner } } })); break;
                case "definition-attributes-order": await Change("variantDefinitions", 40, U.Set("attributes", new BsonArray { Attribute("z", "last"), Attribute("a", "first") })); break;
                case "actor-missing": await Rows(db, "identities").DeleteOneAsync(Id(80)); break;
                case "identifier-code": await Change("catalogIdentifiers", 50, U.Set("canonicalValue", "DIFFERENT").Set("rawValue", "DIFFERENT")); break;
                case "predecessor-missing": await Change("skus", 30, U.Set("predecessorSkuId", 999L)); break;
                case "predecessor-self": await Change("skus", 30, U.Set("predecessorSkuId", 30L)); break;
                case "historical-definition-type": await Change("variantDefinitions", 40, U.Set("baseUnitsPerPackage", 12L)); break;
                case "definition-material": await Change("variantDefinitions", 41, U.Set("baseUnitsPerPackage", 10)); break;
                case "definition-gap": await Change("variantDefinitions", 41, U.Set("version", 3L)); break;
                case "definition-backdated": await Change("variantDefinitions", 41, U.Set("effectiveAt", At.AddSeconds(-1))); break;
                case "id-overflow": var overflow = Category(); overflow["_id"] = long.MaxValue; overflow["code"] = "OVERFLOW"; await Rows(db, "catalogCategories").InsertOneAsync(overflow); break;
            }
            BsonValue value = fault == "counter-type" ? new BsonInt32(1) : new BsonInt64(fault == "counter-overflow" ? long.MaxValue : 1);
            await Rows(db, "idCounters").InsertOneAsync(new BsonDocument { { "_id", "catalogIdentifierHistory" }, { "value", value } }, new InsertOneOptions { BypassDocumentValidation = true });
            var before = await PersistenceState(db);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyV11(db));
            Assert.Contains("reconcile", error.Message); Assert.DoesNotContain("Unpublished private family", error.Message);
            Assert.Equal(before, await PersistenceState(db));
            async Task Change(string name, long id, UpdateDefinition<BsonDocument> update) =>
                await Rows(db, name).UpdateOneAsync(Id(id), update, new UpdateOptions { BypassDocumentValidation = true });
        });
    }

    [Fact]
    public async Task Family_reclassification_retains_original_category_and_creation_evidence()
    {
        await InDatabase(async db =>
        {
            await ApplyV10(db); await Seed(db);
            var originalAudit = Audit(); originalAudit["before"]["category"] = Category(); originalAudit["after"]["category"] = Category();
            await Rows(db, "auditEvents").ReplaceOneAsync(Id(70), originalAudit);
            var nextCategory = Category(); nextCategory["_id"] = 11L; nextCategory["code"] = "OFFICE"; nextCategory["name"] = "Kategori sekarang";
            await Rows(db, "catalogCategories").InsertOneAsync(nextCategory);
            await Rows(db, "productFamilies").UpdateOneAsync(Id(20), U.Set("categoryId", 11L).Set("categoryCode", "OFFICE").Set("revision", 2L));
            await Rows(db, "catalogCategories").UpdateOneAsync(Id(10), U.Set("name", "Nama kategori lama diperbarui").Set("revision", 2L));
            await ApplyV11(db);
            Assert.Equal(originalAudit, await Rows(db, "auditEvents").Find(Id(70)).SingleAsync());
            var family = await Rows(db, "productFamilies").Find(Id(20)).SingleAsync();
            Assert.Equal(11L, family["categoryId"].AsInt64); Assert.Equal("OFFICE", family["categoryCode"].AsString);
            Assert.Equal(1, await Rows(db, "catalogIdentifiers").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        });
    }

    [Fact]
    public async Task Default_definition_assignment_time_is_rejected_by_runtime_and_database()
    {
        await InDatabase(async db =>
        {
            await ApplyV10(db); await Seed(db); await ApplyV11(db);
            var minimum = new BsonDateTime(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc));
            foreach (var (collection, row) in Graph().Where(pair => pair.Collection is "variantDefinitions" or "catalogIdentifiers" or "catalogIdentifierHistory"))
            {
                RewriteDates(row, minimum); row["_id"] = 999L;
                Assert.False(CatalogSchemas.IsValid(collection, row), collection);
                Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => Rows(db, collection).InsertOneAsync(row))).WriteError.Code);
            }
        });
    }

    [Fact]
    public async Task Earliest_valid_millisecond_preserves_pure_policy_and_persisted_provenance()
    {
        await InDatabase(async db =>
        {
            await ApplyV10(db); await Seed(db);
            var earliest = new BsonDateTime(new BsonDateTime(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)).MillisecondsSinceEpoch + 1);
            foreach (var (collection, row) in Graph())
            {
                RewriteDates(row, earliest); Assert.True(CatalogSchemas.IsValid(collection, row), collection);
                await Rows(db, collection).ReplaceOneAsync(Id(row["_id"].AsInt64), row);
            }
            var audit = Audit(); RewriteDates(audit, earliest); await Rows(db, "auditEvents").ReplaceOneAsync(Id(70), audit);
            await ApplyV11(db);
            Assert.Equal(earliest, (await Rows(db, "variantDefinitions").Find(Id(40)).SingleAsync())["effectiveAt"]);
        });
    }

    private static void RewriteDates(BsonValue value, BsonDateTime date)
    {
        if (value.IsBsonDocument)
            foreach (var name in value.AsBsonDocument.Names.ToArray())
            {
                if (value[name].IsBsonDateTime) value[name] = date;
                else RewriteDates(value[name], date);
            }
        else if (value.IsBsonArray) foreach (var item in value.AsBsonArray) RewriteDates(item, date);
    }

    [Fact]
    public async Task Existing_v6_publication_owners_are_normalized_for_equality_without_rewriting_tuples()
    {
        await InDatabase(async db =>
        {
            await ApplyV10(db); await Seed(db);
            var assortment = Assortment(); var owner = assortment["ownerScope"].AsBsonDocument;
            owner.Remove("warehouseId"); owner["branchId"] = BsonNull.Value; owner["divisionId"] = BsonNull.Value;
            await Rows(db, "catalogAssortments").InsertOneAsync(assortment);
            var listings = new BsonArray { new BsonDocument { { "assortmentId", 90L }, { "ownerScope", Owner() } } };
            await Rows(db, "skus").UpdateOneAsync(Id(30), U.Set("catalogListings", listings));
            await ApplyV11(db);
            Assert.Equal(assortment, await Rows(db, "catalogAssortments").Find(Id(90)).SingleAsync());
            Assert.Equal(listings, (await Rows(db, "skus").Find(Id(30)).SingleAsync())["catalogListings"]);
        });
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("cycle")]
    [InlineData("backdated")]
    [InlineData("audit-predecessor")]
    [InlineData("historical-publication-missing")]
    [InlineData("historical-publication-owner")]
    [InlineData("inactive-predecessor")]
    public async Task Replacement_provenance_keeps_both_identities_and_exact_original_predecessor(string fault)
    {
        await InDatabase(async db =>
        {
            await ApplyV10(db); await Seed(db);
            var at = fault == "backdated" ? At.AddSeconds(-1) : At.AddSeconds(1);
            var sku = Sku(); sku["_id"] = 31L; sku["sku"] = "PACK-NEW"; sku["displayName"] = "PACK-NEW · Dus"; sku["currentDefinitionId"] = 41L; sku["predecessorSkuId"] = 30L;
            sku["publicId"] = "66000000-0000-4000-8000-000000000002"; sku["publicUuid"] = Uuid(sku["publicId"].AsString);
            var definition = Definition(); definition["_id"] = 41L; definition["skuId"] = 31L; definition["baseUnitsPerPackage"] = 24; definition["effectiveAt"] = at;
            var identifier = Identifier(); identifier["_id"] = 51L; identifier["skuId"] = 31L; identifier["rawValue"] = "PACK-NEW"; identifier["canonicalValue"] = "PACK-NEW";
            identifier["auditId"] = 71L; identifier["provenanceReference"] = "catalog.command:71"; identifier["effectiveAt"] = at;
            var history = Assignment(); history["_id"] = 61L; history["identifierId"] = 51L; history["skuId"] = 31L; history["auditId"] = 71L; history["createdAt"] = at; history["assignment"] = identifier;
            var audit = Audit(); audit["_id"] = 71L; audit["operation"] = "catalog.sku.replace"; audit["createdAt"] = at; audit["before"] = Snapshot(true);
            audit["after"] = Snapshot(true); audit["after"]["sku"] = sku; audit["after"]["definition"] = definition;
            var result = audit["result"].AsBsonDocument; result["id"] = 31L; result["currentDefinitionId"] = 41L; result["predecessorSkuId"] = 30L; result["recordedAt"] = at;
            if (fault == "audit-predecessor") { audit["before"]["sku"] = sku; audit["before"]["definition"] = definition; }
            if (fault == "inactive-predecessor") audit["before"]["sku"]["active"] = false;
            if (fault.StartsWith("historical-publication", StringComparison.Ordinal))
            {
                var owner = Owner();
                if (fault == "historical-publication-owner") { owner["channelId"] = 104L; await Rows(db, "catalogAssortments").InsertOneAsync(Assortment()); }
                audit["before"]["sku"]["catalogListings"] = new BsonArray { new BsonDocument { { "assortmentId", 90L }, { "ownerScope", owner } } };
            }
            foreach (var (collection, row) in new[] { ("skus", sku), ("variantDefinitions", definition), ("catalogIdentifiers", identifier), ("catalogIdentifierHistory", history), ("auditEvents", audit) })
                await Rows(db, collection).InsertOneAsync(row);
            if (fault == "cycle") await Rows(db, "skus").UpdateOneAsync(Id(30), U.Set("predecessorSkuId", 31L));
            if (fault != "valid")
            {
                var before = await PersistenceState(db); await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyV11(db)); Assert.Equal(before, await PersistenceState(db));
            }
            else
            {
                await ApplyV11(db);
                Assert.Equal(2, await Rows(db, "catalogIdentifiers").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
                Assert.Equal(Sku(), await Rows(db, "skus").Find(Id(30)).SingleAsync());
                Assert.Equal(sku, await Rows(db, "skus").Find(Id(31)).SingleAsync());
            }
        });
    }

    [Fact]
    public async Task Historical_evidence_and_valid_unicode_survive_later_mutations_without_current_authority()
    {
        await InDatabase(async db =>
        {
            await ApplyV10(db); await Seed(db, historical: true);
            await Rows(db, "productFamilies").UpdateOneAsync(Id(20), U.Set("name", "Nama sekarang 🧺").Set("description", "Ukuran\n\tBarang").Set("revision", long.MaxValue).Set("fence", long.MaxValue));
            await Rows(db, "skus").UpdateOneAsync(Id(30), U.Set("active", false).Set("revision", 7L));
            await Rows(db, "authorizationScopes").UpdateOneAsync(Id(101), U.Set("status", "suspended"));
            // Unrelated catalog.read/shared events are not parsed as creation-command evidence.
            await Rows(db, "auditEvents").InsertManyAsync([
                new BsonDocument { { "_id", 71L }, { "schemaVersion", 1 }, { "kind", "catalog.read" }, { "safe", "separate envelope" } },
                new BsonDocument { { "_id", 99L }, { "schemaVersion", 1 }, { "kind", "other.module" } }
            ]);
            var original = await Rows(db, "auditEvents").Find(Id(70)).SingleAsync();
            await ApplyV11(db);
            Assert.Equal(original, await Rows(db, "auditEvents").Find(Id(70)).SingleAsync());
            Assert.Equal(100L, (await new IdAllocator(db).NextAsync("auditEvents")).Value);
            Assert.Equal(long.MaxValue, (await Rows(db, "productFamilies").Find(Id(20)).SingleAsync())["revision"].AsInt64);
            Assert.Empty(await Rows(db, "authorizationGrants").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());
        });
    }

    [Fact]
    public async Task Exact_shapes_reject_wrong_types_unknown_fields_noncanonical_attributes_and_unsafe_publication_at_rest()
    {
        await InDatabase(async db =>
        {
            await ApplyV10(db); await Seed(db); await ApplyV11(db);
            foreach (var (collection, row) in Graph()) Assert.True(CatalogSchemas.IsValid(collection, row), collection);
            var invalid = new List<(string Collection, BsonDocument Row)>();
            void Bad(string collection, BsonDocument row, string field, BsonValue value) { row["_id"] = 999L; row[field] = value; invalid.Add((collection, row)); }
            Bad("catalogCategories", Category(), "schemaVersion", 1L);
            Bad("catalogCategories", Category(), "name", " invalid ");
            Bad("productFamilies", Family(), "imageAssetIds", new BsonArray { 123L });
            Bad("productFamilies", Family(), "unexpected", "hidden");
            Bad("productFamilies", Family(), "description", "unconverted\rcarriage return");
            Bad("variantDefinitions", Definition(), "baseUnitsPerPackage", 12L);
            Bad("variantDefinitions", Definition(), "minimumOrderPackages", 1000001);
            Bad("variantDefinitions", Definition(), "attributes", new BsonArray { Attribute("size", "big"), Attribute("size", "small") });
            Bad("variantDefinitions", Definition(), "effectiveAt", new BsonDateTime(long.MaxValue));
            Bad("catalogIdentifiers", Identifier(), "retiredAt", At);
            Bad("catalogIdentifierHistory", Assignment(), "assignmentVersion", 2L);
            var retired = Sku(); retired["active"] = false;
            Bad("skus", retired, "catalogListings", new BsonArray { new BsonDocument { { "assortmentId", 90L }, { "ownerScope", Owner() } } });
            foreach (var (collection, row) in invalid)
            {
                Assert.False(CatalogSchemas.IsValid(collection, row), collection + ": " + row.ToJson());
                Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => Rows(db, collection).InsertOneAsync(row))).WriteError.Code);
            }
            // BSON JSON Schema can enforce binData shape; equality and subtype are enforced by
            // the shared decoder/preflight instead of claiming generic binData proves UUID identity.
            var mismatched = Family(); mismatched["publicUuid"] = Uuid("55000000-0000-4000-8000-000000000999");
            Assert.False(CatalogSchemas.IsValid("productFamilies", mismatched));
            var unusual = Definition(); unusual["attributes"] = new BsonArray { Attribute("$key.with.dot", "Nilai ukuran – 12") };
            Assert.True(CatalogSchemas.IsValid("variantDefinitions", unusual));
        });
    }

    [Theory]
    [InlineData("ttl")]
    [InlineData("nonunique")]
    [InlineData("sparse")]
    [InlineData("partial")]
    [InlineData("hidden")]
    [InlineData("collation")]
    [InlineData("wrong-name-key")]
    public async Task Unsafe_required_indexes_never_trigger_partial_ddl(string fault)
    {
        await InDatabase(async db =>
        {
            await ApplyV10(db); await Seed(db);
            var options = new CreateIndexOptions<BsonDocument> { Name = "ux_publicId", Unique = fault != "nonunique" };
            if (fault == "ttl") options.ExpireAfter = TimeSpan.FromDays(30);
            if (fault == "sparse") options.Sparse = true;
            if (fault == "partial") options.PartialFilterExpression = new BsonDocument("active", true);
            if (fault == "hidden") options.Hidden = true;
            if (fault == "collation") options.Collation = new Collation("en");
            await Rows(db, "productFamilies").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument(fault is "ttl" or "wrong-name-key" ? "createdAt" : "publicId", 1), options));
            var before = await PersistenceState(db);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyV11(db)); Assert.Equal(before, await PersistenceState(db));
        });
    }

    [Theory]
    [InlineData("view")]
    [InlineData("capped")]
    [InlineData("timeseries")]
    [InlineData("collation")]
    [InlineData("retention")]
    public async Task Unsafe_collection_options_are_rejected_before_any_other_collection_changes(string fault)
    {
        await InDatabase(async db =>
        {
            await ApplyV10(db); const string name = "catalogIdentifierHistory";
            if (fault == "view") await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", name }, { "viewOn", "otherHistory" }, { "pipeline", new BsonArray() } });
            if (fault == "capped") await db.CreateCollectionAsync(name, new CreateCollectionOptions { Capped = true, MaxSize = 4096 });
            if (fault == "timeseries") await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", name }, { "timeseries", new BsonDocument("timeField", "createdAt") } });
            if (fault == "collation") await db.CreateCollectionAsync(name, new CreateCollectionOptions { Collation = new Collation("en") });
            if (fault == "retention") await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", name }, { "clusteredIndex", new BsonDocument { { "key", new BsonDocument("_id", 1) }, { "unique", true } } }, { "expireAfterSeconds", 86400L } });
            var before = await Definitions(db);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyV11(db)); Assert.Equal(before, await Definitions(db));
        });
    }

    [Fact]
    public async Task Interrupted_majority_migration_preserves_stronger_rules_original_indexes_history_and_independent_versions()
    {
        await InDatabase(async db =>
        {
            await ApplyV10(db); await Seed(db, historical: true);
            // Definition and mutable SKU counters are independent; migrations do not invent a relation.
            await Rows(db, "skus").UpdateOneAsync(Id(30), U.Set("revision", 1L));
            var prior = new BsonDocument("name", new BsonDocument("$ne", "Forbidden reviewed name"));
            await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "productFamilies" }, { "validator", prior } });
            await Rows(db, "productFamilies").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("publicId", 1), new CreateIndexOptions { Name = "reviewed_uuid", Unique = true }));
            await Rows(db, "idCounters").InsertOneAsync(new BsonDocument { { "_id", "productFamilies" }, { "value", 1000L } });
            await Rows(db, "idCounters").InsertOneAsync(new BsonDocument { { "_id", "auditEvents" }, { "value", 500L } });
            var retained = new BsonDocument(); foreach (var name in Collections) retained[name] = new BsonArray(await Rows(db, name).Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync());
            using var stop = new CancellationTokenSource(); var commands = new ConcurrentBag<BsonDocument>();
            var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0&w=1");
            settings.ClusterConfigurator = builder =>
            {
                builder.Subscribe<CommandStartedEvent>(command => commands.Add(command.Command.DeepClone().AsBsonDocument));
                builder.Subscribe<CommandSucceededEvent>(command => { if (command.CommandName == "collMod") stop.Cancel(); });
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApplyV11(new MongoClient(settings).GetDatabase(db.DatabaseNamespace.DatabaseName), stop.Token));
            await ApplyV11(db);
            foreach (var name in Collections) Assert.Equal(retained[name], new BsonArray(await Rows(db, name).Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync()));
            Assert.Contains(await Indexes(db, "skus"), row => row["name"] == "uq_organizationId_sku" && row["unique"] == true);
            Assert.Equal(new BsonDocument("$and", new BsonArray { new BsonDocument("organizationId", new BsonDocument("$exists", true)), new BsonDocument("sku", new BsonDocument("$exists", true)) }),
                Assert.Single(await Indexes(db, "skus"), row => row["name"] == "uq_organizationId_sku")["partialFilterExpression"]);
            Assert.Contains(await Indexes(db, "productFamilies"), row => row["name"] == "reviewed_uuid");
            Assert.DoesNotContain(await Indexes(db, "productFamilies"), row => row["name"] == "ux_publicId");
            Assert.Equal(1001L, (await new IdAllocator(db).NextAsync("productFamilies")).Value);
            Assert.Equal(501L, (await new IdAllocator(db).NextAsync("auditEvents")).Value);
            Assert.Equal(42L, (await new IdAllocator(db).NextAsync("variantDefinitions")).Value);
            Assert.Contains(commands, command => command.Contains("create") || command.Contains("collMod"));
            Assert.All(commands.Where(command => command.Contains("create") || command.Contains("collMod") || command.Contains("createIndexes")), command => Assert.Equal("majority", command["writeConcern"]["w"]));
            var forbidden = Family(); forbidden["_id"] = 99L; forbidden["name"] = "Forbidden reviewed name";
            Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => Rows(db, "productFamilies").InsertOneAsync(forbidden))).WriteError.Code);
            var duplicate = Sku(); duplicate["_id"] = 999L;
            Assert.Equal(ServerErrorCategory.DuplicateKey, (await Assert.ThrowsAsync<MongoWriteException>(() => Rows(db, "skus").InsertOneAsync(duplicate))).WriteError.Category);
        });
    }

    private static BsonDocument Id(long value) => new("_id", value);
    private static BsonBinaryData Uuid(string value) => new(Guid.Parse(value), GuidRepresentation.Standard);
    private static BsonDocument Owner() => new() { { "kind", "fulfillmentShop" }, { "organizationId", 100L }, { "shopId", 101L }, { "channelId", 103L }, { "warehouseId", BsonNull.Value } };
    private static BsonDocument Assortment() => new() { { "_id", 90L }, { "schemaVersion", 1 }, { "ownerScope", Owner() }, { "status", "active" }, { "version", 1L }, { "fence", 0L } };
    private static BsonDocument Category() => new() { { "_id", 10L }, { "schemaVersion", 1 }, { "organizationId", 100L }, { "authoringOwner", Owner() }, { "code", "FOOD" }, { "name", "Makanan – ukuran" }, { "active", true }, { "revision", 1L }, { "fence", 0L } };
    private static BsonDocument Family() => new()
    {
        { "_id", 20L }, { "schemaVersion", 1 }, { "organizationId", 100L }, { "authoringOwner", Owner() },
        { "publicId", "55000000-0000-4000-8000-000000000001" }, { "publicUuid", Uuid("55000000-0000-4000-8000-000000000001") },
        { "categoryId", 10L }, { "categoryCode", "FOOD" }, { "name", "Unpublished private family" }, { "description", "Isi eksplisit\nUkuran tetap" },
        { "imageAssetIds", new BsonArray() }, { "active", true }, { "revision", 1L }, { "fence", 0L }
    };
    private static BsonDocument Sku() => new()
    {
        { "_id", 30L }, { "schemaVersion", 1 }, { "organizationId", 100L }, { "authoringOwner", Owner() }, { "familyId", 20L },
        { "publicId", "66000000-0000-4000-8000-000000000001" }, { "publicUuid", Uuid("66000000-0000-4000-8000-000000000001") },
        { "sku", "PACK-12" }, { "physicalItemKey", "same-base-item" }, { "active", true }, { "currentDefinitionId", 40L }, { "revision", 1L },
        { "predecessorSkuId", BsonNull.Value }, { "displayName", "PACK-12 · Dus" }, { "catalogListings", new BsonArray() }, { "fence", 0L }
    };
    private static BsonDocument Attribute(string key, string value) => new() { { "key", key }, { "value", value } };
    private static BsonDocument Definition() => new()
    {
        { "_id", 40L }, { "schemaVersion", 1 }, { "skuId", 30L }, { "version", 1L }, { "packageLabel", "Dus" }, { "packageKind", "sealed" },
        { "baseUnitCode", "PCS" }, { "baseUnitsPerPackage", 12 }, { "attributes", new BsonArray { Attribute("size", "besar") } },
        { "imageAssetIds", new BsonArray() }, { "minimumOrderPackages", 1 }, { "orderIncrementPackages", 1 }, { "effectiveAt", At }
    };
    private static BsonDocument Identifier() => new()
    {
        { "_id", 50L }, { "schemaVersion", 1 }, { "organizationId", 100L }, { "skuId", 30L }, { "kind", "skuCode" }, { "namespace", "internal" },
        { "rawValue", "PACK-12" }, { "canonicalValue", "PACK-12" }, { "isPrimary", true }, { "effectiveAt", At }, { "retiredAt", BsonNull.Value },
        { "revision", 1L }, { "provenanceReference", "catalog.command:70" }, { "auditId", 70L }
    };
    private static BsonDocument Assignment() => new()
    {
        { "_id", 60L }, { "schemaVersion", 1 }, { "identifierId", 50L }, { "organizationId", 100L }, { "skuId", 30L }, { "assignmentVersion", 1L },
        { "event", "assigned" }, { "actorId", 80L }, { "createdAt", At }, { "auditId", 70L }, { "assignment", Identifier() }
    };
    private static BsonDocument Result() => new() { { "kind", "sku" }, { "id", 30L }, { "version", 1L }, { "familyId", 20L }, { "currentDefinitionId", 40L }, { "definitionVersion", 1L }, { "predecessorSkuId", BsonNull.Value }, { "recordedAt", At } };
    private static BsonDocument Snapshot(bool after) => new() { { "category", BsonNull.Value }, { "family", Family() }, { "sku", after ? Sku() : BsonNull.Value }, { "definition", after ? Definition() : BsonNull.Value }, { "assortments", new BsonArray() } };
    private static BsonDocument Audit() => new()
    {
        { "_id", 70L }, { "schemaVersion", 1 }, { "kind", "catalog.command" }, { "actorId", 80L }, { "actorGrantVersion", 1L },
        { "operation", "catalog.sku.create" }, { "bodyHash", new string('a', 64) }, { "reason", "Explicit synthetic catalog creation" },
        { "correlationId", "catalog-migration-fixture" }, { "createdAt", At }, { "owners", new BsonArray { Owner() } },
        { "result", Result() }, { "before", Snapshot(false) }, { "after", Snapshot(true) }
    };
    private static IEnumerable<(string Collection, BsonDocument Row)> Graph() =>
        [("catalogCategories", Category()), ("productFamilies", Family()), ("skus", Sku()), ("variantDefinitions", Definition()), ("catalogIdentifiers", Identifier()), ("catalogIdentifierHistory", Assignment())];
    private static async Task Seed(IMongoDatabase db, bool historical = false)
    {
        foreach (var (collection, row) in Graph()) await Rows(db, collection).InsertOneAsync(row);
        await Rows(db, "identities").InsertOneAsync(new BsonDocument { { "_id", 80L }, { "schemaVersion", 1 }, { "issuer", "https://synthetic-catalog.example/" }, { "subject", "original-actor" }, { "status", "suspended" }, { "grantVersion", 1L }, { "securityVersion", 1L }, { "fence", 0L } });
        foreach (var (id, kind, parent) in new[] { (100L, "fulfillmentOrganization", 0L), (101L, "shop", 100L), (102L, "warehouse", 101L), (103L, "channel", 100L), (104L, "channel", 100L) })
        {
            var node = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "kind", kind }, { "status", "active" }, { "version", 1L }, { "fence", 0L } };
            if (parent != 0) node["parentId"] = parent;
            await Rows(db, "authorizationScopes").InsertOneAsync(node);
        }
        await Rows(db, "auditEvents").InsertOneAsync(Audit());
        if (historical)
        {
            var next = Definition(); next["_id"] = 41L; next["version"] = 2L; next["packageLabel"] = "Dus baru"; next["effectiveAt"] = At.AddSeconds(1);
            await Rows(db, "variantDefinitions").InsertOneAsync(next);
            await Rows(db, "skus").UpdateOneAsync(Id(30), U.Set("currentDefinitionId", 41L).Set("revision", 3L).Set("displayName", "PACK-12 · Dus baru"));
        }
    }
    private static async Task<BsonDocument> PersistenceState(IMongoDatabase db)
    {
        var state = new BsonDocument { { "definitions", new BsonArray(await Definitions(db)) }, { "history", new BsonArray((await History(db)).Take(10)) },
            { "counters", new BsonArray(await Rows(db, "idCounters").Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
        foreach (var name in Collections) state[name] = new BsonArray(await Indexes(db, name));
        return state;
    }

    private async Task InDatabase(Func<IMongoDatabase, Task> run)
    {
        var name = "ut_catalog_migration_" + Guid.NewGuid().ToString("N");
        try { await run(fixture.Client.GetDatabase(name)); } finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private async Task ApplyV10(IMongoDatabase db)
    {
        IMigrationStep[] steps = [new FoundationMigration(db), new AuthorizationMigration(db), new AuthorizationLifecycleMigration(db),
            new IdentityMigration(db), new ProviderLifecycleMigration(db), new CatalogVisibilityMigration(db), new HistoricalReadMigration(db),
            new PersonAuthorityMigration(db), new PersonGovernanceMigration(db), new PersonReviewPreviewMigration(db)];
        var gate = new MigrationGate(db, clock, 11);
        foreach (var step in steps)
        {
            var lease = await gate.AcquireAsync(step.Version, CancellationToken.None);
            await step.ApplyAsync(lease!, CancellationToken.None); await lease!.CompleteAsync(CancellationToken.None);
        }
    }
    private async Task ApplyV11(IMongoDatabase db, CancellationToken token = default)
    {
        var lease = await new MigrationGate(db, clock, 11).AcquireAsync(11, token); if (lease is null) return;
        try { await new CatalogMigration(db).ApplyAsync(lease, token); await lease.CompleteAsync(token); }
        catch { await lease.ReleaseAsync(); throw; }
    }
    private static IMongoCollection<BsonDocument> Rows(IMongoDatabase db, string name) => db.GetCollection<BsonDocument>(name);
    private static async Task<List<BsonDocument>> Definitions(IMongoDatabase db) =>
        (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static Task<List<BsonDocument>> History(IMongoDatabase db) => Rows(db, "schemaMigrations").Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync();
    private static async Task<List<BsonDocument>> Indexes(IMongoDatabase db, string name) => await (await Rows(db, name).Indexes.ListAsync()).ToListAsync();
}
