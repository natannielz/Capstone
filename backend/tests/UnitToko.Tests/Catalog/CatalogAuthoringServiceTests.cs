using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed partial class CatalogAuthoringServiceTests(MongoFixture fixture)
{
    private static readonly UpdateDefinitionBuilder<BsonDocument> U = Builders<BsonDocument>.Update;
    [Fact]
    public async Task Staff_creates_family_and_twelve_twentyfour_variants_and_reloads_without_entitlements_or_seeds()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var options = await data.Service.OwnerOptionsAsync(data.Maker, Audit);
        Assert.Contains(options, item => item.Owner == Shop); Assert.Contains(options, item => item.Owner == Warehouse);
        Assert.DoesNotContain(options, item => item.Owner == OtherOwner);
        var category = await data.Service.SaveCategoryAsync(data.Maker, new(Shop, null, 0, " atk ", " Alat tulis 😀 "), Metadata("category"));
        var family = await data.Service.CreateFamilyAsync(data.Maker, new(Shop, category.Id, " Pensil 😀 ", "Baris satu\r\n\tBaris dua", []), Metadata("family"));
        var twelve = await data.Service.CreateSkuAsync(data.Maker, new(family.Id, 1, " pensil-12 ", "pencil-base", Definition()), Metadata("twelve"));
        var twentyfour = await data.Service.CreateSkuAsync(data.Maker, new(family.Id, 1, "PENSIL-24", "pencil-base", Definition(24)), Metadata("twentyfour"));
        var reloaded = data.NewService();
        var stored = await reloaded.FamilyAsync(data.Maker, family.Id.Value, Audit);
        Assert.Equal("Pensil 😀", stored.Name); Assert.Equal("Baris satu\n\tBaris dua", stored.Description);
        var variants = await reloaded.SkusAsync(data.Maker, family.Id.Value, 0, 25, Audit);
        Assert.Equal(2, variants.Total); Assert.Equal(new[] { 12, 24 }, variants.Items.Select(item => item.Definition.BaseUnitsPerPackage));
        Assert.All(variants.Items, item => { Assert.Equal(Shop, item.AuthoringOwner); Assert.Empty(item.Publications); });
        Assert.NotEqual(twelve.Id, twentyfour.Id);
        Assert.Equal(0, await data.CountAsync("catalogEntitlements")); Assert.Equal(0, await data.CountAsync("catalogAssortments"));
        Assert.Equal(2, await data.CountAsync("catalogIdentifiers")); Assert.Equal(2, await data.CountAsync("catalogIdentifierHistory"));
        Assert.Equal(4, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "catalog.command"))); Assert.Equal(4, await data.CountAsync("outboxEvents"));
        var raw = await data.RowAsync("skus", twelve.Id.Value);
        Assert.Equal(BsonBinarySubType.UuidStandard, raw["publicUuid"].AsBsonBinaryData.SubType);
        Assert.Equal(Guid.Parse(raw["publicId"].AsString), raw["publicUuid"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.BuyerService.QueryAsync(data.Buyer, 0, 25, null));
    }
    [Fact]
    public async Task Category_and_family_edits_keep_owner_and_primary_code_and_replay_original_result()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var category = await data.CategoryAsync();
        var updated = await data.Service.SaveCategoryAsync(data.Maker, new(Shop, category.Id, 1, "ATK", "Peralatan kantor"), Metadata("edit-category"));
        Assert.Equal(2, updated.Version);
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.SaveCategoryAsync(data.Maker,
            new(Shop, category.Id, 2, "NEW-CODE", "Changed"), Metadata("change-code")));
        var family = await data.Service.CreateFamilyAsync(data.Maker, new(Shop, category.Id, "Original", "", []), Metadata("create-family"));
        await data.Service.EditFamilyAsync(data.Maker, new(family.Id, 1, category.Id, "Corrected", "Details", []), Metadata("edit-family"));
        Assert.Equal("Corrected", (await data.Service.FamilyAsync(data.Maker, family.Id.Value, Audit)).Name);
        Assert.Equal(family, await data.Service.ReadOperationReceiptAsync(data.Maker, "catalog.family.create", "create-family", RecoveryAudit));
        Assert.Equal(family, await data.Service.CreateFamilyAsync(data.Maker, new(Shop, category.Id, "Original", "", []), Metadata("create-family")));
        Assert.Equal(1, await data.CountAsync("productFamilies"));
    }
    [Theory]
    [InlineData("owner")]
    [InlineData("family")]
    [InlineData("sku")]
    public async Task Other_owner_cannot_count_join_or_mutate_private_master(string target)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync();
        var before = await data.StateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
        {
            if (target == "owner") await data.Service.FamiliesAsync(data.Other, Shop, null, 0, 25, Audit);
            else if (target == "family") await data.Service.SkusAsync(data.Other, sku.FamilyId!.Value.Value, 0, 25, Audit);
            else await data.Service.ReviseDefinitionAsync(data.Other, new(sku.Id, 1, Definition(label: "Karton")), Metadata("cross-owner"));
        });
        Assert.Equal(before, await data.StateAsync());
    }
    [Fact]
    public async Task Warehouse_writer_cannot_promote_to_shop_or_sibling_warehouse()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var options = await data.Service.OwnerOptionsAsync(data.WarehouseActor, Audit);
        Assert.Single(options); Assert.Equal(Warehouse, options[0].Owner);
        await data.CategoryAsync(data.WarehouseActor, Warehouse);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.CategoryAsync(data.WarehouseActor, Shop, "SHOP"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.CategoryAsync(data.WarehouseActor, SecondWarehouse, "SIBLING"));
        Assert.Equal(1, await data.CountAsync("catalogCategories"));
    }
    [Fact]
    public async Task Parent_owner_and_category_cannot_be_injected_or_reparented()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var foreign = await data.CategoryAsync(data.Other, OtherOwner, "FOREIGN");
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.CreateFamilyAsync(data.Maker,
            new(Shop, foreign.Id, "Injected", "", []), Metadata("cross-category")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.SaveCategoryAsync(data.Maker,
            new(Shop with { OrganizationId = 999 }, null, 0, "INVALID", "Invalid owner"), Metadata("bad-parent")));
        Assert.Equal(0, await data.CountAsync("productFamilies"));
    }
    [Fact]
    public async Task Description_and_definition_corrections_preserve_old_versions_and_material_change_requires_replacement()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var created = await data.SkuAsync();
        var old = await data.RowAsync("variantDefinitions", created.CurrentDefinitionId!.Value.Value);
        var corrected = await data.Service.ReviseDefinitionAsync(data.Maker, new(created.Id, 1, Definition(label: "Karton")), Metadata("correct"));
        Assert.Equal(2, corrected.Version); Assert.Equal(2, corrected.DefinitionVersion);
        Assert.Equal(old, await data.RowAsync("variantDefinitions", created.CurrentDefinitionId.Value.Value));
        var before = await data.StateAsync();
        var invalid = await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.ReviseDefinitionAsync(data.Maker,
            new(created.Id, 2, Definition(10)), Metadata("material")));
        Assert.Equal("catalog_material_change", invalid.Code); Assert.Equal(before, await data.StateAsync());
        var replacement = await data.Service.CreateReplacementSkuAsync(data.Maker,
            new(created.Id, 2, "PENCIL-10", "pencil-base", Definition(10)), Metadata("replacement"));
        Assert.NotEqual(created.Id, replacement.Id); Assert.Equal(created.Id, replacement.PredecessorSkuId);
        Assert.Equal(1, replacement.Version); Assert.Equal(1, replacement.DefinitionVersion);
        Assert.True((await data.Service.SkuAsync(data.Maker, created.Id.Value, Audit)).Sku.Active);
        Assert.Equal(2, await data.CountAsync("catalogIdentifiers"));
    }
    [Theory]
    [InlineData("category")]
    [InlineData("family")]
    [InlineData("image")]
    [InlineData("definition")]
    public async Task Invalid_or_unavailable_authoring_input_leaves_no_business_effects(string kind)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var family = await data.FamilyAsync();
        var categoryId = (await data.Service.FamilyAsync(data.Maker, family.Id.Value, Audit)).CategoryId;
        var before = await data.StateAsync();
        await Assert.ThrowsAsync<DomainValidationException>(async () =>
        {
            if (kind == "category") await data.Service.SaveCategoryAsync(data.Maker, new(Shop, null, 0, "ABC", "Broken\ud800"), Metadata("invalid"));
            else if (kind == "family") await data.Service.EditFamilyAsync(data.Maker, new(family.Id, 1, categoryId, "Valid", "Bad\u0001", []), Metadata("invalid"));
            else await data.Service.CreateSkuAsync(data.Maker, new(family.Id, 1, "BAD", "base", kind == "image" ? Definition() with { ImageAssetIds = [new(5)] } : Definition(0)), Metadata("invalid"));
        });
        Assert.Equal(before, await data.StateAsync());
    }
    [Fact]
    public async Task Concurrent_normalized_code_and_exact_key_creates_keep_one_reserved_identity()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var family = await data.FamilyAsync();
        var input = new CatalogCreateSku(family.Id, 1, " pencils ", "base", Definition());
        var repeated = await Task.WhenAll(data.Service.CreateSkuAsync(data.Maker, input, Metadata("same-key")),
            data.Service.CreateSkuAsync(data.Maker, input, Metadata("same-key")));
        Assert.Equal(repeated[0], repeated[1]);
        var duplicate = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.CreateSkuAsync(data.Maker,
            input with { Code = "PENCILS" }, Metadata("other-key")));
        Assert.Equal("catalog_identifier_conflict", duplicate.Code);
        var reused = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.CreateSkuAsync(data.Maker,
            input with { Code = "PENCILS-OTHER" }, Metadata("same-key")));
        Assert.Equal("idempotency_key_reused", reused.Code);
        Assert.Equal(1, await data.CountAsync("skus")); Assert.Equal(1, await data.CountAsync("catalogIdentifierHistory"));
    }
    [Fact]
    public async Task Publication_requires_every_old_and_new_owner_and_retirement_withdraws_every_listing_atomically()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync(); await data.AssortmentsAsync();
        await data.Base.AddGrantAsync(data.Maker, 3001, OtherOwner, [Permissions.CatalogManage]);
        var listed = await data.Service.SetPublicationsAsync(data.Maker,
            new(sku.Id, 1, [new(501), new(503)], [new(new(501), 1), new(new(503), 1)]), Metadata("publish"));
        Assert.Equal(2, listed.Version);
        var view = await data.BuyerService.ReadPublicAsync(data.Buyer, (await data.Service.SkuAsync(data.Maker, sku.Id.Value, Audit)).Sku.PublicId.ToString("D"));
        Assert.NotNull(view); Assert.Equal(12, view.Definition.BaseUnitsPerPackage); Assert.Equal("Pensil", view.Family.Name);
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 3001L), U.Set("status", "revoked").Set("revokedAt", data.Clock.UtcNow.UtcDateTime).Inc("version", 1L));
        var before = await data.StateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.SetPublicationsAsync(data.Maker,
            new(sku.Id, 2, [new(501)], [new(new(501), 2), new(new(503), 2)]), Metadata("remove-other")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RetireSkuAsync(data.Maker, new(sku.Id, 2), Metadata("retire-denied")));
        Assert.Equal(before, await data.StateAsync());
        await data.Base.AddGrantAsync(data.Maker, 3002, OtherOwner, [Permissions.CatalogManage]);
        var retired = await data.Service.RetireSkuAsync(data.Maker, new(sku.Id, 2), Metadata("retire"));
        Assert.Equal(3, retired.Version); Assert.False((await data.Service.SkuAsync(data.Maker, sku.Id.Value, Audit)).Sku.Active);
        Assert.Equal(3L, (await data.RowAsync("catalogAssortments", 501))["version"].AsInt64);
        Assert.Equal(3L, (await data.RowAsync("catalogAssortments", 503))["version"].AsInt64);
        Assert.Empty((await data.BuyerService.QueryAsync(data.Buyer, 0, 25, null)).Items);
        Assert.Equal(1, await data.CountAsync("catalogIdentifiers"));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.SetPublicationsAsync(data.Maker,
            new(sku.Id, 3, [new(501)], [new(new(501), 3)]), Metadata("publish-retired")));
    }
    [Theory]
    [InlineData("missing-old")]
    [InlineData("duplicate")]
    [InlineData("stale")]
    public async Task Publication_version_selection_is_complete_unique_and_current(string defect)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync(); await data.AssortmentsAsync();
        await data.Service.SetPublicationsAsync(data.Maker, new(sku.Id, 1, [new(501)], [new(new(501), 1)]), Metadata("initial"));
        var before = await data.StateAsync();
        var versions = defect switch { "missing-old" => ImmutableArray.Create(new CatalogAssortmentVersion(new(502), 1)),
            "duplicate" => [new(new(501), 2), new(new(501), 2), new(new(502), 1)],
            _ => [new(new(501), 1), new(new(502), 1)] };
        Task Attempt() => data.Service.SetPublicationsAsync(data.Maker,
            new(sku.Id, 2, [new(502)], versions), Metadata("invalid-publication"));
        if (defect == "stale") Assert.Equal("catalog_publication_conflict", (await Assert.ThrowsAsync<PersistenceConflictException>(Attempt)).Code);
        else Assert.Equal("invalid_catalog_request", (await Assert.ThrowsAsync<DomainValidationException>(Attempt)).Code);
        Assert.Equal(before, await data.StateAsync());
    }
    [Theory]
    [InlineData("result")]
    [InlineData("receipt-schema")]
    [InlineData("audit-owner")]
    [InlineData("audit-link")]
    [InlineData("audit-grant-version")]
    public async Task Malformed_receipt_or_original_audit_fails_closed_for_lookup_and_replay(string defect)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var family = await data.FamilyAsync();
        var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument("operation", "catalog.family.create")).SingleAsync();
        var key = receipt["key"].AsString;
        if (defect == "result") await data.Rows("idempotencyRecords").UpdateOneAsync(new BsonDocument("_id", receipt["_id"]), U.Set("result.id", 999L));
        if (defect == "receipt-schema") await data.Rows("idempotencyRecords").UpdateOneAsync(new BsonDocument("_id", receipt["_id"]), U.Set("catalogAuthoring.schemaVersion", 2));
        if (defect == "audit-link") await data.Rows("idempotencyRecords").UpdateOneAsync(new BsonDocument("_id", receipt["_id"]), U.Set("catalogAuthoring.auditId", 999L));
        if (defect == "audit-grant-version") await data.Rows("auditEvents").UpdateOneAsync(new BsonDocument("_id", receipt["catalogAuthoring"]["auditId"]), U.Set("actorGrantVersion", 0L));
        if (defect == "audit-owner") await data.Rows("auditEvents").UpdateOneAsync(new BsonDocument("_id", receipt["catalogAuthoring"]["auditId"]), U.Set("owners", new BsonArray()));
        var before = await data.StateAsync();
        Assert.Equal("catalog_receipt_invalid", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.ReadOperationReceiptAsync(data.Maker, "catalog.family.create", key, RecoveryAudit))).Code);
        Assert.Equal(before, await data.StateAsync());
        var originalCategory = (await data.RowAsync("productFamilies", family.Id.Value))["categoryId"].AsInt64;
        Assert.Equal("catalog_receipt_invalid", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.CreateFamilyAsync(data.Maker, new(Shop, new(originalCategory), "Pensil", "Deskripsi", []), Metadata(key)))).Code);
        Assert.Equal(before, await data.StateAsync());
        Assert.Equal(1, await data.CountAsync("productFamilies"));
    }
    [Fact]
    public async Task Missing_or_cross_actor_receipt_is_unconfirmed_and_committed_receipt_requires_current_owner()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var family = await data.FamilyAsync();
        Assert.Null(await data.Service.ReadOperationReceiptAsync(data.Maker, "catalog.family.create", "missing", RecoveryAudit));
        Assert.Null(await data.Service.ReadOperationReceiptAsync(data.Other, "catalog.family.create", "family-ATK", RecoveryAudit));
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 3000L), U.Set("status", "revoked").Set("revokedAt", data.Clock.UtcNow.UtcDateTime).Inc("version", 1L));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadOperationReceiptAsync(data.Maker, "catalog.family.create", "family-ATK", RecoveryAudit));
        Assert.Equal(1, await data.CountAsync("productFamilies"));
    }
    [Theory]
    [InlineData("revision")]
    [InlineData("definition")]
    [InlineData("fence")]
    public async Task Exhausted_counters_never_partially_append_definition_or_receipt(string field)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync();
        if (field == "definition") await data.Rows("variantDefinitions").UpdateOneAsync(new BsonDocument("_id", sku.CurrentDefinitionId!.Value.Value),
            U.Set("version", long.MaxValue), new UpdateOptions { BypassDocumentValidation = true });
        else await data.Rows("skus").UpdateOneAsync(new BsonDocument("_id", sku.Id.Value), U.Set(field, long.MaxValue));
        var before = await data.StateAsync();
        Task Attempt() => data.Service.ReviseDefinitionAsync(data.Maker,
            new(sku.Id, field == "revision" ? long.MaxValue : 1, Definition(label: "New")), Metadata("exhausted"));
        if (field == "fence") Assert.Equal("catalog_revision_exhausted", (await Assert.ThrowsAsync<PersistenceConflictException>(Attempt)).Code);
        else Assert.Equal("catalog_revision_exhausted", (await Assert.ThrowsAsync<DomainValidationException>(Attempt)).Code);
        Assert.Equal(before, await data.StateAsync());
    }

    [Fact]
    public async Task Simultaneous_distinct_keys_for_same_canonical_code_commit_only_one_identity()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var family = await data.FamilyAsync();
        async Task<object> Attempt(string code, string key)
        {
            try { return await data.NewService().CreateSkuAsync(data.Maker, new(family.Id, 1, code, "base", Definition()), Metadata(key)); }
            catch (PersistenceConflictException error) { return error; }
        }
        var results = await Task.WhenAll(Attempt("same-code", "first"), Attempt(" SAME-CODE ", "second"));
        Assert.Single(results.OfType<CatalogMutationResult>());
        Assert.Equal("catalog_identifier_conflict", Assert.Single(results.OfType<PersistenceConflictException>()).Code);
        Assert.Equal(1, await data.CountAsync("skus")); Assert.Equal(1, await data.CountAsync("catalogIdentifiers"));
        Assert.Equal(1, await data.CountAsync("catalogIdentifierHistory")); Assert.Equal(1, await data.CountAsync("variantDefinitions"));
    }
    [Fact]
    public async Task Read_purpose_is_catalog_specific_and_no_private_values_enter_read_audit()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var family = await data.FamilyAsync();
        var before = await data.StateAsync();
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.FamilyAsync(data.Maker, family.Id.Value,
            new("access-review", "wrong-purpose")));
        Assert.Equal(before, await data.StateAsync());
        await data.Service.FamiliesAsync(data.Maker, Shop, "Pensil", 0, 25, Audit);
        var audit = await data.Rows("auditEvents").Find(new BsonDocument("kind", "catalog.read")).SingleAsync();
        Assert.Equal("catalog-authoring", audit["purpose"].AsString);
        Assert.False(audit.ToJson().Contains("Pensil", StringComparison.Ordinal));
        Assert.Equal(64, audit["selectionDigest"].AsString.Length);
    }


    [Theory]
    [InlineData("uuid")]
    [InlineData("owner")]
    [InlineData("definition-pointer")]
    [InlineData("family-owner")]
    public async Task Malformed_current_master_links_deny_before_audited_disclosure(string defect)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var sku = await data.SkuAsync();
        var collection = defect == "family-owner" ? "productFamilies" : "skus";
        var id = defect == "family-owner" ? sku.FamilyId!.Value.Value : sku.Id.Value;
        var update = defect switch
        {
            "uuid" => U.Set("publicId", Guid.NewGuid().ToString("D")),
            "owner" => U.Set("authoringOwner.organizationId", new BsonArray { 100L }),
            "definition-pointer" => U.Set("currentDefinitionId", 999L),
            _ => U.Set("authoringOwner.shopId", 201L).Set("authoringOwner.channelId", 203L)
        };
        await data.Rows(collection).UpdateOneAsync(new BsonDocument("_id", id), update, new UpdateOptions { BypassDocumentValidation = true });
        var before = await data.StateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.SkuAsync(data.Maker, sku.Id.Value, Audit));
        Assert.Equal(before, await data.StateAsync());
    }

}
