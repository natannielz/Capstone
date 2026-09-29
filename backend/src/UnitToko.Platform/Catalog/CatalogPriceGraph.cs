using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Catalog.CatalogPriceBson;

namespace UnitToko.Platform.Catalog;

public sealed partial class CatalogPriceService
{
    private async Task<BsonDocument> GetAsync(IClientSessionHandle tx, string collection, long id, CancellationToken ct)
    {
        if (id <= 0) throw B.Invalid();
        return await Rows(collection).Find(tx, F.Eq("_id", id)).SingleOrDefaultAsync(ct) ?? throw B.Invalid();
    }
    private async Task<BsonDocument> ProposalAsync(Context context, long id, CancellationToken ct)
    {
        var row = await GetAsync(context.Tx, B.Proposals, id, ct);
        B.Proposal(row);
        if (row["skuId"] != context.Sku["_id"]) throw B.Invalid();
        var sku = row["skuSnapshot"].AsBsonDocument;
        foreach (var name in new[] { "_id", "organizationId", "familyId", "publicId", "publicUuid", "sku", "physicalItemKey", "predecessorSkuId", "authoringOwner" })
            if (sku[name] != context.Sku[name]) throw B.Invalid();
        if (sku["revision"].AsInt64 > context.Sku["revision"].AsInt64) throw B.Invalid();
        var definition = await repository.GetAsync(context.Tx, "variantDefinitions", row["definition"]["_id"].AsInt64, ct);
        if (!definition.Equals(row["definition"])) throw B.Invalid();
        if (!row["basePrice"].IsBsonNull) {
            var fact = row["basePrice"].AsBsonDocument;
            var actual = B.Price(await GetAsync(context.Tx, B.Versions, fact["_id"].AsInt64, ct));
            if (!B.Fact(actual).Equals(fact)) throw B.Invalid();
        }
        await ProvenanceAsync(context.Tx, row, null, ct);
        return row;
    }
    private async Task<PriceVersion> PriceGraphAsync(Context context, BsonDocument row, CancellationToken ct)
    {
        var price = B.Price(row);
        if (price.SkuId.Value != context.Sku["_id"].AsInt64) throw B.Invalid();
        var proposal = await ProposalAsync(context, row["proposalId"].AsInt64, ct);
        var decision = await GetAsync(context.Tx, B.Decisions, row["decisionId"].AsInt64, ct);
        B.Decision(decision, proposal);
        if (decision["decision"] != "approve" || decision["priceId"] != row["_id"] || decision["priceVersion"] != row["version"])
            throw B.Invalid();
        await ProvenanceAsync(context.Tx, decision, proposal, ct);
        var previous = proposal["basePrice"].IsBsonNull ? null : B.Price(proposal["basePrice"].AsBsonDocument, true);
        var expected = CatalogPricePolicy.Approve(CatalogRepository.Sku(proposal["skuSnapshot"].AsBsonDocument),
            CatalogRepository.Definition(proposal["definition"].AsBsonDocument), previous,
            B.Expected(proposal["expected"].AsBsonDocument), B.Input(proposal["input"].AsBsonDocument),
            price.Id, B.Time(decision["decidedAt"]));
        if (price != expected) throw B.Invalid();
        return price;
    }
    private async Task ProvenanceAsync(IClientSessionHandle tx, BsonDocument row, BsonDocument? proposal, CancellationToken ct)
    {
        var audit = await GetAsync(tx, "auditEvents", row["auditId"].AsInt64, ct);
        if (!audit.Equals(B.Audit(row))) throw B.Invalid("catalog_price_provenance_invalid");
        var operation = B.Operation(row);
        var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", B.Actor(row)) & F.Eq("operation", operation) &
            F.Eq("key", row["requestKey"])).SingleOrDefaultAsync(ct) ?? throw B.Invalid("catalog_price_provenance_invalid");
        B.Guard(() => {
            var independent = proposal is not null && row["decision"] != "withdraw";
            var fields = new List<string> { "_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result" };
            if (independent) fields.Add("personApproval");
            B.Exact(receipt, fields.ToArray());
            if (!receipt["_id"].IsObjectId || !receipt["schemaVersion"].IsInt32 || receipt["schemaVersion"] != 1 ||
                !receipt["actorId"].IsInt64 || receipt["actorId"] != B.Actor(row) || receipt["operation"] != operation ||
                receipt["key"] != row["requestKey"] || receipt["bodyHash"] != row["bodyHash"] ||
                B.Time(receipt["createdAt"]) > B.Time(B.At(row)) || !receipt["result"].Equals(B.Result(row))) throw B.Invalid();
            if (independent) {
                var evidence = new ApprovalRequest(1, 1, ProposedBy: proposal!["proposedBy"].AsInt64);
                if (!receipt["personApproval"].Equals(PersonApprovalReceipt.Create(Permissions.PricingApprove, evidence))) throw B.Invalid();
                _ = PersonApprovalReceipt.Read(receipt, Permissions.PricingApprove);
            }
        });
    }
}
