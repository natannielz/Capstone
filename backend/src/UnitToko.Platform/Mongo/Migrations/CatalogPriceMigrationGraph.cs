using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using B = UnitToko.Platform.Catalog.CatalogPriceBson;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Validates historical facts without requiring historical users to still be active.</summary>
internal sealed class CatalogPriceMigrationGraph(IMongoDatabase database)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private async Task<BsonDocument> Get(string name, BsonValue id, CancellationToken ct) =>
        await Rows(name).Find(new BsonDocument("_id", id)).SingleOrDefaultAsync(ct) ?? throw B.Invalid();
    internal async Task ValidateAsync(MigrationLease lease, CancellationToken ct)
    {
        foreach (var name in CatalogPriceSchemas.Collections)
        {
            using var cursor = await Rows(name).Find(new BsonDocument()).ToCursorAsync(ct);
            while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current)
            {
                await lease.RenewAsync(ct);
                if (name == B.Proposals) await Proposal(row, ct);
                else if (name == B.Decisions)
                {
                    var proposal = await Get(B.Proposals, row["proposalId"], ct); B.Decision(row, proposal);
                    await Provenance(row, proposal, ct);
                    if (row["decision"] == "approve") {
                        var price = await Get(B.Versions, row["priceId"], ct);
                        if (price["decisionId"] != row["_id"] || price["proposalId"] != row["proposalId"] || price["version"] != row["priceVersion"]) throw B.Invalid();
                    }
                }
                else if (name == B.Versions) await Price(row, ct);
                else
                {
                    B.Head(row); var price = await Get(B.Versions, row["priceId"], ct);
                    if (price["skuId"] != row["_id"] || price["version"] != row["version"]) throw B.Invalid();
                    var latest = await Rows(B.Versions).Find(new BsonDocument("skuId", row["_id"])).Sort(new BsonDocument("version", -1)).FirstAsync(ct);
                    if (latest["_id"] != row["priceId"]) throw B.Invalid();
                }
            }
        }
        // Detect deleted command rows even when all remaining rows are internally valid.
        using var audits = await Rows("auditEvents").Find(new BsonDocument("kind", B.Prefix + "command")).ToCursorAsync(ct);
        while (await audits.MoveNextAsync(ct)) foreach (var audit in audits.Current)
        {
            await lease.RenewAsync(ct);
            var operation = audit["operation"].AsString;
            var name = operation == B.Prefix + "propose" ? B.Proposals : B.Decisions;
            var row = await Get(name, audit["record"]["_id"], ct);
            if (!audit.Equals(B.Audit(row))) throw B.Invalid();
        }
        var actions = new BsonArray(new[] { "propose", "approve", "reject", "withdraw" }.Select(action => B.Prefix + action));
        using var receipts = await Rows("idempotencyRecords").Find(new BsonDocument("operation", new BsonDocument("$in", actions))).ToCursorAsync(ct);
        while (await receipts.MoveNextAsync(ct)) foreach (var receipt in receipts.Current)
        {
            await lease.RenewAsync(ct);
            var propose = receipt["operation"] == B.Prefix + "propose";
            var row = await Get(propose ? B.Proposals : B.Decisions, receipt["result"][propose ? "proposalId" : "decisionId"], ct);
            if (B.Actor(row) != receipt["actorId"] || B.Operation(row) != receipt["operation"] || row["requestKey"] != receipt["key"] ||
                row["bodyHash"] != receipt["bodyHash"] || !B.Result(row).Equals(receipt["result"])) throw B.Invalid();
        }
    }
    private async Task Proposal(BsonDocument row, CancellationToken ct)
    {
        B.Proposal(row);
        var sku = CatalogRepository.Require("skus", await Get("skus", row["skuId"], ct));
        var currentDefinition = CatalogRepository.Require("variantDefinitions", await Get("variantDefinitions", sku["currentDefinitionId"], ct));
        CatalogAuthoringService.RequirePair(sku, currentDefinition);
        var snapshot = row["skuSnapshot"].AsBsonDocument;
        foreach (var name in new[] { "_id", "organizationId", "familyId", "publicId", "publicUuid", "sku", "physicalItemKey", "predecessorSkuId", "authoringOwner" })
            if (snapshot[name] != sku[name]) throw B.Invalid();
        if (snapshot["revision"].AsInt64 > sku["revision"].AsInt64) throw B.Invalid();
        var family = CatalogRepository.Require("productFamilies", await Get("productFamilies", sku["familyId"], ct));
        var category = CatalogRepository.Require("catalogCategories", await Get("catalogCategories", family["categoryId"], ct));
        var owner = CatalogRepository.Owner(sku["authoringOwner"].AsBsonDocument);
        if (CatalogRepository.Owner(family["authoringOwner"].AsBsonDocument) != owner ||
            CatalogRepository.Owner(category["authoringOwner"].AsBsonDocument) != owner || family["categoryCode"] != category["code"]) throw B.Invalid();
        if (!(await Get("variantDefinitions", row["definition"]["_id"], ct)).Equals(row["definition"])) throw B.Invalid();
        if (!row["basePrice"].IsBsonNull) {
            var fact = row["basePrice"].AsBsonDocument;
            if (!B.Fact(B.Price(await Get(B.Versions, fact["_id"], ct))).Equals(fact)) throw B.Invalid();
        }
        await Provenance(row, null, ct);
    }
    private async Task Price(BsonDocument row, CancellationToken ct)
    {
        var price = B.Price(row);
        var proposal = await Get(B.Proposals, row["proposalId"], ct);
        var decision = await Get(B.Decisions, row["decisionId"], ct); B.Decision(decision, proposal);
        if (decision["decision"] != "approve" || decision["priceId"] != row["_id"] || decision["priceVersion"] != row["version"]) throw B.Invalid();
        var previous = proposal["basePrice"].IsBsonNull ? null : B.Price(proposal["basePrice"].AsBsonDocument, true);
        var expected = CatalogPricePolicy.Approve(CatalogRepository.Sku(proposal["skuSnapshot"].AsBsonDocument),
            CatalogRepository.Definition(proposal["definition"].AsBsonDocument), previous, B.Expected(proposal["expected"].AsBsonDocument),
            B.Input(proposal["input"].AsBsonDocument), price.Id, B.Time(decision["decidedAt"]));
        if (price != expected) throw B.Invalid();
        var head = await Get(B.Heads, row["skuId"], ct); B.Head(head);
        if (head["version"].AsInt64 < price.Version) throw B.Invalid();
    }
    private async Task Provenance(BsonDocument row, BsonDocument? proposal, CancellationToken ct)
    {
        if (!(await Get("auditEvents", row["auditId"], ct)).Equals(B.Audit(row))) throw B.Invalid();
        var receipt = await Rows("idempotencyRecords").Find(new BsonDocument {
            { "actorId", B.Actor(row) }, { "operation", B.Operation(row) }, { "key", row["requestKey"] }
        }).SingleOrDefaultAsync(ct) ?? throw B.Invalid();
        var independent = proposal is not null && row["decision"] != "withdraw";
        var fields = new List<string> { "_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result" };
        if (independent) fields.Add("personApproval");
        B.Exact(receipt, fields.ToArray());
        if (!receipt["_id"].IsObjectId || !receipt["schemaVersion"].IsInt32 || receipt["schemaVersion"] != 1 ||
            !receipt["actorId"].IsInt64 || receipt["bodyHash"] != row["bodyHash"] ||
            B.Time(receipt["createdAt"]) > B.Time(B.At(row)) || !receipt["result"].Equals(B.Result(row))) throw B.Invalid();
        if (independent) {
            var request = new ApprovalRequest(1, 1, ProposedBy: proposal!["proposedBy"].AsInt64);
            if (!receipt["personApproval"].Equals(PersonApprovalReceipt.Create(Permissions.PricingApprove, request))) throw B.Invalid();
            _ = PersonApprovalReceipt.Read(receipt, Permissions.PricingApprove);
        }
    }
}
