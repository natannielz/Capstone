using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Catalog;

public sealed partial class CatalogAuthoringService
{
    public async Task<CatalogMutationResult> SaveCategoryAsync(ActorContext actor, CatalogSaveCategory input, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input); CatalogAuthoringPolicy.Owner(input.Owner);
        var code = CatalogPolicy.NormalizeSkuCode(input.Code); var name = CatalogAuthoringPolicy.Name(input.Name);
        if (input.CategoryId is null && input.ExpectedVersion != 0) throw BadRequest();
        var categoryId = input.CategoryId ?? await ids.NextAsync("catalogCategories", cancellationToken);
        return await ExecuteAsync(actor, "catalog.category.save", input, metadata, async (context, _, ct) =>
        {
            RequireOwner(context, input.Owner); BsonDocument? before = null; CatalogCategory category;
            if (input.CategoryId is not null)
            {
                before = await MasterAsync(context, "catalogCategories", categoryId.Value, ct);
                if (RowOwner(before) != input.Owner) throw CatalogRepository.Invalid();
                CatalogRepository.Expected(before, input.ExpectedVersion);
                if (before["code"].AsString != code) throw new DomainValidationException("catalog_category_code_immutable", "Category code is permanent");
                category = CatalogRepository.Category(before) with { Name = name, Revision = CatalogRepository.Next(before["revision"].AsInt64) };
            }
            else
            {
                if (await repository.Rows("catalogCategories").Find(context.Tx, CatalogRepository.OwnerFilter("authoringOwner", input.Owner) & F.Eq("code", code)).AnyAsync(ct))
                    throw new PersistenceConflictException("catalog_category_conflict", "Category code is already used by this owner");
                category = new(categoryId, new(input.Owner.OrganizationId!.Value), input.Owner, code, name, true, 1);
            }
            var after = CatalogRepository.Category(category);
            await SaveAsync(context, "catalogCategories", before, after, ct);
            return new(CatalogRepository.Snapshots(category: before), CatalogRepository.Snapshots(category: after),
                new("category", category.Id, category.Revision, null, null, null, null, Now()));
        }, cancellationToken);
    }
    public async Task<CatalogMutationResult> CreateFamilyAsync(ActorContext actor, CatalogCreateFamily input, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input); CatalogAuthoringPolicy.Owner(input.Owner); CatalogAuthoringPolicy.Images(input.ImageAssetIds);
        var name = CatalogAuthoringPolicy.Name(input.Name); var description = CatalogAuthoringPolicy.Description(input.Description);
        var familyId = await ids.NextAsync("productFamilies", cancellationToken); var publicId = Guid.NewGuid();
        return await ExecuteAsync(actor, "catalog.family.create", input, metadata, async (context, _, ct) =>
        {
            RequireOwner(context, input.Owner); var category = await MasterAsync(context, "catalogCategories", input.CategoryId.Value, ct);
            if (RowOwner(category) != input.Owner) throw CatalogRepository.Invalid(); RequireActive(category);
            var family = new ProductFamily(familyId, new(input.Owner.OrganizationId!.Value), input.Owner, publicId,
                input.CategoryId, category["code"].AsString, name, description, [], true, 1);
            var after = CatalogRepository.Family(family); await SaveAsync(context, "productFamilies", null, after, ct);
            return new(CatalogRepository.Snapshots(category: category), CatalogRepository.Snapshots(category: category, family: after),
                new("family", familyId, 1, familyId, null, null, null, Now()));
        }, cancellationToken);
    }
    public Task<CatalogMutationResult> EditFamilyAsync(ActorContext actor, CatalogEditFamily input, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input); CatalogAuthoringPolicy.Images(input.ImageAssetIds);
        var name = CatalogAuthoringPolicy.Name(input.Name); var description = CatalogAuthoringPolicy.Description(input.Description);
        return ExecuteAsync(actor, "catalog.family.edit", input, metadata, async (context, _, ct) =>
        {
            var graph = await FamilyGraphAsync(context, input.FamilyId.Value, ct); CatalogRepository.Expected(graph.Family, input.ExpectedVersion);
            var category = input.CategoryId.Value == graph.Category["_id"].AsInt64 ? graph.Category : await MasterAsync(context, "catalogCategories", input.CategoryId.Value, ct);
            if (RowOwner(category) != RowOwner(graph.Family) || category["organizationId"] != graph.Family["organizationId"]) throw CatalogRepository.Invalid();
            RequireActive(category);
            var family = CatalogRepository.Family(graph.Family) with { CategoryId = input.CategoryId, CategoryCode = category["code"].AsString,
                Name = name, Description = description, Revision = CatalogRepository.Next(graph.Family["revision"].AsInt64) };
            var after = CatalogRepository.Family(family); await SaveAsync(context, "productFamilies", graph.Family, after, ct);
            return new(CatalogRepository.Snapshots(category: graph.Category, family: graph.Family), CatalogRepository.Snapshots(category: category, family: after),
                new("family", family.Id, family.Revision, family.Id, null, null, null, Now()));
        }, cancellationToken);
    }
    public async Task<CatalogMutationResult> CreateSkuAsync(ActorContext actor, CatalogCreateSku input, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input); ValidateImages(input.Definition);
        var skuId = await ids.NextAsync("skus", cancellationToken); var definitionId = await ids.NextAsync("variantDefinitions", cancellationToken);
        var identifierId = await ids.NextAsync("catalogIdentifiers", cancellationToken); var historyId = await ids.NextAsync("catalogIdentifierHistory", cancellationToken); var uuid = Guid.NewGuid();
        return await ExecuteAsync(actor, "catalog.sku.create", input, metadata, async (context, auditId, ct) =>
        {
            var graph = await FamilyGraphAsync(context, input.FamilyId.Value, ct); CatalogRepository.Expected(graph.Family, input.ExpectedFamilyVersion);
            RequireActive(graph.Family); RequireActive(graph.Category); var now = Now();
            var change = CatalogPolicy.CreateSku(skuId, new(graph.Family["organizationId"].AsInt64), input.FamilyId, uuid, input.Code, input.PhysicalItemKey, definitionId, input.Definition, now);
            await RequireUnusedCodeAsync(context, change.Sku, ct);
            var sku = CatalogRepository.Sku(change.Sku, RowOwner(graph.Family), change.Definition); var definition = CatalogRepository.Definition(change.Definition);
            await InsertSkuAsync(context, sku, definition, identifierId.Value, historyId.Value, auditId, now, ct);
            return new(CatalogRepository.Snapshots(graph.Category, graph.Family), CatalogRepository.Snapshots(graph.Category, graph.Family, sku, definition), Result(change.Sku, change.Definition, now));
        }, cancellationToken);
    }
    public async Task<CatalogMutationResult> ReviseDefinitionAsync(ActorContext actor, CatalogReviseDefinition input, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input); ValidateImages(input.Definition); var definitionId = await ids.NextAsync("variantDefinitions", cancellationToken);
        return await ExecuteAsync(actor, "catalog.sku.revise", input, metadata, async (context, _, ct) =>
        {
            var graph = await SkuGraphAsync(context, input.SkuId.Value, ct); CatalogRepository.Expected(graph.Sku, input.ExpectedVersion); var now = Now();
            var change = CatalogPolicy.ReviseDefinition(CatalogRepository.Sku(graph.Sku), CatalogRepository.Definition(graph.Definition), input.ExpectedVersion, definitionId, input.Definition, now);
            var definition = CatalogRepository.Definition(change.Definition);
            var sku = CatalogRepository.Sku(change.Sku, RowOwner(graph.Sku), change.Definition, graph.Sku["catalogListings"].AsBsonArray);
            await repository.InsertAsync(context.Tx, "variantDefinitions", definition, ct); await SaveAsync(context, "skus", graph.Sku, sku, ct);
            return new(CatalogRepository.Snapshots(graph.Category, graph.Family, graph.Sku, graph.Definition), CatalogRepository.Snapshots(graph.Category, graph.Family, sku, definition), Result(change.Sku, change.Definition, now));
        }, cancellationToken);
    }
    public async Task<CatalogMutationResult> CreateReplacementSkuAsync(ActorContext actor, CatalogCreateReplacement input, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input); ValidateImages(input.Definition);
        var skuId = await ids.NextAsync("skus", cancellationToken); var definitionId = await ids.NextAsync("variantDefinitions", cancellationToken);
        var identifierId = await ids.NextAsync("catalogIdentifiers", cancellationToken); var historyId = await ids.NextAsync("catalogIdentifierHistory", cancellationToken); var uuid = Guid.NewGuid();
        return await ExecuteAsync(actor, "catalog.sku.replace", input, metadata, async (context, auditId, ct) =>
        {
            var graph = await SkuGraphAsync(context, input.PredecessorSkuId.Value, ct); CatalogRepository.Expected(graph.Sku, input.ExpectedVersion);
            RequireActive(graph.Sku); RequireActive(graph.Family); RequireActive(graph.Category); var now = Now();
            var change = CatalogPolicy.CreateReplacementSku(CatalogRepository.Sku(graph.Sku), CatalogRepository.Definition(graph.Definition), input.ExpectedVersion,
                skuId, uuid, input.Code, input.PhysicalItemKey, definitionId, input.Definition, now);
            await RequireUnusedCodeAsync(context, change.Sku, ct);
            var sku = CatalogRepository.Sku(change.Sku, RowOwner(graph.Sku), change.Definition); var definition = CatalogRepository.Definition(change.Definition);
            await InsertSkuAsync(context, sku, definition, identifierId.Value, historyId.Value, auditId, now, ct);
            return new(CatalogRepository.Snapshots(graph.Category, graph.Family, graph.Sku, graph.Definition), CatalogRepository.Snapshots(graph.Category, graph.Family, sku, definition), Result(change.Sku, change.Definition, now));
        }, cancellationToken);
    }
    public Task<CatalogMutationResult> RetireSkuAsync(ActorContext actor, CatalogRetireSku input, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ExecuteAsync(actor, "catalog.sku.retire", input, metadata, async (context, _, ct) =>
        {
            var graph = await SkuGraphAsync(context, input.SkuId.Value, ct); CatalogRepository.Expected(graph.Sku, input.ExpectedVersion); RequireActive(graph.Sku);
            var previous = await LoadListingsAsync(context, graph.Sku, [], ct);
            foreach (var assortment in previous) RequireOwner(context, CatalogRepository.Owner(assortment["ownerScope"].AsBsonDocument));
            var updated = await UpdateAssortmentsAsync(context, previous, previous.Select(row => row["_id"].AsInt64).ToHashSet(), ct);
            var value = CatalogRepository.Sku(graph.Sku) with { Active = false, Revision = CatalogRepository.Next(graph.Sku["revision"].AsInt64) };
            var definition = CatalogRepository.Definition(graph.Definition); var after = CatalogRepository.Sku(value, RowOwner(graph.Sku), definition);
            await SaveAsync(context, "skus", graph.Sku, after, ct);
            return new(CatalogRepository.Snapshots(graph.Category, graph.Family, graph.Sku, graph.Definition, previous), CatalogRepository.Snapshots(graph.Category, graph.Family, after, graph.Definition, updated), Result(value, definition, Now()));
        }, cancellationToken);
    }
    public Task<CatalogMutationResult> SetPublicationsAsync(ActorContext actor, CatalogSetPublications input, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.AssortmentIds.IsDefault || input.AssortmentIds.Length > 128 || input.AssortmentIds.Any(id => id.Value <= 0) ||
            input.AssortmentIds.Distinct().Count() != input.AssortmentIds.Length || input.ExpectedAssortments.IsDefault || input.ExpectedAssortments.Length > 256 ||
            input.ExpectedAssortments.Any(item => item is null || item.Id.Value <= 0 || item.Version <= 0) || input.ExpectedAssortments.Select(item => item.Id).Distinct().Count() != input.ExpectedAssortments.Length) throw BadRequest();
        return ExecuteAsync(actor, "catalog.sku.publish", input, metadata, async (context, _, ct) =>
        {
            var graph = await SkuGraphAsync(context, input.SkuId.Value, ct); CatalogRepository.Expected(graph.Sku, input.ExpectedVersion);
            RequireActive(graph.Sku); RequireActive(graph.Family); RequireActive(graph.Category);
            var desired = input.AssortmentIds.Select(id => id.Value).ToHashSet();
            var old = graph.Sku["catalogListings"].AsBsonArray.Select(item => item["assortmentId"].AsInt64).ToHashSet();
            var previous = await LoadListingsAsync(context, graph.Sku, desired, ct);
            if (!previous.Select(row => row["_id"].AsInt64).ToHashSet().SetEquals(input.ExpectedAssortments.Select(item => item.Id.Value))) throw BadRequest();
            foreach (var assortment in previous)
            {
                RequireOwner(context, CatalogRepository.Owner(assortment["ownerScope"].AsBsonDocument));
                if (assortment["version"].AsInt64 != input.ExpectedAssortments.Single(item => item.Id.Value == assortment["_id"].AsInt64).Version)
                    throw new PersistenceConflictException("catalog_publication_conflict", "Publication options changed; reload and review");
                if (desired.Contains(assortment["_id"].AsInt64) && assortment["status"].AsString != "active") throw CatalogRepository.Invalid();
            }
            var changed = old.ToHashSet(); changed.SymmetricExceptWith(desired);
            var updated = await UpdateAssortmentsAsync(context, previous, changed, ct);
            var listings = new BsonArray(previous.Where(row => desired.Contains(row["_id"].AsInt64)).Select(row => new BsonDocument
                { { "assortmentId", row["_id"] }, { "ownerScope", CatalogRepository.Owner(CatalogRepository.Owner(row["ownerScope"].AsBsonDocument)) } }));
            var value = CatalogRepository.Sku(graph.Sku) with { Revision = CatalogRepository.Next(graph.Sku["revision"].AsInt64) };
            var definition = CatalogRepository.Definition(graph.Definition); var after = CatalogRepository.Sku(value, RowOwner(graph.Sku), definition, listings);
            await SaveAsync(context, "skus", graph.Sku, after, ct);
            return new(CatalogRepository.Snapshots(graph.Category, graph.Family, graph.Sku, graph.Definition, previous), CatalogRepository.Snapshots(graph.Category, graph.Family, after, graph.Definition, updated), Result(value, definition, Now()));
        }, cancellationToken);
    }
    private async Task<List<BsonDocument>> LoadListingsAsync(Context context, BsonDocument sku, IEnumerable<long> additional, CancellationToken ct)
    {
        var listings = sku["catalogListings"].AsBsonArray; var selected = listings.Select(item => item["assortmentId"].AsInt64).Concat(additional).Distinct().Order().ToArray();
        if (selected.Length > 256) throw BadRequest(); var result = new List<BsonDocument>();
        foreach (var id in selected)
        {
            var row = CatalogRepository.Assortment(await repository.Rows("catalogAssortments").Find(context.Tx, F.Eq("_id", id)).FirstOrDefaultAsync(ct));
            var owner = CatalogRepository.Owner(row["ownerScope"].AsBsonDocument);
            if (owner.OrganizationId != sku["organizationId"].AsInt64 || listings.Any(item => item["assortmentId"].AsInt64 == id && CatalogRepository.Owner(item["ownerScope"].AsBsonDocument) != owner)) throw CatalogRepository.Invalid();
            context.Head("catalogAssortments", row); result.Add(row);
        }
        return result;
    }
    private async Task<List<BsonDocument>> UpdateAssortmentsAsync(Context context, List<BsonDocument> previous, HashSet<long> changed, CancellationToken ct)
    {
        var result = new List<BsonDocument>();
        // Preflight every counter before issuing a write, including later destinations.
        foreach (var row in previous.Where(row => changed.Contains(row["_id"].AsInt64)))
        {
            _ = CatalogRepository.Next(row["version"].AsInt64);
            if (row["fence"].AsInt64 >= long.MaxValue - 1) throw new PersistenceConflictException("catalog_revision_exhausted", "Catalog fence is exhausted");
        }
        foreach (var row in previous)
        {
            var after = row.DeepClone().AsBsonDocument;
            if (changed.Contains(row["_id"].AsInt64))
            {
                after["version"] = CatalogRepository.Next(row["version"].AsInt64); after["fence"] = row["fence"].AsInt64 + 1;
                var change = await repository.Rows("catalogAssortments").ReplaceOneAsync(context.Tx, CatalogRepository.Exact("catalogAssortments", row), after, cancellationToken: ct);
                if (change.ModifiedCount != 1) throw new PersistenceConflictException("catalog_publication_conflict", "Publication options changed; reload and review");
            }
            context.Head("catalogAssortments", after); result.Add(after);
        }
        return result;
    }
    private async Task RequireUnusedCodeAsync(Context context, Sku sku, CancellationToken ct)
    {
        if (await repository.Rows("catalogIdentifiers").Find(context.Tx, F.Eq("organizationId", sku.OrganizationId.Value) & F.Eq("namespace", "internal") & F.Eq("canonicalValue", sku.Code)).AnyAsync(ct) ||
            await repository.Rows("skus").Find(context.Tx, F.Eq("organizationId", sku.OrganizationId.Value) & F.Eq("sku", sku.Code)).AnyAsync(ct))
            throw new PersistenceConflictException("catalog_identifier_conflict", "SKU code is permanently reserved");
    }
    private async Task InsertSkuAsync(Context context, BsonDocument sku, BsonDocument definition, long identifierId, long historyId, long auditId, DateTimeOffset now, CancellationToken ct)
    {
        await repository.InsertAsync(context.Tx, "variantDefinitions", definition, ct); await SaveAsync(context, "skus", null, sku, ct);
        var assignment = new BsonDocument
        {
            { "_id", identifierId }, { "schemaVersion", 1 }, { "organizationId", sku["organizationId"] }, { "skuId", sku["_id"] },
            { "kind", "skuCode" }, { "namespace", "internal" }, { "rawValue", sku["sku"] }, { "canonicalValue", sku["sku"] },
            { "isPrimary", true }, { "effectiveAt", now.UtcDateTime }, { "retiredAt", BsonNull.Value }, { "revision", 1L },
            { "provenanceReference", "catalog.command:" + auditId }, { "auditId", auditId }
        };
        await repository.InsertAsync(context.Tx, "catalogIdentifiers", assignment, ct);
        await repository.InsertAsync(context.Tx, "catalogIdentifierHistory", new BsonDocument
        {
            { "_id", historyId }, { "schemaVersion", 1 }, { "identifierId", identifierId }, { "organizationId", sku["organizationId"] },
            { "skuId", sku["_id"] }, { "assignmentVersion", 1L }, { "event", "assigned" }, { "actorId", context.Actor.UserId },
            { "createdAt", now.UtcDateTime }, { "auditId", auditId }, { "assignment", assignment.DeepClone() }
        }, ct);
    }
    private async Task SaveAsync(Context context, string collection, BsonDocument? before, BsonDocument after, CancellationToken ct)
    {
        if (before is null) await repository.InsertAsync(context.Tx, collection, after, ct);
        else await repository.ReplaceAsync(context.Tx, collection, before, after, ct);
        context.Head(collection, after);
    }
    private static CatalogMutationResult Result(Sku sku, VariantDefinitionVersion definition, DateTimeOffset at) =>
        new("sku", sku.Id, sku.Revision, sku.FamilyId, definition.Id, definition.Version, sku.PredecessorSkuId, at);
    private static void RequireActive(BsonDocument row)
    {
        if (!row["active"].AsBoolean) throw new DomainValidationException("catalog_resource_inactive", "This catalog item is retired or inactive");
    }
    private static void ValidateImages(DefinitionInput definition)
    {
        if (definition is null) throw BadRequest(); CatalogAuthoringPolicy.Images(definition.ImageAssetIds);
    }
    private static DomainValidationException BadRequest() => new("invalid_catalog_request", "Catalog input is invalid or incomplete");
}
