using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Procurement;
using B = UnitToko.Platform.Procurement.SupplierDocumentBson;

namespace UnitToko.Platform.Procurement;

internal sealed record SupplierDocumentSourceCapacity(string Kind, long SourceId, long? SkuId, string SourceHash,
    BsonDocument Location, BsonDocument? Head, BsonDocument Capacity, BsonDocument Maximum);

public sealed partial class SupplierDocumentService
{
    private async Task<SupplierDocumentSourceCapacity> CapacityAsync(Context context, string kind, long sourceId, long? skuId, string hash, BsonDocument maximum, CancellationToken ct) {
        var table = kind == "inspection" ? B.InspectionHeads : B.OrderHeads;
        var filter = kind == "inspection" ? new BsonDocument("_id", sourceId) : new BsonDocument { { "orderId", sourceId }, { "requestSkuId", skuId!.Value } };
        var raw = await Rows(table).Find(context.Tx, filter).SingleOrDefaultAsync(ct); var head = raw is null ? null : await records.RowAsync(context.Tx, table, raw["_id"].AsInt64, ct);
        // Reconstruct from immutable source links as well as claims: deleting a claim must never reopen capacity.
        var linkFilter = kind == "inspection" ? new BsonDocument { { "kind", "inspection" }, { "id", sourceId } }
            : new BsonDocument { { "kind", "inspection" }, { "orderId", sourceId }, { "requestSkuId", skuId!.Value } };
        var claimed = await Rows(B.Matches).Find(context.Tx, new BsonDocument("$or", new BsonArray {
            new BsonDocument("links", new BsonDocument("$elemMatch", linkFilter)),
            new BsonDocument("claims", new BsonDocument("$elemMatch", new BsonDocument { { "kind", kind }, { "sourceId", sourceId }, { "skuId", skuId is { } sku ? sku : BsonNull.Value } })) })).ToListAsync(ct);
        foreach (var candidate in claimed) _ = await records.MatchAsync(context.Tx, candidate["_id"].AsInt64, ct);
        var ordered = claimed.Select(row => (Row: row, Claim: row["claims"].AsBsonArray.Single(value => value["kind"] == kind && value["sourceId"] == sourceId && value["skuId"] == (skuId is { } s ? (BsonValue)s : BsonNull.Value)).AsBsonDocument))
            .OrderBy(pair => pair.Claim["after"]["version"].AsInt64).ToArray();
        var capacity = maximum.DeepClone().AsBsonDocument; capacity.Remove("maximumAmounts"); BsonValue last = BsonNull.Value, lastHash = BsonNull.Value, firstAt = BsonNull.Value, at = BsonNull.Value;
        foreach (var pair in ordered) {
            var row = await records.MatchAsync(context.Tx, pair.Row["_id"].AsInt64, ct); var claim = pair.Claim;
            SupplierDocumentRecords.Equal(row["location"], ProcurementRequestBson.Location(context.Location)); SupplierDocumentRecords.Equal(claim["sourceHash"], hash);
            SupplierDocumentRecords.Equal(claim["before"], capacity); SupplierDocumentRecords.Equal(claim["previousMatchId"], last); SupplierDocumentRecords.Equal(claim["previousHash"], lastHash);
            capacity = SourceAfter(capacity, kind, sourceId, skuId, row["input"]["lines"].AsBsonArray, row["links"].AsBsonArray, maximum);
            SupplierDocumentRecords.Equal(claim["after"], capacity); if (head is null || claim["headId"] != head["_id"]) throw B.Broken();
            if (firstAt.IsBsonNull) firstAt = row["recordedAt"]; if (!at.IsBsonNull && row["recordedAt"].CompareTo(at) < 0) throw B.Broken();
            at = row["recordedAt"]; last = row["_id"]; lastHash = row["snapshotHash"];
        }
        if (head is null && ordered.Length != 0 || head is not null && ordered.Length == 0) throw B.Broken();
        if (head is not null) {
            SupplierDocumentRecords.Equal(head["sourceHash"], hash); SupplierDocumentRecords.Equal(head["location"], ProcurementRequestBson.Location(context.Location));
            SupplierDocumentRecords.Equal(head["version"], capacity["version"]); SupplierDocumentRecords.Equal(head["lines"], capacity["lines"]);
            SupplierDocumentRecords.Equal(head["lastMatchId"], last); SupplierDocumentRecords.Equal(head["lastHash"], lastHash); SupplierDocumentRecords.Equal(head["createdAt"], firstAt); SupplierDocumentRecords.Equal(head["updatedAt"], at);
            context.ExtraFences.RemoveAll(pair => pair.Table == table && pair.Row["_id"] == head["_id"]); context.ExtraFences.Add((table, head));
        }
        return new(kind, sourceId, skuId, hash, ProcurementRequestBson.Location(context.Location), head, capacity, maximum);
    }
    private static BsonDocument SourceAfter(BsonDocument before, string kind, long sourceId, long? skuId, BsonArray inputs, BsonArray links, BsonDocument maximum) {
        var after = before.DeepClone().AsBsonDocument; after["version"] = checked(before["version"].AsInt64 + 1); var found = false;
        for (var index = 0; index < links.Count; index++) {
            var link = links[index]; if (link.IsBsonNull || link["kind"] != "inspection" || (kind == "inspection" ? link["id"] != sourceId : link["orderId"] != sourceId || link["requestSkuId"] != skuId!.Value)) continue;
            var input = inputs[index]; var lineNumber = kind == "inspection" ? link["lineNumber"].AsInt32 : 1; var line = after["lines"][lineNumber - 1].AsBsonDocument; found = true;
            var matched = checked(line["matchedPackages"].AsInt64 + input["packages"].AsInt32); var total = maximum["lines"][lineNumber - 1]["packages"].AsInt32;
            if (matched > total) throw B.Changed("supplier_document_capacity_exceeded"); line["matchedPackages"] = matched; line["remainingPackages"] = total - matched;
            if (kind == "order") foreach (var (field, source) in new[] { ("matchedNetAmount", "netAmount"), ("matchedTaxAmount", "taxAmount"), ("matchedChargeAmount", "chargeAmount") }) {
                var sum = Money.Add(Money.Parse(line[field].AsString), Money.Parse(input[source].AsString));
                if (sum.Value > Money.Parse(maximum["maximumAmounts"][source].AsString).Value) throw B.Changed("supplier_document_capacity_exceeded"); line[field] = sum.ToString();
            }
        }
        if (!found || after["version"].AsInt64 >= long.MaxValue - 1) throw B.Broken(); return after;
    }
    private async Task<BsonArray> WriteClaimsAsync(Context context, BsonDocument row, IReadOnlyList<SupplierDocumentSourceCapacity> sources, CancellationToken ct) {
        var claims = new BsonArray();
        foreach (var source in sources) {
            var table = source.Kind == "inspection" ? B.InspectionHeads : B.OrderHeads; var head = source.Head;
            var id = head?["_id"].AsInt64 ?? (source.Kind == "inspection" ? source.SourceId : (await ids.NextAsync(B.OrderHeads, ct)).Value);
            var after = SourceAfter(source.Capacity, source.Kind, source.SourceId, source.SkuId, row["input"]["lines"].AsBsonArray, row["links"].AsBsonArray, source.Maximum);
            var claim = new BsonDocument { { "kind", source.Kind }, { "sourceId", source.SourceId }, { "skuId", source.SkuId is { } sku ? sku : BsonNull.Value }, { "sourceHash", source.SourceHash },
                { "headId", id }, { "before", source.Capacity }, { "after", after }, { "previousMatchId", head?["lastMatchId"] ?? BsonNull.Value }, { "previousHash", head?["lastHash"] ?? BsonNull.Value } }; claims.Add(claim);
        }
        return claims;
    }
    private async Task PersistClaimsAsync(Context context, BsonDocument row, IReadOnlyList<SupplierDocumentSourceCapacity> sources, CancellationToken ct) {
        foreach (var source in sources) {
            var claim = row["claims"].AsBsonArray.Single(value => value["kind"] == source.Kind && value["sourceId"] == source.SourceId && value["skuId"] == (source.SkuId is { } sku ? (BsonValue)sku : BsonNull.Value));
            var table = source.Kind == "inspection" ? B.InspectionHeads : B.OrderHeads; var next = new BsonDocument { { "_id", claim["headId"] }, { "schemaVersion", 1 }, { "sourceHash", source.SourceHash },
                { "location", source.Location }, { "version", claim["after"]["version"] }, { "lines", claim["after"]["lines"] }, { "lastMatchId", row["_id"] }, { "lastHash", row["snapshotHash"] },
                { "fence", source.Head?["fence"] ?? 0L }, { "createdAt", source.Head?["createdAt"] ?? row["recordedAt"] }, { "updatedAt", row["recordedAt"] } };
            if (source.Kind == "inspection") next.Add("inspectionId", source.SourceId); else next.Add("orderId", source.SourceId).Add("requestSkuId", source.SkuId!.Value);
            if (source.Head is null) await Rows(table).InsertOneAsync(context.Tx, next, cancellationToken: ct);
            else if ((await Rows(table).ReplaceOneAsync(context.Tx, source.Head, next, cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
            context.ExtraFences.RemoveAll(pair => pair.Table == table && pair.Row["_id"] == next["_id"]); context.ExtraFences.Add((table, next));
        }
    }
}
