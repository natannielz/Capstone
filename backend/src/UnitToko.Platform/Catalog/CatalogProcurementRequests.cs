using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.RegularExpressions;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;

namespace UnitToko.Platform.Catalog;

internal sealed record ProcurementCatalogCapture(BsonArray Sources, IReadOnlyList<(string Collection, BsonDocument Row)> Heads);

public sealed partial class CatalogAuthoringService
{
    private void RequireProcurement(IMongoDatabase owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ProcurementLocation location, string permission) {
        if (owner is null || !ReferenceEquals(owner.Client, database.Client) || !owner.DatabaseNamespace.Equals(database.DatabaseNamespace) ||
            !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || permission is not (Permissions.ProcurementRead or Permissions.ProcurementPrepare or Permissions.ProcurementApprove or Permissions.SupplierChangePropose or Permissions.SupplierChangeApprove))
            throw new InvalidOperationException("Procurement catalog reads require the authorized owning transaction");
        var scope = ProcurementRequestRules.Scope(location); var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow);
        AuthorizationStore.Require(query);
        if (!ScopePolicy.IsScopeActive(scope, authority.Directory) || !query.Matches(CatalogRepository.Resource(scope))) throw CatalogRepository.Invalid("scope_denied");
    }
    internal async Task<ProcurementCatalogCapture> CaptureProcurementAsync(IMongoDatabase owner, ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, ProcurementLocation location, string permission, IReadOnlyList<ProcurementRequestLineInput> lines, CancellationToken ct) {
        RequireProcurement(owner, actor, authority, tx, location, permission);
        var sources = new BsonArray(); var heads = new Dictionary<(string, long), BsonDocument>(); var scope = ProcurementRequestRules.Scope(location);
        foreach (var line in lines) {
            var sku = await repository.GetAsync(tx, "skus", line.SkuId, ct); var author = RowOwner(sku);
            if (!ScopePolicy.ContainsScope(author, scope)) throw CatalogRepository.Invalid("scope_denied");
            var family = await repository.GetAsync(tx, "productFamilies", sku["familyId"].AsInt64, ct);
            var category = await repository.GetAsync(tx, "catalogCategories", family["categoryId"].AsInt64, ct);
            var definition = await repository.GetAsync(tx, "variantDefinitions", sku["currentDefinitionId"].AsInt64, ct);
            if (RowOwner(family) != author || RowOwner(category) != author || category["code"] != family["categoryCode"]) throw CatalogRepository.Invalid();
            RequirePair(sku, definition); RequireActive(sku); RequireActive(family); RequireActive(category);
            CatalogRepository.Expected(sku, line.ExpectedSkuRevision);
            if (definition["_id"].AsInt64 != line.DefinitionId || definition["version"].AsInt64 != line.DefinitionVersion || definition["effectiveAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime)
                throw new PersistenceConflictException("catalog_revision_conflict", "Catalog packaging changed; reload and review");
            var audits = new BsonDocument();
            foreach (var (name, collection, row) in new[] { ("sku", "skus", sku), ("family", "productFamilies", family), ("category", "catalogCategories", category) }) {
                heads[(collection, row["_id"].AsInt64)] = row;
                var audit = await repository.Rows("auditEvents").Find(tx, new BsonDocument { { "kind", "catalog.command" },
                    { "result.kind", name }, { "result.id", row["_id"] }, { "result.version", row["revision"] } }).SingleOrDefaultAsync(ct) ?? throw CatalogRepository.Invalid();
                await RequireProcurementAuditAsync(tx, audit, name, row, ct); audits.Add(name, audit["_id"]);
            }
            sources.Add(new BsonDocument { { "sku", ProcurementMaster(sku) }, { "family", ProcurementMaster(family) }, { "category", ProcurementMaster(category) },
                { "definition", definition.DeepClone() }, { "audits", audits } });
        }
        RequireProcurement(owner, actor, authority, tx, location, permission); ct.ThrowIfCancellationRequested();
        return new(sources, heads.Select(pair => (pair.Key.Item1, pair.Value)).ToArray());
    }
    internal async Task FenceProcurementAsync(IMongoDatabase owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ProcurementLocation location, string permission, ProcurementCatalogCapture capture, CancellationToken ct) {
        RequireProcurement(owner, actor, authority, tx, location, permission);
        foreach (var head in capture.Heads.OrderBy(row => row.Collection, StringComparer.Ordinal).ThenBy(row => row.Row["_id"].AsInt64))
            await repository.TouchAsync(tx, head.Collection, head.Row, ct);
        RequireProcurement(owner, actor, authority, tx, location, permission); ct.ThrowIfCancellationRequested();
    }
    internal async Task<(ProcurementCatalogCapture Capture, bool HasMore, BsonDocument Matching)> ProcurementPackagesAsync(IMongoDatabase owner, ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, ProcurementLocation location, int offset, int limit, string? search, CancellationToken ct, string permission = Permissions.ProcurementRead) {
        RequireProcurement(owner, actor, authority, tx, location, permission); AuthorizationStore.ValidatePage(offset, limit);
        var scope = ProcurementRequestRules.Scope(location); var shop = scope with { Kind = GrantScopeKind.FulfillmentShop, WarehouseId = null };
        var query = search is null or "" ? "" : ProductMatchRule.Normalize(CatalogAuthoringPolicy.Text(search, 128));
        if (query.Length > 128) throw CatalogRepository.Invalid();
        var pipeline = StockChoicePipeline(F.Or(CatalogRepository.OwnerFilter("authoringOwner", scope), CatalogRepository.OwnerFilter("authoringOwner", shop)) & F.Eq("active", true));
        pipeline.Add(new("$sort", new BsonDocument("_id", 1)));
        pipeline.Add(new("$project", new BsonDocument { { "_id", 1 }, { "revision", 1 }, { "currentDefinitionId", 1 },
            { "definition.version", 1 }, { "sku", 1 }, { "family.name", 1 }, { "definition.packageLabel", 1 } }));
        List<BsonDocument> rows; var limited = false;
        if (query.Length == 0) {
            pipeline.Add(new("$skip", offset)); pipeline.Add(new("$limit", limit + 1));
            rows = await repository.Rows("skus").Aggregate<BsonDocument>(tx, pipeline).ToListAsync(ct);
        } else {
            // Exact codes and literal fragments search the complete authorized scope; only typo candidates use the bounded prefix of that scope.
            BsonDocument Stop() => new("$limit", ProductMatchRule.CandidateLimit + 1);
            var fragments = new BsonArray(query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(token => new BsonDocument("$or", new BsonArray(
                new[] { "sku", "family.name", "definition.packageLabel" }.Select(field => new BsonDocument(field, new BsonRegularExpression(Regex.Escape(token), "i")))))));
            pipeline.Add(new("$facet", new BsonDocument {
                { "exact", new BsonArray { new BsonDocument("$match", new BsonDocument("sku", new BsonRegularExpression("^" + Regex.Escape(query) + "$", "i"))), Stop() } },
                { "literal", new BsonArray { new BsonDocument("$match", new BsonDocument("$and", fragments)), Stop() } },
                { "fuzzy", new BsonArray { Stop() } } }));
            var found = await repository.Rows("skus").Aggregate<BsonDocument>(tx, pipeline).SingleAsync(ct);
            limited = found.Elements.Any(part => part.Value.AsBsonArray.Count > ProductMatchRule.CandidateLimit);
            rows = found.Elements.SelectMany(part => part.Value.AsBsonArray.Take(ProductMatchRule.CandidateLimit)).Select(value => value.AsBsonDocument)
                .DistinctBy(row => row["_id"].AsInt64).Select(row => (Row: row, Match: Match(row))).Where(item => item.Match is not null)
                .OrderBy(item => item.Match!.Rank).ThenBy(item => item.Match!.EditDistance ?? 0).ThenBy(item => item.Row["sku"].AsString, StringComparer.Ordinal)
                .ThenBy(item => item.Row["_id"].AsInt64).Skip(offset).Take(limit + 1).Select(item => item.Row).ToList();
        }
        ProductMatch? Match(BsonDocument row) => ProductMatchRule.Match(query, row["sku"].AsString, row["family"]["name"].AsString, row["definition"]["packageLabel"].AsString);
        var lines = rows.Take(limit).Select(row => new ProcurementRequestLineInput(row["_id"].AsInt64, row["revision"].AsInt64,
            row["currentDefinitionId"].AsInt64, row["definition"]["version"].AsInt64, 1, null)).ToArray();
        var matching = new BsonDocument { { "ruleVersion", ProductMatchRule.Version }, { "limited", limited }, { "items", new BsonArray(rows.Take(limit).Select(row => {
            var match = Match(row) ?? throw CatalogRepository.Invalid();
            return new BsonDocument { { "skuId", row["_id"] }, { "kind", match.Kind }, { "field", match.Field },
                { "editDistance", match.EditDistance is { } distance ? new BsonInt32(distance) : BsonNull.Value } };
        })) } };
        return (await CaptureProcurementAsync(owner, actor, authority, tx, location, permission, lines, ct), rows.Count > limit, matching);
    }
    internal static BsonDocument ProcurementMaster(BsonDocument row) { var copy = row.DeepClone().AsBsonDocument; copy["fence"] = 0L; return copy; }
    private async Task RequireProcurementAuditAsync(IClientSessionHandle tx, BsonDocument audit, string part, BsonDocument row, CancellationToken ct,
        PurchaseOrderSourceReadCapture? sourceCapture = null) {
        _ = CatalogReceiptRules.RequireAudit(audit);
        if (!ProcurementMaster(audit["after"][part].AsBsonDocument).Equals(ProcurementMaster(row))) throw CatalogRepository.Invalid();
        var receipts = await repository.Rows("idempotencyRecords").Find(tx, new BsonDocument("catalogAuthoring.auditId", audit["_id"])).Limit(2).ToListAsync(ct);
        sourceCapture?.Record("idempotencyRecords", new BsonDocument("catalogAuthoring.auditId", audit["_id"]), receipts);
        if (receipts.Count != 1) throw CatalogRepository.Invalid(); var receipt = receipts[0];
        CatalogReceiptRules.RequireReceipt(receipt, audit["actorId"].AsInt64, audit["operation"].AsString, receipt["key"].AsString);
        if (receipt["bodyHash"] != audit["bodyHash"] || receipt["result"] != audit["result"] || receipt["createdAt"] != audit["createdAt"]) throw CatalogRepository.Invalid();
        var effectFilter = new BsonDocument("sourceKey", "catalog.command:" + audit["_id"].AsInt64);
        var effect = (sourceCapture is null ? await repository.Rows("outboxEvents").Find(tx, effectFilter).SingleOrDefaultAsync(ct)
            : await sourceCapture.OneAsync(database, tx, "outboxEvents", effectFilter, ct)) ?? throw CatalogRepository.Invalid();
        var expected = new BsonDocument { { "kind", audit["result"]["kind"] }, { "id", audit["result"]["id"] }, { "version", audit["result"]["version"] } };
        if (effect["kind"] != "catalog.changed" || effect["createdAt"] != audit["createdAt"] || effect["payload"] != expected) throw CatalogRepository.Invalid();
    }
    internal async Task VerifyProcurementSourceAsync(IClientSessionHandle tx, ProcurementLocation location, BsonDocument source, CancellationToken ct,
        PurchaseOrderSourceReadCapture? sourceCapture = null) {
        var scope = ProcurementRequestRules.Scope(location);
        var sku = CatalogRepository.Require("skus", source["sku"].AsBsonDocument); var family = CatalogRepository.Require("productFamilies", source["family"].AsBsonDocument);
        var category = CatalogRepository.Require("catalogCategories", source["category"].AsBsonDocument); var definition = CatalogRepository.Require("variantDefinitions", source["definition"].AsBsonDocument);
        if (!ScopePolicy.ContainsScope(RowOwner(sku), scope) || RowOwner(family) != RowOwner(sku) || RowOwner(category) != RowOwner(sku) ||
            sku["familyId"] != family["_id"] || family["categoryId"] != category["_id"] || family["categoryCode"] != category["code"]) throw CatalogRepository.Invalid();
        RequirePair(sku, definition); RequireActive(sku); RequireActive(family); RequireActive(category);
        var actualDefinition = await repository.GetAsync(tx, "variantDefinitions", definition["_id"].AsInt64, ct);
        sourceCapture?.Record("variantDefinitions", new BsonDocument("_id", definition["_id"]), [actualDefinition]);
        if (!definition.Equals(actualDefinition)) throw CatalogRepository.Invalid();
        foreach (var (name, row) in new[] { ("sku", sku), ("family", family), ("category", category) }) {
            var audit = await repository.Rows("auditEvents").Find(tx, new BsonDocument("_id", source["audits"][name])).SingleOrDefaultAsync(ct) ?? throw CatalogRepository.Invalid();
            sourceCapture?.Record("auditEvents", new BsonDocument("_id", source["audits"][name]), [audit]);
            await RequireProcurementAuditAsync(tx, audit, name, row, ct, sourceCapture);
        }
    }
}
