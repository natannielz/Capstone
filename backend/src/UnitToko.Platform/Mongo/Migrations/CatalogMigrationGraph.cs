using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo.Migrations;

// Historical provenance is checked independently of current staff authority. No legacy
// relationship is inferred, and mutable historical snapshots are never overwritten.
internal static class CatalogMigrationGraph
{
    internal static async Task ValidateAsync(IMongoDatabase database, Dictionary<string, Dictionary<long, BsonDocument>> rows, CancellationToken token)
    {
        var categories = rows["catalogCategories"]; var families = rows["productFamilies"]; var skus = rows["skus"];
        var definitions = rows["variantDefinitions"]; var identifiers = rows["catalogIdentifiers"]; var histories = rows["catalogIdentifierHistory"];
        var external = new Dictionary<(string Collection, long Id), BsonDocument>();
        async Task<BsonDocument> Exact(string collection, long id)
        {
            if (external.TryGetValue((collection, id), out var cached)) return cached;
            var found = await database.GetCollection<BsonDocument>(collection).WithReadConcern(ReadConcern.Majority)
                .Find(new BsonDocument("_id", id)).SingleOrDefaultAsync(token);
            if (found is null || !Positive(found, "_id") || found["_id"].AsInt64 != id || !Schema(found)) throw Invalid(collection, "typed references");
            external[(collection, id)] = found; return found;
        }
        async Task Scope(long id, string kind, long? parent)
        {
            var node = await Exact("authorizationScopes", id);
            if (node.GetValue("kind", BsonNull.Value) != kind || !Status(node) || !Positive(node, "version") || !Nonnegative(node, "fence") ||
                (parent.HasValue ? !Positive(node, "parentId") || node["parentId"].AsInt64 != parent : !node.GetValue("parentId", BsonNull.Value).IsBsonNull))
                throw Invalid("authorizationScopes", "catalog ownership hierarchy");
        }
        async Task Owner(BsonDocument owner)
        {
            await Scope(owner["organizationId"].AsInt64, "fulfillmentOrganization", null);
            await Scope(owner["shopId"].AsInt64, "shop", owner["organizationId"].AsInt64);
            await Scope(owner["channelId"].AsInt64, "channel", owner["organizationId"].AsInt64);
            if (owner["kind"] == "fulfillmentWarehouse") await Scope(owner["warehouseId"].AsInt64, "warehouse", owner["shopId"].AsInt64);
        }
        foreach (var category in categories.Values) await Owner(category["authoringOwner"].AsBsonDocument);
        foreach (var family in families.Values)
        {
            var category = Required(categories, family["categoryId"], "productFamilies");
            if (!SharedOwner(family, category) || family["categoryCode"] != category["code"]) throw Invalid("productFamilies", "category ownership");
        }
        foreach (var definition in definitions.Values) _ = Required(skus, definition["skuId"], "variantDefinitions");
        var definitionsBySku = definitions.Values.GroupBy(row => row["skuId"].AsInt64).ToDictionary(group => group.Key, group => group.OrderBy(row => row["version"].AsInt64).ToArray());
        foreach (var sku in skus.Values)
        {
            var family = Required(families, sku["familyId"], "skus");
            if (!SharedOwner(sku, family)) throw Invalid("skus", "family ownership");
            if (!definitionsBySku.TryGetValue(sku["_id"].AsInt64, out var versions) || versions.Length == 0) throw Invalid("skus", "definition chain");
            var first = versions[0];
            for (var index = 0; index < versions.Length; index++)
            {
                var definition = versions[index];
                if (definition["version"].AsInt64 != index + 1L || !SameFields(first, definition, "packageKind", "baseUnitCode", "baseUnitsPerPackage", "attributes") ||
                    index > 0 && definition["effectiveAt"].AsBsonDateTime.MillisecondsSinceEpoch < versions[index - 1]["effectiveAt"].AsBsonDateTime.MillisecondsSinceEpoch)
                    throw Invalid("variantDefinitions", "immutable definition chain");
            }
            var current = versions[^1];
            if (sku["currentDefinitionId"] != current["_id"] || sku["displayName"] != sku["sku"].AsString + " · " + current["packageLabel"].AsString)
                throw Invalid("skus", "current definition pointer/display");
            var visited = new HashSet<long> { sku["_id"].AsInt64 }; var cursor = sku;
            while (!cursor["predecessorSkuId"].IsBsonNull)
            {
                cursor = Required(skus, cursor["predecessorSkuId"], "skus");
                if (!visited.Add(cursor["_id"].AsInt64) || cursor["familyId"] != sku["familyId"] || !SharedOwner(cursor, sku)) throw Invalid("skus", "replacement predecessor chain");
            }
            foreach (var listing in sku["catalogListings"].AsBsonArray)
            {
                var assortment = await Exact("catalogAssortments", listing["assortmentId"].AsInt64);
                if (!ValidAssortment(assortment) || !CatalogSchemas.SameOwner(listing["ownerScope"].AsBsonDocument, assortment["ownerScope"].AsBsonDocument))
                    throw Invalid("skus", "publication assortment ownership");
                await Owner(assortment["ownerScope"].AsBsonDocument);
            }
        }
        var assignmentsByIdentifier = histories.Values.GroupBy(row => row["identifierId"].AsInt64).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var history in histories.Values) _ = Required(identifiers, history["identifierId"], "catalogIdentifierHistory");
        foreach (var sku in skus.Values)
            if (identifiers.Values.Count(row => row["skuId"] == sku["_id"]) != 1) throw Invalid("catalogIdentifiers", "one permanent primary reservation per SKU");
        foreach (var identifier in identifiers.Values)
        {
            var sku = Required(skus, identifier["skuId"], "catalogIdentifiers");
            var first = definitionsBySku[sku["_id"].AsInt64][0];
            if (identifier["organizationId"] != sku["organizationId"] || identifier["canonicalValue"] != sku["sku"] || identifier["effectiveAt"] != first["effectiveAt"] ||
                !assignmentsByIdentifier.TryGetValue(identifier["_id"].AsInt64, out var assignments) || assignments.Length != 1 ||
                !CatalogSchemas.SemanticEquals(assignments[0]["assignment"], identifier)) throw Invalid("catalogIdentifiers", "original assignment history");
            var history = assignments[0]; var actor = await Exact("identities", history["actorId"].AsInt64);
            if (!Status(actor) || !Positive(actor, "grantVersion") || !Nonnegative(actor, "fence") ||
                !actor.TryGetValue("issuer", out var issuer) || !issuer.IsString || string.IsNullOrWhiteSpace(issuer.AsString) ||
                !actor.TryGetValue("subject", out var subject) || !subject.IsString || string.IsNullOrWhiteSpace(subject.AsString)) throw Invalid("identities", "historical catalog actor");
            var audit = await Exact("auditEvents", identifier["auditId"].AsInt64);
            if (!ValidAssignmentAudit(audit, sku, first, history, actor)) throw Invalid("auditEvents", "original catalog command provenance");
            // Historical snapshots can outlive later mutations. Only immutable identities
            // and stored immutable definitions are linked to current data.
            foreach (var side in new[] { "before", "after" })
            {
                var snapshot = audit[side].AsBsonDocument;
                if (snapshot["family"].IsBsonDocument)
                {
                    var pastFamily = snapshot["family"].AsBsonDocument; var liveFamily = Required(families, pastFamily["_id"], "auditEvents");
                    if (!SameFields(pastFamily, liveFamily, "organizationId", "authoringOwner", "publicId", "publicUuid")) throw Invalid("auditEvents", "historical family identity");
                    var pastCategory = Required(categories, pastFamily["categoryId"], "auditEvents");
                    if (!SharedOwner(pastFamily, pastCategory) || pastFamily["categoryCode"] != pastCategory["code"]) throw Invalid("auditEvents", "historical family category identity");
                }
                if (snapshot["category"].IsBsonDocument)
                {
                    var pastCategory = snapshot["category"].AsBsonDocument; var liveCategory = Required(categories, pastCategory["_id"], "auditEvents");
                    if (!SameFields(pastCategory, liveCategory, "organizationId", "authoringOwner", "code")) throw Invalid("auditEvents", "historical category identity");
                }
                if (snapshot["sku"].IsBsonDocument)
                {
                    var pastSku = snapshot["sku"].AsBsonDocument; var liveSku = Required(skus, pastSku["_id"], "auditEvents");
                    if (!SameFields(pastSku, liveSku, "organizationId", "authoringOwner", "familyId", "publicId", "publicUuid", "sku", "physicalItemKey", "predecessorSkuId")) throw Invalid("auditEvents", "historical SKU identity");
                    foreach (var listing in pastSku["catalogListings"].AsBsonArray)
                    {
                        var current = await Exact("catalogAssortments", listing["assortmentId"].AsInt64);
                        if (!ValidAssortment(current) || !CatalogSchemas.SameOwner(listing["ownerScope"].AsBsonDocument, current["ownerScope"].AsBsonDocument))
                            throw Invalid("auditEvents", "historical SKU publication identity");
                        await Owner(current["ownerScope"].AsBsonDocument);
                    }
                }
                if (snapshot["definition"].IsBsonDocument && !CatalogSchemas.SemanticEquals(snapshot["definition"], Required(definitions, snapshot["definition"]["_id"], "auditEvents")))
                    throw Invalid("auditEvents", "historical immutable definition");
                foreach (var assortment in snapshot["assortments"].AsBsonArray)
                {
                    var current = await Exact("catalogAssortments", assortment["_id"].AsInt64);
                    if (!ValidAssortment(current) || !CatalogSchemas.SameOwner(assortment["ownerScope"].AsBsonDocument, current["ownerScope"].AsBsonDocument) || current["version"].AsInt64 < assortment["version"].AsInt64)
                        throw Invalid("auditEvents", "historical assortment identity");
                }
            }
        }
    }

    private static bool ValidAssignmentAudit(BsonDocument audit, BsonDocument sku, BsonDocument first, BsonDocument history, BsonDocument actor)
    {
        if (!ExactFields(audit, "_id", "schemaVersion", "kind", "actorId", "actorGrantVersion", "operation", "bodyHash", "reason", "correlationId", "createdAt", "owners", "result", "before", "after") ||
            !Schema(audit) || !Positive(audit, "_id") || !Positive(audit, "actorId") || !Positive(audit, "actorGrantVersion") || audit["kind"] != "catalog.command" ||
            audit["actorId"] != history["actorId"] || actor["grantVersion"].AsInt64 < audit["actorGrantVersion"].AsInt64 || !CatalogSchemas.ValidDate(audit["createdAt"]) || audit["createdAt"] != history["createdAt"] ||
            !Text(audit["reason"], 1000) || !Text(audit["correlationId"], 128) || !audit["bodyHash"].IsString || audit["bodyHash"].AsString.Length != 64 || audit["bodyHash"].AsString.Any(c => !"0123456789abcdef".Contains(c)) ||
            !audit["owners"].IsBsonArray || audit["owners"].AsBsonArray.Count != 1 || !CatalogSchemas.SemanticEquals(audit["owners"][0], sku["authoringOwner"]) ||
            !Snapshot(audit["before"]) || !Snapshot(audit["after"])) return false;
        var after = audit["after"].AsBsonDocument; var before = audit["before"].AsBsonDocument; var result = audit["result"];
        if (!after["sku"].IsBsonDocument || !after["definition"].IsBsonDocument || !after["family"].IsBsonDocument || !before["family"].IsBsonDocument || before["family"]["_id"] != after["family"]["_id"] ||
            after["sku"]["_id"] != sku["_id"] || after["sku"]["revision"] != 1L || after["sku"]["active"] != true || after["sku"]["catalogListings"].AsBsonArray.Count != 0 ||
            after["family"]["_id"] != sku["familyId"] || !CatalogSchemas.SemanticEquals(after["definition"], first) ||
            !result.IsBsonDocument || !ExactFields(result.AsBsonDocument, "kind", "id", "version", "familyId", "currentDefinitionId", "definitionVersion", "predecessorSkuId", "recordedAt") ||
            result["kind"] != "sku" || !Positive(result.AsBsonDocument, "id") || !Positive(result.AsBsonDocument, "version") || !Positive(result.AsBsonDocument, "familyId") || !Positive(result.AsBsonDocument, "currentDefinitionId") || !Positive(result.AsBsonDocument, "definitionVersion") ||
            result["id"] != sku["_id"] || result["version"] != 1L || result["familyId"] != sku["familyId"] || result["currentDefinitionId"] != first["_id"] || result["definitionVersion"] != 1L ||
            !CatalogSchemas.SemanticEquals(result["predecessorSkuId"], sku["predecessorSkuId"]) || result["recordedAt"] != audit["createdAt"]) return false;
        return sku["predecessorSkuId"].IsBsonNull
            ? audit["operation"] == "catalog.sku.create" && before["sku"].IsBsonNull && before["definition"].IsBsonNull
            : audit["operation"] == "catalog.sku.replace" && before["sku"].IsBsonDocument && before["sku"]["active"] == true && before["definition"].IsBsonDocument && before["sku"]["_id"] == sku["predecessorSkuId"] &&
                first["effectiveAt"].AsBsonDateTime.MillisecondsSinceEpoch >= before["definition"]["effectiveAt"].AsBsonDateTime.MillisecondsSinceEpoch;
    }
    private static bool Snapshot(BsonValue value)
    {
        if (!value.IsBsonDocument || !ExactFields(value.AsBsonDocument, "category", "family", "sku", "definition", "assortments")) return false;
        foreach (var (field, collection) in new[] { ("category", "catalogCategories"), ("family", "productFamilies"), ("sku", "skus"), ("definition", "variantDefinitions") })
            if (!value[field].IsBsonNull && (!value[field].IsBsonDocument || !CatalogSchemas.IsValid(collection, value[field].AsBsonDocument))) return false;
        if (!value["assortments"].IsBsonArray || value["assortments"].AsBsonArray.Count > 256) return false;
        long previous = 0;
        foreach (var row in value["assortments"].AsBsonArray)
        {
            if (!row.IsBsonDocument || !ValidAssortment(row.AsBsonDocument) || row["_id"].AsInt64 <= previous) return false;
            previous = row["_id"].AsInt64;
        }
        if (value["sku"].IsBsonDocument && value["definition"].IsBsonDocument &&
            (value["sku"]["currentDefinitionId"] != value["definition"]["_id"] || value["definition"]["skuId"] != value["sku"]["_id"] || value["sku"]["displayName"] != value["sku"]["sku"].AsString + " · " + value["definition"]["packageLabel"].AsString)) return false;
        if (value["sku"].IsBsonDocument && value["family"].IsBsonDocument &&
            (value["sku"]["familyId"] != value["family"]["_id"] || !SharedOwner(value["sku"].AsBsonDocument, value["family"].AsBsonDocument))) return false;
        return true;
    }
    private static bool ValidAssortment(BsonDocument row) => ExactFields(row, "_id", "schemaVersion", "ownerScope", "status", "version", "fence") && Schema(row) &&
        Positive(row, "_id") && Positive(row, "version") && Nonnegative(row, "fence") && Status(row) && ValidPublicationOwner(row["ownerScope"]);
    private static bool ValidPublicationOwner(BsonValue value)
    {
        if (!value.IsBsonDocument) return false;
        var owner = value.AsBsonDocument;
        return owner.Names.Distinct().Count() == owner.ElementCount && owner.Names.All(name => new[] { "kind", "organizationId", "shopId", "channelId", "warehouseId", "branchId", "divisionId" }.Contains(name)) &&
            Positive(owner, "organizationId") && Positive(owner, "shopId") && Positive(owner, "channelId") && owner.GetValue("branchId", BsonNull.Value).IsBsonNull && owner.GetValue("divisionId", BsonNull.Value).IsBsonNull &&
            (owner.GetValue("kind", BsonNull.Value) == "fulfillmentShop" && owner.GetValue("warehouseId", BsonNull.Value).IsBsonNull || owner.GetValue("kind", BsonNull.Value) == "fulfillmentWarehouse" && Positive(owner, "warehouseId"));
    }
    private static BsonDocument Required(Dictionary<long, BsonDocument> records, BsonValue id, string collection) => records.TryGetValue(id.AsInt64, out var row) ? row : throw Invalid(collection, "catalog graph references");
    private static bool SharedOwner(BsonDocument left, BsonDocument right) => left["organizationId"] == right["organizationId"] && CatalogSchemas.SameOwner(left["authoringOwner"].AsBsonDocument, right["authoringOwner"].AsBsonDocument);
    private static bool SameFields(BsonDocument left, BsonDocument right, params string[] fields) => fields.All(field => CatalogSchemas.SemanticEquals(left[field], right[field]));
    private static bool ExactFields(BsonDocument row, params string[] fields) => row.ElementCount == fields.Length && row.Names.Distinct().Count() == fields.Length && fields.All(row.Contains);
    private static bool Schema(BsonDocument row) => row.TryGetValue("schemaVersion", out var value) && value.IsInt32 && value.AsInt32 == 1;
    private static bool Positive(BsonDocument row, string name) => row.TryGetValue(name, out var value) && value.IsInt64 && value.AsInt64 > 0;
    private static bool Nonnegative(BsonDocument row, string name) => row.TryGetValue(name, out var value) && value.IsInt64 && value.AsInt64 >= 0;
    private static bool Status(BsonDocument row) => row.TryGetValue("status", out var value) && value.IsString && new[] { "active", "suspended", "revoked" }.Contains(value.AsString);
    private static bool Text(BsonValue value, int maximum) => value.IsString && CatalogSchemas.CanonicalText(value.AsString, maximum);
    private static InvalidOperationException Invalid(string collection, string reason) => new($"Catalog migration found incompatible {reason} in {collection}; reconcile them before retrying");
}
