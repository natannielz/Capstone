using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Catalog;

/// <summary>Private evidence reader. The caller must first authorize the exact SKU/document owner
/// and fence current SKU/definition/permission facts in the same transaction.</summary>
internal sealed class CatalogIdentifierGraph(IMongoDatabase database)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal sealed class Capture(IMongoDatabase database, IReadOnlyList<BsonDocument> identifiers,
        IReadOnlyList<(string Collection, BsonDocument Row)> evidence)
    {
        internal IReadOnlyList<BsonDocument> Identifiers { get; } = identifiers;
        internal async Task CompleteAsync(IClientSessionHandle tx, CancellationToken ct, IReadOnlySet<long>? writtenIdentifiers = null)
        {
            foreach (var item in evidence.OrderBy(item => item.Collection, StringComparer.Ordinal).ThenBy(item => item.Row["_id"].ToString(), StringComparer.Ordinal))
                if (item.Collection != CatalogIdentifierRecords.Collection || writtenIdentifiers is null || !writtenIdentifiers.Contains(item.Row["_id"].AsInt64))
                    await CatalogIdentifierRecords.FenceAsync(database.GetCollection<BsonDocument>(item.Collection), tx, item.Row, ct);
        }
    }
    internal async Task<Capture> ReadSkuAsync(IClientSessionHandle tx, BsonDocument sku, BsonDocument definition, CancellationToken ct)
    {
        CatalogAuthoringService.RequirePair(sku, definition);
        // Bounded complete evidence, never a truncated first match. Exceeding either limit refuses resolution.
        var identifiers = await Rows(CatalogIdentifierRecords.Collection).Find(tx, new BsonDocument("skuId", sku["_id"]))
            .Sort(new BsonDocument("_id", 1)).Limit(129).ToListAsync(ct);
        if (identifiers.Count is 0 or > 128) throw CatalogIdentifierRecords.Broken();
        var evidence = new Dictionary<(string Collection, BsonValue Id), BsonDocument>();
        void Observe(string collection, BsonDocument row)
        {
            var key = (collection, row["_id"]);
            if (evidence.TryGetValue(key, out var before) && !CatalogSchemas.SemanticEquals(before, row)) throw CatalogIdentifierRecords.Broken();
            evidence[key] = row.DeepClone().AsBsonDocument;
        }
        var parsed = new List<CatalogIdentifier>();
        foreach (var row in identifiers)
        {
            var identifier = CatalogIdentifierRecords.Read(row); parsed.Add(identifier);
            if (identifier.SkuId.Value != sku["_id"].AsInt64 || identifier.OrganizationId.Value != sku["organizationId"].AsInt64) throw CatalogIdentifierRecords.Broken();
            if (identifier.Kind == "gtin" && identifier.Namespace != CatalogIdentifierPolicy.Namespace("gtin", CatalogRepository.Owner(sku["authoringOwner"].AsBsonDocument), null)) throw CatalogIdentifierRecords.Broken();
            Observe(CatalogIdentifierRecords.Collection, row);
            if (identifier.DefinitionId is { } definitionId)
            {
                var assignedDefinition = await Rows("variantDefinitions").Find(tx, new BsonDocument("_id", definitionId.Value)).SingleOrDefaultAsync(ct);
                if (assignedDefinition is null || !CatalogSchemas.IsValid("variantDefinitions", assignedDefinition) || assignedDefinition["skuId"] != sku["_id"] ||
                    new[] { "packageKind", "baseUnitCode", "baseUnitsPerPackage", "attributes" }.Any(field => !CatalogSchemas.SemanticEquals(assignedDefinition[field], definition[field]))) throw CatalogIdentifierRecords.Broken();
                Observe("variantDefinitions", assignedDefinition);
            }
            var history = await Rows(CatalogIdentifierRecords.History).Find(tx, new BsonDocument("identifierId", row["_id"]))
                .Sort(new BsonDocument("assignmentVersion", 1)).Limit(257).ToListAsync(ct);
            if (history.Count is 0 or > 256 || history.Count != identifier.Revision) throw CatalogIdentifierRecords.Broken();
            CatalogIdentifier? previous = null;
            foreach (var item in history)
            {
                CatalogIdentifierRecords.RequireHistory(item); var assignment = item["assignment"].AsBsonDocument;
                var state = CatalogIdentifierRecords.Read(assignment);
                if (state.Id != identifier.Id || state.Revision != (previous?.Revision ?? 0) + 1 ||
                    previous is null && item["event"] != "assigned" ||
                    previous is not null && (previous.RetiredAt is not null || item["event"] == "assigned" ||
                        !CatalogIdentifierEvidence.Transition(previous, state, item["event"] == "aliased"))) throw CatalogIdentifierRecords.Broken();
                var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", item["auditId"])).SingleOrDefaultAsync(ct) ?? throw CatalogIdentifierRecords.Broken();
                if (item["schemaVersion"] == 1)
                {
                    _ = CatalogReceiptRules.RequireAudit(audit);
                    if (audit["operation"].AsString is not ("catalog.sku.create" or "catalog.sku.replace") || audit["actorId"] != item["actorId"] ||
                        audit["createdAt"] != item["createdAt"] || audit["after"]["sku"]["_id"] != assignment["skuId"] ||
                        audit["after"]["sku"]["organizationId"] != assignment["organizationId"] || audit["after"]["sku"]["sku"] != assignment["canonicalValue"]) throw CatalogIdentifierRecords.Broken();
                }
                else
                {
                    _ = CatalogIdentifierEvidence.RequireAudit(audit);
                    if (audit["actorId"] != item["actorId"] || audit["createdAt"] != item["createdAt"] ||
                        audit["identifiersAfter"].AsBsonArray.Count(value => CatalogSchemas.SemanticEquals(value, assignment)) != 1 ||
                        previous is not null && audit["identifiersBefore"].AsBsonArray.Count(value => value["_id"] == assignment["_id"] &&
                            CatalogIdentifierRecords.Read(value.AsBsonDocument) == previous) != 1) throw CatalogIdentifierRecords.Broken();
                    var receipts = await Rows("idempotencyRecords").Find(tx, new BsonDocument("catalogIdentifier.auditId", audit["_id"])).Limit(2).ToListAsync(ct);
                    if (receipts.Count != 1) throw CatalogIdentifierRecords.Broken();
                    CatalogIdentifierEvidence.RequireReceipt(receipts[0], audit); Observe("idempotencyRecords", receipts[0]);
                }
                Observe(CatalogIdentifierRecords.History, item); Observe("auditEvents", audit); previous = state;
            }
            if (!CatalogSchemas.SemanticEquals(history[^1]["assignment"], row)) throw CatalogIdentifierRecords.Broken();
        }
        if (parsed.Count(value => value.IsPrimary && value.Kind == "skuCode" && value.Namespace == "internal" && value.RetiredAt is null) != 1 ||
            parsed.Count(value => value.IsPrimary) != 1 ||
            // The initial stable code remains reserved even when a corrected primary is displayed.
            parsed.Count(value => value.Kind == "skuCode" && value.Namespace == "internal" && value.CanonicalValue == sku["sku"].AsString) != 1)
            throw CatalogIdentifierRecords.Broken();
        return new(database, identifiers, evidence.Select(pair => (pair.Key.Collection, pair.Value)).ToArray());
    }
}
