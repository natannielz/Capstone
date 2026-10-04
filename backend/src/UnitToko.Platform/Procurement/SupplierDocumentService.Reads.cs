using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using B = UnitToko.Platform.Procurement.SupplierDocumentBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class SupplierDocumentService
{
    public async Task<BsonDocument> QualityAllocationsAsync(ActorContext actor, long documentId, long inspectionId,
        int inspectionLineNumber, long? beforeAllocationId = null, CancellationToken ct = default) {
        if (documentId <= 0 || inspectionId <= 0 || inspectionLineNumber <= 0 || beforeAllocationId is <= 0)
            throw SupplierDocumentRules.Invalid();
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await DocumentContextAsync(actor, authority, tx, documentId, Permissions.ProcurementSupplierDocumentMatch, token);
            if (context.Draft!["input"]["kind"] != "invoice") throw B.Changed();
            await VerifySourcesAsync(context, token);
            var history = await records.InspectionAsync(tx, inspectionId, token);
            var inspection = history.Submissions.SingleOrDefault(row => row["_id"] == inspectionId) ?? throw B.Broken();
            if (inspection["location"] != R.Location(context.Location) ||
                history.Source["notice"]["source"]["proposal"]["input"]["supplierId"] != context.Draft["supplierId"] ||
                inspectionLineNumber > inspection["input"]["lines"].AsBsonArray.Count) throw B.Changed("supplier_document_quality_source_unavailable");
            var receiptRows = await Rows(ProcurementStockReceiptBson.Receipts).Find(tx, new BsonDocument {
                { "inspectionId", inspectionId }, { "inspectionLineNumber", inspectionLineNumber }, { "location", R.Location(context.Location) } })
                .Project(new BsonDocument("_id", 1)).ToListAsync(token);
            var receiptIds = new BsonArray(receiptRows.Select(row => row["_id"]));
            var filter = new BsonDocument("receiptId", new BsonDocument("$in", receiptIds));
            if (beforeAllocationId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var raws = await Rows(ProcurementQualityAcceptanceBson.Allocations).Find(tx, filter)
                .Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var sourceCapacity = await records.SourceCapacityAsync(tx, context.Location, "inspection", inspectionId, null,
                inspection["snapshotHash"].AsString,
                B.Capacity(new BsonArray(inspection["input"]["lines"].AsBsonArray.Select(line => new BsonDocument("packages", line["receivedPackages"])))), token);
            if (sourceCapacity.Head is not null) context.ExtraFences.Add((B.InspectionHeads, sourceCapacity.Head));
            var qualityReader = new ProcurementQualityAcceptanceSourceReader(database, unit, clock, catalog, orders,
                new UnitToko.Platform.Inventory.StockSourceSetupService(database, unit, ids, clock));
            var items = new BsonArray();
            foreach (var raw in raws.Take(25)) {
                var quality = await qualityReader.ReadAsync(tx, raw["_id"].AsInt64, token);
                if (quality.Receipt["inspectionId"] != inspectionId || quality.Receipt["inspectionLineNumber"] != inspectionLineNumber ||
                    quality.Receipt["location"] != R.Location(context.Location) || quality.Receipt["supplierId"] != context.Draft["supplierId"] ||
                    quality.Receipt["inspection"]["snapshotHash"] != inspection["snapshotHash"] ||
                    quality.Receipt["source"]["approval"]["snapshotHash"] != history.Source["notice"]["source"]["approval"]["snapshotHash"])
                    throw B.Broken();
                var matched = sourceCapacity.QualityMatched.GetValueOrDefault(raw["_id"].AsInt64);
                var accepted = quality.Allocation["packages"].AsInt32;
                if (matched < 0 || matched > accepted) throw B.Broken();
                var choice = B.PublicQuality(quality.Snapshot);
                choice.Add("matchedPackages", matched); choice.Add("remainingPackages", accepted - matched);
                items.Add(choice);
                if (quality.Capacity.Head is not null && !context.ExtraFences.Any(pair => pair.Table == ProcurementQualityAcceptanceBson.ReceiptHeads && pair.Row["_id"] == quality.Capacity.Head["_id"]))
                    context.ExtraFences.Add((ProcurementQualityAcceptanceBson.ReceiptHeads, quality.Capacity.Head));
            }
            await FenceAsync(context, token);
            return new BsonDocument { { "documentId", documentId }, { "inspectionId", inspectionId }, { "inspectionLineNumber", inspectionLineNumber },
                { "items", items }, { "nextAllocationId", raws.Count > 25 ? raws[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, CancellationToken ct = default) { await StorageAsync(ct); return await receiving.TargetsAsync(actor, ct); }
    public async Task<BsonDocument> OrdersAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        await StorageAsync(ct); return await receiving.OrdersAsync(actor, location, beforeId, ct);
    }
    private async Task VerifySourcesAsync(Context context, CancellationToken ct) {
        var history = context.History ?? throw B.Broken();
        foreach (var pair in await records.RequireSourceCapacitiesAsync(context.Tx, history, ct)) {
            if (pair.Table is B.InspectionHeads or B.OrderHeads) {
                context.ExtraFences.RemoveAll(value => value.Table == pair.Table && value.Row["_id"] == pair.Row["_id"]);
                context.ExtraFences.Add(pair);
            } else if (!context.ExtraFences.Any(value => value.Table == pair.Table && value.Row["_id"] == pair.Row["_id"])) context.ExtraFences.Add(pair);
        }
    }
    private static (BsonArray Items, BsonValue Next) MatchPage(SupplierDocumentHistory history, long? beforeId) {
        var selected = history.Matches.Where(row => beforeId is null || row["_id"].AsInt64 < beforeId).OrderByDescending(row => row["_id"].AsInt64).Take(26).ToArray();
        return (new(selected.Take(25).Select(B.MatchView)), selected.Length > 25 ? selected[24]["_id"] : BsonNull.Value);
    }
    private BsonDocument ReadView(Context context) {
        context.IncludeEvidence = ScopePolicy.Authorize(context.Actor, context.Authority, Permissions.ProcurementSupplierDocumentEvidenceRead, context.Resource, clock.UtcNow).Allowed;
        var history = context.History!; var page = MatchPage(history, null);
        return new() { { "draft", B.DraftView(history.Draft) }, { "document", history.Document is null ? BsonNull.Value : B.SubmissionView(history.Document, context.IncludeEvidence) },
            { "capacity", history.Capacity }, { "reviewedInput", history.Head["reviewedInput"] }, { "possibleDuplicate", history.Identity["draftCount"].AsInt64 > 1 }, { "matches", page.Items }, { "nextMatchId", page.Next } };
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, long draftId, CancellationToken ct = default) {
        if (draftId <= 0) throw SupplierDocumentRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await DraftContextAsync(actor, authority, tx, draftId, Permissions.ProcurementRead, false, token);
            await VerifySourcesAsync(context, token); var result = ReadView(context); await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> ListAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw SupplierDocumentRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var scope = ContextFor(actor, authority, tx, location, Permissions.ProcurementRead); await PeopleAsync(scope, null, token);
            // ponytail: retain complete scoped immutable claim/effect reconstruction before paging; an indexed reconciliation proof may replace it after measured load evidence.
            var rows = await Rows(B.Drafts).Find(tx, new BsonDocument("location", R.Location(location))).Sort(new BsonDocument("_id", -1)).ToListAsync(token);
            var page = rows.Where(row => beforeId is null || row["_id"].AsInt64 < beforeId).Take(26).ToArray(); var items = new BsonArray(); var contexts = new List<Context>();
            foreach (var row in rows) {
                var context = await DraftContextAsync(actor, authority, tx, row["_id"].AsInt64, Permissions.ProcurementRead, false, token); await VerifySourcesAsync(context, token);
                if (page.Take(25).Any(selected => selected["_id"] == row["_id"])) items.Add(ReadView(context));
                await FenceAsync(context, token); contexts.Add(context);
            }
            await records.OrphansAsync(tx, token); await FenceAsync(scope, token); foreach (var context in contexts) Current(context); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "location", R.Location(location) }, { "items", items }, { "nextDraftId", page.Length > 25 ? page[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> MatchesAsync(ActorContext actor, long documentId, long? beforeId = null, CancellationToken ct = default) {
        if (documentId <= 0 || beforeId is <= 0) throw SupplierDocumentRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await DocumentContextAsync(actor, authority, tx, documentId, Permissions.ProcurementRead, token); await VerifySourcesAsync(context, token);
            var page = MatchPage(context.History!, beforeId); await FenceAsync(context, token);
            return new BsonDocument { { "documentId", documentId }, { "items", page.Items }, { "nextMatchId", page.Next } };
        }, ct);
    }
    public async Task<BsonDocument> InspectionsAsync(ActorContext actor, long documentId, long? beforeId = null, CancellationToken ct = default) {
        if (documentId <= 0 || beforeId is <= 0) throw SupplierDocumentRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await DocumentContextAsync(actor, authority, tx, documentId, Permissions.ProcurementSupplierDocumentMatch, token);
            if (context.Draft!["input"]["kind"] != "invoice") throw B.Changed(); await VerifySourcesAsync(context, token);
            var filter = new BsonDocument { { "location", R.Location(context.Location) }, { "source.notice.source.proposal.input.supplierId", context.Draft["supplierId"] } };
            if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(ProcurementInspectionBson.Submissions).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray();
            foreach (var row in rows.Take(25)) {
                var history = await records.InspectionAsync(tx, row["_id"].AsInt64, token); var original = history.Submissions.Single(value => value["_id"] == row["_id"]);
                SupplierDocumentRecords.Equal(original["location"], R.Location(context.Location)); SupplierDocumentRecords.Equal(history.Source["notice"]["source"]["proposal"]["input"]["supplierId"], context.Draft["supplierId"]);
                var capacity = await CapacityAsync(context, "inspection", row["_id"].AsInt64, null, original["snapshotHash"].AsString,
                    B.Capacity(new BsonArray(original["input"]["lines"].AsBsonArray.Select(line => new BsonDocument("packages", line["receivedPackages"])))), token);
                items.Add(new BsonDocument { { "inspectionId", row["_id"] }, { "notice", ProcurementReceivingBson.NoticeView(history.Source["notice"].AsBsonDocument) },
                    { "intake", ProcurementReceivingBson.IntakeView(history.Source["intake"].AsBsonDocument) }, { "input", original["input"] },
                    { "order", PurchaseOrderService.FundingOrderView(history.Source["notice"]["source"].AsBsonDocument) }, { "capacity", capacity.Capacity } });
            }
            await records.OrphansAsync(tx, token); await FenceAsync(context, token);
            return new BsonDocument { { "documentId", documentId }, { "items", items }, { "nextInspectionId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> InvoiceMatchesAsync(ActorContext actor, long documentId, long? beforeId = null, CancellationToken ct = default) {
        if (documentId <= 0 || beforeId is <= 0) throw SupplierDocumentRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await DocumentContextAsync(actor, authority, tx, documentId, Permissions.ProcurementSupplierDocumentMatch, token);
            if (context.Draft!["input"]["kind"] != "sph") throw B.Changed(); await VerifySourcesAsync(context, token);
            var filter = new BsonDocument { { "location", R.Location(context.Location) }, { "supplierId", context.Draft["supplierId"] }, { "input.reviewedInput.kind", "invoice" } };
            if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(B.Matches).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray(); var contexts = new List<Context>();
            foreach (var row in rows.Take(25)) {
                var source = await DocumentContextAsync(actor, authority, tx, row["documentId"].AsInt64, Permissions.ProcurementRead, token); await VerifySourcesAsync(source, token);
                var match = source.History!.Matches.Single(value => value["_id"] == row["_id"]); items.Add(new BsonDocument { { "document", B.DraftView(source.Draft!) }, { "match", B.MatchView(match) } });
                await FenceAsync(source, token); contexts.Add(source);
            }
            await FenceAsync(context, token); foreach (var source in contexts) Current(source); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "documentId", documentId }, { "items", items }, { "nextMatchId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument?> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        if (operation is not (B.DraftOperation or B.SubmitOperation or B.MatchOperation)) throw SupplierDocumentRules.Invalid(); Key(key); await StorageAsync(ct);
        var permission = operation == B.MatchOperation ? Permissions.ProcurementSupplierDocumentMatch : Permissions.ProcurementSupplierDocumentRecord;
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var row = await ExistingAsync(actor, authority, tx, operation, key, token);
            if (row is null) { await FenceQueryAsync(actor, authority, tx, permission, token); return null; }
            var context = await DraftContextAsync(actor, authority, tx, row["draftId"].AsInt64, permission, false, token); await VerifySourcesAsync(context, token);
            var actual = operation == B.DraftOperation ? context.Draft! : operation == B.SubmitOperation ? context.Submission ?? throw B.Broken() : context.History!.Matches.SingleOrDefault(value => value["_id"] == row["_id"]) ?? throw B.Broken();
            SupplierDocumentRecords.Equal(actual["_id"], row["_id"]); await FenceAsync(context, token); return B.Result(actual);
        }, ct);
    }
}
