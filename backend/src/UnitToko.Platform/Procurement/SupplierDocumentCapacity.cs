using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Procurement;
using B = UnitToko.Platform.Procurement.SupplierDocumentBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal sealed record SupplierDocumentSourceCapacity(string Kind, long SourceId, long? SkuId, string SourceHash,
    BsonDocument Location, BsonDocument? Head, BsonDocument Capacity, BsonDocument Maximum,
    IReadOnlyDictionary<long, long> QualityMatched);

internal sealed partial class SupplierDocumentRecords
{
    internal async Task<SupplierDocumentSourceCapacity> SourceCapacityAsync(IClientSessionHandle tx, ProcurementLocation location, string kind, long sourceId, long? skuId, string hash, BsonDocument maximum, CancellationToken ct,
        Dictionary<long, BsonDocument>? verifiedMatches = null) {
        var table = kind == "inspection" ? B.InspectionHeads : B.OrderHeads;
        var filter = kind == "inspection" ? new BsonDocument("_id", sourceId) : new BsonDocument { { "orderId", sourceId }, { "requestSkuId", skuId!.Value } };
        var raw = await Rows(table).Find(tx, filter).SingleOrDefaultAsync(ct); var head = raw is null ? null : await RowAsync(tx, table, raw["_id"].AsInt64, ct);
        // Reconstruct from immutable source links as well as claims: deleting a claim must never reopen capacity.
        var linkFilter = kind == "inspection" ? new BsonDocument { { "kind", "inspection" }, { "id", sourceId } }
            : new BsonDocument { { "kind", "inspection" }, { "orderId", sourceId }, { "requestSkuId", skuId!.Value } };
        var claimed = await Rows(B.Matches).Find(tx, new BsonDocument("$or", new BsonArray {
            new BsonDocument("links", new BsonDocument("$elemMatch", linkFilter)),
            new BsonDocument("claims", new BsonDocument("$elemMatch", new BsonDocument { { "kind", kind }, { "sourceId", sourceId }, { "skuId", skuId is { } sku ? sku : BsonNull.Value } })) })).ToListAsync(ct);
        verifiedMatches ??= [];
        foreach (var candidate in claimed) {
            var id = candidate["_id"].AsInt64;
            if (!verifiedMatches.TryGetValue(id, out var verified)) {
                verified = await MatchAsync(tx, id, ct);
                verifiedMatches.Add(id, verified);
            }
            Equal(candidate, verified);
        }
        var ordered = claimed.Select(raw => {
            var row = verifiedMatches[raw["_id"].AsInt64];
            return (Row: row, Claim: row["claims"].AsBsonArray.Single(value => value["kind"] == kind && value["sourceId"] == sourceId && value["skuId"] == (skuId is { } s ? (BsonValue)s : BsonNull.Value)).AsBsonDocument);
        })
            .OrderBy(pair => pair.Claim["after"]["version"].AsInt64).ToArray();
        var capacity = maximum.DeepClone().AsBsonDocument; capacity.Remove("maximumAmounts"); BsonValue last = BsonNull.Value, lastHash = BsonNull.Value, firstAt = BsonNull.Value, at = BsonNull.Value;
        foreach (var pair in ordered) {
            var row = pair.Row; var claim = pair.Claim;
            SupplierDocumentRecords.Equal(row["location"], ProcurementRequestBson.Location(location)); SupplierDocumentRecords.Equal(claim["sourceHash"], hash);
            SupplierDocumentRecords.Equal(claim["before"], capacity); SupplierDocumentRecords.Equal(claim["previousMatchId"], last); SupplierDocumentRecords.Equal(claim["previousHash"], lastHash);
            capacity = SupplierDocumentService.SourceAfter(capacity, kind, sourceId, skuId, row["input"]["lines"].AsBsonArray, row["links"].AsBsonArray, maximum);
            SupplierDocumentRecords.Equal(claim["after"], capacity); if (head is null || claim["headId"] != head["_id"]) throw B.Broken();
            if (firstAt.IsBsonNull) firstAt = row["recordedAt"]; if (!at.IsBsonNull && row["recordedAt"].CompareTo(at) < 0) throw B.Broken();
            at = row["recordedAt"]; last = row["_id"]; lastHash = row["snapshotHash"];
        }
        var qualityMatched = new Dictionary<long, long>();
        if (kind == "inspection") foreach (var candidate in claimed.Select(row => verifiedMatches[row["_id"].AsInt64])) {
            for (var index = 0; index < candidate["links"].AsBsonArray.Count; index++) {
                var link = candidate["links"][index];
                if (link.IsBsonNull || link["kind"] != "inspection" || link["id"] != sourceId ||
                    !link.AsBsonDocument.TryGetValue("quality", out var quality)) continue;
                var id = quality["allocationId"].AsInt64;
                var line = candidate["input"]["lines"][index];
                if (line["qualityAllocationId"] != id || candidate["lines"][index]["quality"] != quality) throw B.Broken();
                qualityMatched[id] = checked(qualityMatched.GetValueOrDefault(id) + line["packages"].AsInt32);
                if (qualityMatched[id] > quality["acceptedPackages"].AsInt32) throw B.Changed("supplier_document_quality_capacity_exceeded");
            }
        }
        if (head is null && ordered.Length != 0 || head is not null && ordered.Length == 0) throw B.Broken();
        if (head is not null) {
            SupplierDocumentRecords.Equal(head["sourceHash"], hash); SupplierDocumentRecords.Equal(head["location"], ProcurementRequestBson.Location(location));
            SupplierDocumentRecords.Equal(head["version"], capacity["version"]); SupplierDocumentRecords.Equal(head["lines"], capacity["lines"]);
            SupplierDocumentRecords.Equal(head["lastMatchId"], last); SupplierDocumentRecords.Equal(head["lastHash"], lastHash); SupplierDocumentRecords.Equal(head["createdAt"], firstAt); SupplierDocumentRecords.Equal(head["updatedAt"], at);
        }
        return new(kind, sourceId, skuId, hash, ProcurementRequestBson.Location(location), head, capacity, maximum, qualityMatched);
    }
    internal async Task<List<SupplierDocumentSourceCapacity>> SourceCapacitiesAsync(IClientSessionHandle tx, ProcurementLocation location,
        Dictionary<long, ProcurementInspectionHistory> inspections, BsonArray links, List<(string Table, BsonDocument Row)> fences, CancellationToken ct,
        BsonArray? pendingLines = null, Dictionary<long, BsonDocument>? verifiedMatches = null) {
        // Both inspection and order capacities traverse the same immutable matches.
        // Keep one proof map for this invocation and transaction only.
        verifiedMatches ??= [];
        var result = new List<SupplierDocumentSourceCapacity>(); var ordersSeen = new HashSet<(long Order, long Sku)>();
        async Task AddAsync(string kind, long sourceId, long? skuId, string hash, BsonDocument maximum) {
            var source = await SourceCapacityAsync(tx, location, kind, sourceId, skuId, hash, maximum, ct, verifiedMatches); result.Add(source);
            if (source.Head is not { } head) return;
            var table = kind == "inspection" ? B.InspectionHeads : B.OrderHeads;
            fences.RemoveAll(pair => pair.Table == table && pair.Row["_id"] == head["_id"]); fences.Add((table, head));
        }
        foreach (var pair in inspections.OrderBy(pair => pair.Key)) {
            var history = pair.Value; var inspection = history.Submissions.Single(row => row["_id"] == pair.Key);
            Equal(inspection["location"], R.Location(location));
            var lines = new BsonArray(inspection["input"]["lines"].AsBsonArray.Select(line => new BsonDocument("packages", line["receivedPackages"])));
            await AddAsync("inspection", pair.Key, null, inspection["snapshotHash"].AsString, B.Capacity(lines));
            foreach (var (table, row) in new[] { (ProcurementReceivingBson.Heads, history.CustodyHead), (ProcurementInspectionBson.Heads, history.Head!),
                (ProcurementInspectionBson.Drafts, history.Drafts.Single(row => row["_id"] == inspection["draftId"])) })
                if (!fences.Any(existing => existing.Table == table && existing.Row["_id"] == row["_id"])) fences.Add((table, row));
            foreach (var link in links.Where(link => !link.IsBsonNull && link["kind"] == "inspection" && link["id"] == pair.Key)) {
                var key = (link["orderId"].AsInt64, link["requestSkuId"].AsInt64); if (!ordersSeen.Add(key)) continue;
                var source = history.Source["notice"]["source"]; var quoted = source["proposal"]["input"]["lines"].AsBsonArray.Single(line => line["requestSkuId"] == key.Item2);
                var maximum = B.Capacity(new BsonArray { new BsonDocument("packages", quoted["packages"]) });
                maximum.Add("maximumAmounts", new BsonDocument { { "netAmount", Money.Multiply(Money.Parse(quoted["netPricePerPackage"].AsString), quoted["packages"].AsInt32).ToString() },
                    { "taxAmount", quoted["taxAmount"] }, { "chargeAmount", quoted["supplierChargeAmount"] } });
                await AddAsync("order", key.Item1, key.Item2, link["orderHash"].AsString, maximum);
            }
        }
        if (pendingLines is not null) {
            var added = new Dictionary<long, long>();
            for (var index = 0; index < links.Count; index++) {
                var link = links[index]; if (link.IsBsonNull || link["kind"] != "inspection" ||
                    !link.AsBsonDocument.TryGetValue("quality", out var quality)) continue;
                var id = quality["allocationId"].AsInt64;
                if (pendingLines[index]["qualityAllocationId"] != id) throw B.Broken();
                added[id] = checked(added.GetValueOrDefault(id) + pendingLines[index]["packages"].AsInt32);
                var source = result.Single(value => value.Kind == "inspection" && value.SourceId == link["id"].AsInt64);
                if (source.QualityMatched.GetValueOrDefault(id) + added[id] > quality["acceptedPackages"].AsInt32)
                    throw B.Changed("supplier_document_quality_capacity_exceeded");
            }
        }
        return result;
    }
    // Reuses the live owner's exact source-chain proof during successor migrations and linked claims.
    // The caller supplies genuine HistoryAsync output and fences the returned mutable rows in its transaction.
    internal async Task<IReadOnlyList<(string Table, BsonDocument Row)>> RequireSourceCapacitiesAsync(IClientSessionHandle tx, SupplierDocumentHistory history,
        CancellationToken ct, SupplierDocumentResolvedMatchProof? completedProof = null) {
        var inspections = completedProof?.RequireCompleted(this, tx, history) ?? new Dictionary<long, ProcurementInspectionHistory>(); var links = new BsonArray();
        var fences = new List<(string Table, BsonDocument Row)>();
        var verifiedMatches = new Dictionary<long, BsonDocument>();
        async Task IncludeInspectionAsync(BsonValue link) {
            var inspectionId = link["id"].AsInt64;
            if (!inspections.TryGetValue(inspectionId, out var inspection)) {
                inspection = await InspectionAsync(tx, inspectionId, ct);
                inspections.Add(inspectionId, inspection);
            }
            var row = inspection.Submissions.SingleOrDefault(value => value["_id"] == inspectionId) ?? throw B.Broken();
            Equal(row["snapshotHash"], link["hash"]);
        }
        void IncludeMatch(BsonDocument match) {
            var id = match["_id"].AsInt64;
            if (verifiedMatches.TryGetValue(id, out var previous)) Equal(previous, match);
            else verifiedMatches.Add(id, match);
        }
        foreach (var match in history.Matches) {
            IncludeMatch(match);
            foreach (var link in match["links"].AsBsonArray.Where(link => !link.IsBsonNull)) {
                links.Add(link); if (link["kind"] != "invoice_match") continue;
                var invoice = await SubmissionAsync(tx, link["documentId"].AsInt64, ct);
                var invoiceHistory = await HistoryAsync(tx, invoice["draftId"].AsInt64, ct);
                if (invoiceHistory.Document != invoice || !invoiceHistory.Matches.Any(row =>
                    row["_id"] == link["id"] && row["snapshotHash"] == link["hash"])) throw B.Broken();
                foreach (var invoiceMatch in invoiceHistory.Matches) {
                    IncludeMatch(invoiceMatch);
                    foreach (var originalLink in invoiceMatch["links"].AsBsonArray.Where(value => !value.IsBsonNull)) {
                        if (originalLink["kind"] != "inspection") throw B.Broken();
                        links.Add(originalLink);
                    }
                }
                foreach (var pair in new[] { (B.Heads, invoiceHistory.Head), (B.Identities, invoiceHistory.Identity), (B.Drafts, invoiceHistory.Draft) })
                    if (!fences.Any(value => value.Table == pair.Item1 && value.Row["_id"] == pair.Item2["_id"])) fences.Add(pair);
            }
        }
        foreach (var link in links.Where(value => value["kind"] == "inspection")) await IncludeInspectionAsync(link);
        _ = await SourceCapacitiesAsync(tx, R.Location(history.Draft["location"]), inspections, links, fences, ct,
            verifiedMatches: verifiedMatches);
        return fences;
    }
}

public sealed partial class SupplierDocumentService
{
    private async Task<SupplierDocumentSourceCapacity> CapacityAsync(Context context, string kind, long sourceId, long? skuId, string hash, BsonDocument maximum, CancellationToken ct) {
        var source = await records.SourceCapacityAsync(context.Tx, context.Location, kind, sourceId, skuId, hash, maximum, ct);
        if (source.Head is { } head) {
            var table = kind == "inspection" ? B.InspectionHeads : B.OrderHeads;
            context.ExtraFences.RemoveAll(pair => pair.Table == table && pair.Row["_id"] == head["_id"]); context.ExtraFences.Add((table, head));
        }
        return source;
    }
    internal static BsonDocument SourceAfter(BsonDocument before, string kind, long sourceId, long? skuId, BsonArray inputs, BsonArray links, BsonDocument maximum) {
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
