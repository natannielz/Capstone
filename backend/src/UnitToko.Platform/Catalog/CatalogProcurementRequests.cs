using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Catalog;

internal sealed record ProcurementCatalogCapture(BsonArray Sources, IReadOnlyList<(string Collection, BsonDocument Row)> Heads);

public sealed partial class CatalogAuthoringService
{
    private void RequireProcurement(IMongoDatabase owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ProcurementLocation location, string permission) {
        if (owner is null || !ReferenceEquals(owner.Client, database.Client) || !owner.DatabaseNamespace.Equals(database.DatabaseNamespace) ||
            !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || permission is not (Permissions.ProcurementRead or Permissions.ProcurementPrepare or Permissions.ProcurementApprove))
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
    internal async Task<(ProcurementCatalogCapture Capture, bool HasMore)> ProcurementPackagesAsync(IMongoDatabase owner, ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, ProcurementLocation location, int offset, int limit, string? search, CancellationToken ct) {
        RequireProcurement(owner, actor, authority, tx, location, Permissions.ProcurementRead); AuthorizationStore.ValidatePage(offset, limit);
        var scope = ProcurementRequestRules.Scope(location); var shop = scope with { Kind = GrantScopeKind.FulfillmentShop, WarehouseId = null };
        var pipeline = StockChoicePipeline(F.Or(CatalogRepository.OwnerFilter("authoringOwner", scope), CatalogRepository.OwnerFilter("authoringOwner", shop)) &
            F.Eq("active", true) & SearchFilter("displayName", search));
        pipeline.Add(new("$sort", new BsonDocument("_id", 1))); pipeline.Add(new("$skip", offset)); pipeline.Add(new("$limit", limit + 1));
        pipeline.Add(new("$project", new BsonDocument { { "_id", 1 }, { "revision", 1 }, { "currentDefinitionId", 1 }, { "definition.version", 1 } }));
        var rows = await repository.Rows("skus").Aggregate<BsonDocument>(tx, pipeline).ToListAsync(ct);
        var lines = rows.Take(limit).Select(row => new ProcurementRequestLineInput(row["_id"].AsInt64, row["revision"].AsInt64,
            row["currentDefinitionId"].AsInt64, row["definition"]["version"].AsInt64, 1, null)).ToArray();
        return (await CaptureProcurementAsync(owner, actor, authority, tx, location, Permissions.ProcurementRead, lines, ct), rows.Count > limit);
    }
    internal static BsonDocument ProcurementMaster(BsonDocument row) { var copy = row.DeepClone().AsBsonDocument; copy["fence"] = 0L; return copy; }
    private async Task RequireProcurementAuditAsync(IClientSessionHandle tx, BsonDocument audit, string part, BsonDocument row, CancellationToken ct) {
        _ = CatalogReceiptRules.RequireAudit(audit);
        if (!ProcurementMaster(audit["after"][part].AsBsonDocument).Equals(ProcurementMaster(row))) throw CatalogRepository.Invalid();
        var receipts = await repository.Rows("idempotencyRecords").Find(tx, new BsonDocument("catalogAuthoring.auditId", audit["_id"])).Limit(2).ToListAsync(ct);
        if (receipts.Count != 1) throw CatalogRepository.Invalid(); var receipt = receipts[0];
        CatalogReceiptRules.RequireReceipt(receipt, audit["actorId"].AsInt64, audit["operation"].AsString, receipt["key"].AsString);
        if (receipt["bodyHash"] != audit["bodyHash"] || receipt["result"] != audit["result"] || receipt["createdAt"] != audit["createdAt"]) throw CatalogRepository.Invalid();
        var effect = await repository.Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", "catalog.command:" + audit["_id"].AsInt64)).SingleOrDefaultAsync(ct) ?? throw CatalogRepository.Invalid();
        var expected = new BsonDocument { { "kind", audit["result"]["kind"] }, { "id", audit["result"]["id"] }, { "version", audit["result"]["version"] } };
        if (effect["kind"] != "catalog.changed" || effect["createdAt"] != audit["createdAt"] || effect["payload"] != expected) throw CatalogRepository.Invalid();
    }
    internal async Task VerifyProcurementSourceAsync(IClientSessionHandle tx, ProcurementLocation location, BsonDocument source, CancellationToken ct) {
        var scope = ProcurementRequestRules.Scope(location);
        var sku = CatalogRepository.Require("skus", source["sku"].AsBsonDocument); var family = CatalogRepository.Require("productFamilies", source["family"].AsBsonDocument);
        var category = CatalogRepository.Require("catalogCategories", source["category"].AsBsonDocument); var definition = CatalogRepository.Require("variantDefinitions", source["definition"].AsBsonDocument);
        if (!ScopePolicy.ContainsScope(RowOwner(sku), scope) || RowOwner(family) != RowOwner(sku) || RowOwner(category) != RowOwner(sku) ||
            sku["familyId"] != family["_id"] || family["categoryId"] != category["_id"] || family["categoryCode"] != category["code"]) throw CatalogRepository.Invalid();
        RequirePair(sku, definition); RequireActive(sku); RequireActive(family); RequireActive(category);
        if (!definition.Equals(await repository.GetAsync(tx, "variantDefinitions", definition["_id"].AsInt64, ct))) throw CatalogRepository.Invalid();
        foreach (var (name, row) in new[] { ("sku", sku), ("family", family), ("category", category) }) {
            var audit = await repository.Rows("auditEvents").Find(tx, new BsonDocument("_id", source["audits"][name])).SingleOrDefaultAsync(ct) ?? throw CatalogRepository.Invalid();
            await RequireProcurementAuditAsync(tx, audit, name, row, ct);
        }
    }
}
