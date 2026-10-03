using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Procurement.ProcurementStockValuationBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementStockValuationService
{
    private async Task FenceQueryAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, CancellationToken ct) {
        var permissions = new[] { Permissions.ProcurementStockValuationRead, Permissions.ProcurementRead, Permissions.StockRead };
        var clauses = permissions.SelectMany(permission => Query(actor, authority, permission).Clauses).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        var expiry = await SessionAsync(actor, tx, ct);
        foreach (var permission in permissions) _ = Query(actor, authority, permission);
        if (expiry is { } until && clock.UtcNow >= until) throw new AuthorizationDeniedException("stale_or_inactive_session"); ct.ThrowIfCancellationRequested();
    }
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, CancellationToken ct = default) {
        await StorageAsync(ct); var named = await receiving.TargetsAsync(actor, ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var items = new BsonArray(); foreach (var item in named["items"].AsBsonArray) {
                var location = R.Location(item["location"]); var context = new Context(actor, authority, tx, location, Permissions.ProcurementStockValuationRead);
                try { _ = Require(context); } catch (AuthorizationDeniedException) { continue; }
                items.Add(item);
            }
            await FenceQueryAsync(actor, authority, tx, token); return new BsonDocument("items", items);
        }, ct);
    }
    private async Task<BsonDocument> ReceiptAsync(IClientSessionHandle tx, long id, ProcurementLocation? location, CancellationToken ct) {
        var receipt = await new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, sources).ReadAsync(tx, id, ct);
        if (location is not null) ProcurementStockValuationRecords.Equal(receipt["location"], R.Location(location));
        return receipt;
    }
    public async Task<BsonDocument> CostAsync(ActorContext actor, long receiptId, long? beforeAllocationId = null,
        CancellationToken ct = default) {
        if (receiptId <= 0 || beforeAllocationId is <= 0) throw ProcurementStockValuationRules.Invalid();
        await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var receipt = await ReceiptAsync(tx, receiptId, null, token);
            var context = new Context(actor, authority, tx, R.Location(receipt["location"]), Permissions.ProcurementStockValuationRead);
            _ = Require(context);
            var capacityReader = new ProcurementStockValuationCapacityReader(database);
            var coverage = await capacityReader.ReceiptAsync(tx, receipt, token);
            var documents = new SupplierDocumentRecords(database, unit, clock, catalog, orders);
            var verifiedMatches = new Dictionary<long, BsonDocument>();
            foreach (var group in coverage.Allocations.GroupBy(row => row["invoiceDocumentId"].AsInt64)) {
                var invoice = await documents.SubmissionAsync(tx, group.Key, token);
                var history = await documents.HistoryAsync(tx, invoice["draftId"].AsInt64, token);
                if (history.Identity["draftCount"].AsInt64 != 1 || history.Document is null ||
                    history.Document["_id"] != invoice["_id"] || invoice["input"]["kind"] != "invoice" ||
                    invoice["location"] != receipt["location"] || invoice["supplierId"] != receipt["supplierId"]) throw B.Broken();
                foreach (var matchGroup in group.GroupBy(row => row["invoiceMatchId"].AsInt64)) {
                    var match = history.Matches.SingleOrDefault(row => row["_id"] == matchGroup.Key) ?? throw B.Broken();
                    if (match["documentId"] != invoice["_id"] || matchGroup.Any(row => row["matchHash"] != match["snapshotHash"]))
                        throw B.Broken();
                    if (!verifiedMatches.TryAdd(matchGroup.Key, match)) throw B.Broken();
                }
            }
            await capacityReader.VerifyMatchesAsync(tx, coverage, verifiedMatches, token);
            var count = receipt["input"]["packages"].AsInt32;
            var valued = coverage.State["allocatedPackages"].AsInt64;
            var unvalued = coverage.State["remainingPackages"].AsInt64;
            if (valued < 0 || unvalued < 0 || checked(valued + unvalued) != count) throw B.Broken();
            var net = new Money(0m); var tax = new Money(0m); var charge = new Money(0m); var capitalized = new Money(0m);
            foreach (var allocation in coverage.Allocations) {
                net = Money.Add(net, Money.Parse(allocation["netAmount"].AsString));
                tax = Money.Add(tax, Money.Parse(allocation["taxAmount"].AsString));
                charge = Money.Add(charge, Money.Parse(allocation["chargeAmount"].AsString));
                capitalized = Money.Add(capitalized, Money.Parse(allocation["capitalizedAmount"].AsString));
            }
            if (capitalized != Money.Add(Money.Add(net, tax), charge)) throw B.Broken();
            if (beforeAllocationId is { } before && !coverage.Allocations.Any(row => row["_id"] == before))
                throw B.Changed("procurement_stock_valuation_cursor_unavailable");
            var sorted = coverage.Allocations.Where(row => beforeAllocationId is null || row["_id"].AsInt64 < beforeAllocationId)
                .OrderByDescending(row => row["_id"].AsInt64).Take(26).ToArray();
            var items = new BsonArray();
            foreach (var allocation in sorted.Take(25)) {
                var proposal = await Rows(B.Proposals).Find(tx, new BsonDocument("_id", allocation["proposalId"])).SingleOrDefaultAsync(token) ?? throw B.Broken();
                if (proposal["snapshotHash"] != allocation["proposalHash"] || proposal["input"]["receiptId"] != receiptId) throw B.Broken();
                items.Add(new BsonDocument { { "id", allocation["_id"] }, { "proposalId", allocation["proposalId"] },
                    { "decisionId", allocation["decisionId"] }, { "invoiceDocumentId", allocation["invoiceDocumentId"] },
                    { "invoiceMatchId", allocation["invoiceMatchId"] }, { "matchLineNumber", allocation["matchLineNumber"] },
                    { "journalId", allocation["journalId"] }, { "packages", allocation["packages"] },
                    { "netAmount", allocation["netAmount"] }, { "taxAmount", allocation["taxAmount"] },
                    { "chargeAmount", allocation["chargeAmount"] }, { "capitalizedAmount", allocation["capitalizedAmount"] },
                    { "postingDate", proposal["input"]["postingDate"] }, { "recordedAt", allocation["recordedAt"] } });
            }
            var result = new BsonDocument { { "receiptId", receiptId }, { "lotId", receipt["lotId"] },
                { "balanceId", receipt["balanceId"] }, { "packageCount", (long)count }, { "valuedPackages", valued },
                { "unvaluedPackages", unvalued }, { "currency", "IDR" },
                { "state", valued == 0 ? "unvalued" : unvalued == 0 ? "fully_valued" : "partially_valued" },
                { "netAmount", net.ToString() }, { "taxAmount", tax.ToString() }, { "chargeAmount", charge.ToString() },
                { "capitalizedAmount", capitalized.ToString() }, { "items", items },
                { "nextAllocationId", sorted.Length > 25 ? sorted[24]["_id"] : BsonNull.Value } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> ReceiptsAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw ProcurementStockValuationRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var context = new Context(actor, authority, tx, location, Permissions.ProcurementStockValuationRead); _ = Require(context);
            var filter = new BsonDocument("location", R.Location(location)); if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(ProcurementStockReceiptBson.Receipts).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = new BsonArray(); foreach (var raw in rows.Take(25)) {
                var receipt = await ReceiptAsync(tx, raw["_id"].AsInt64, location, token);
                var capacity = await new ProcurementStockValuationCapacityReader(database).ReceiptAsync(tx, receipt, token);
                items.Add(new BsonDocument { { "receipt", ProcurementStockReceiptBson.View(receipt) }, { "receiptCapacity", capacity.State } });
            }
            await FenceAsync(context, token); return new() { { "location", R.Location(location) }, { "items", items },
                { "nextReceiptId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> MatchesAsync(ActorContext actor, long receiptId, long? beforeMatchId = null, CancellationToken ct = default) {
        if (receiptId <= 0 || beforeMatchId is <= 0) throw ProcurementStockValuationRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var receipt = await ReceiptAsync(tx, receiptId, null, token); var location = R.Location(receipt["location"]);
            var context = new Context(actor, authority, tx, location, Permissions.ProcurementStockValuationRead); _ = Require(context);
            var filter = new BsonDocument("location", receipt["location"]); if (beforeMatchId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var raws = await Rows(SupplierDocumentBson.Matches).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var documents = new SupplierDocumentRecords(database, unit, clock, catalog, orders); var items = new BsonArray();
            foreach (var raw in raws.Take(25)) {
                if (raw["supplierId"] != receipt["supplierId"]) continue;
                var invoice = await documents.SubmissionAsync(tx, raw["documentId"].AsInt64, token);
                if (invoice["input"]["kind"] != "invoice" || invoice["location"] != receipt["location"]) continue;
                var match = await documents.MatchAsync(tx, raw["_id"].AsInt64, token, invoice);
                for (var i = 0; i < match["lines"].AsBsonArray.Count; i++) {
                    var line = match["lines"][i]; var link = match["links"][i];
                    if (line["state"] != "matched" || link.IsBsonNull || link["kind"] != "inspection" ||
                        link["id"] != receipt["inspectionId"] || link["lineNumber"] != receipt["inspectionLineNumber"] ||
                        link["hash"] != receipt["inspection"]["snapshotHash"] || link["orderId"] != receipt["orderId"] ||
                        link["orderHash"] != receipt["source"]["approval"]["snapshotHash"]) continue;
                    var capacity = await new ProcurementStockValuationCapacityReader(database).ReadAsync(tx, receipt, match, i + 1, token);
                    items.Add(new BsonDocument { { "invoiceDocumentId", invoice["_id"] }, { "invoiceDocumentRevision", 1L },
                        { "invoiceMatchId", match["_id"] }, { "matchLineNumber", i + 1 }, { "documentReference", invoice["input"]["documentReference"] },
                        { "line", new BsonDocument { { "documentLineNumber", line["documentLineNumber"] }, { "packages", line["packages"] },
                            { "netAmount", line["netAmount"] }, { "taxAmount", line["taxAmount"] }, { "chargeAmount", line["chargeAmount"] },
                            { "inspectionId", line["inspectionId"] }, { "inspectionLineNumber", line["inspectionLineNumber"] }, { "state", line["state"] } } },
                        { "matchCapacity", capacity.Match } });
                }
            }
            await FenceAsync(context, token); return new() { { "receiptId", receiptId }, { "items", items },
                { "nextMatchId", raws.Count > 25 ? raws[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> PoliciesAsync(ActorContext actor, long receiptId, long? beforeId = null, CancellationToken ct = default) {
        if (receiptId <= 0 || beforeId is <= 0) throw ProcurementStockValuationRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var receipt = await ReceiptAsync(tx, receiptId, null, token); var location = R.Location(receipt["location"]);
            var context = new Context(actor, authority, tx, location, Permissions.ProcurementStockValuationRead); _ = Require(context);
            var filter = new BsonDocument("location", receipt["location"]); if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(B.Policies).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = new BsonArray(); foreach (var row in rows.Take(25)) {
                if (!row["active"].AsBoolean || row["approvedAt"].ToUniversalTime() > Now.UtcDateTime || row["expiresAt"].ToUniversalTime() <= Now.UtcDateTime) continue;
                items.Add(PolicyView(row));
            }
            await FenceAsync(context, token); return new() { { "receiptId", receiptId }, { "items", items },
                { "nextPolicyId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, long proposalId, CancellationToken ct = default) {
        if (proposalId <= 0) throw ProcurementStockValuationRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var proposal = await records.ProposalAsync(tx, proposalId, token); var input = B.Input(proposal["input"]);
            var context = new Context(actor, authority, tx, input.Location, Permissions.ProcurementStockValuationRead); _ = Require(context);
            var decision = await records.DecisionAsync(tx, proposal, token); var source = await records.SourcesAsync(tx, input, token, proposal);
            var result = new BsonDocument { { "proposal", ProposalView(proposal, decision, source) },
                { "decision", decision is null ? BsonNull.Value : B.Result(decision) },
                { "decisionReason", decision is null ? BsonNull.Value : decision["reason"] } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> HistoryAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw ProcurementStockValuationRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var context = new Context(actor, authority, tx, location, Permissions.ProcurementStockValuationRead); _ = Require(context);
            var filter = new BsonDocument("location", R.Location(location)); if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var raws = await Rows(B.Proposals).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = new BsonArray(); foreach (var raw in raws.Take(25)) {
                var proposal = await records.ProposalAsync(tx, raw["_id"].AsInt64, token);
                var input = B.Input(proposal["input"]); var source = await records.SourcesAsync(tx, input, token, proposal);
                var decision = await records.DecisionAsync(tx, proposal, token);
                items.Add(new BsonDocument { { "proposal", ProposalView(proposal, decision, source) },
                    { "decision", decision is null ? BsonNull.Value : B.Result(decision) },
                    { "decisionReason", decision is null ? BsonNull.Value : decision["reason"] } });
            }
            await FenceAsync(context, token); return new() { { "location", R.Location(location) }, { "items", items },
                { "nextProposalId", raws.Count > 25 ? raws[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument?> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        if (operation != B.ProposeOperation && operation != B.DecideOperation) throw ProcurementStockValuationRules.Invalid(); Key(key); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var row = await records.ForKeyAsync(tx, actor.UserId, operation, key, token);
            if (row is null) { await FenceQueryAsync(actor, authority, tx, token); return null; }
            var context = new Context(actor, authority, tx, R.Location(row["location"]), Permissions.ProcurementStockValuationRead); _ = Require(context);
            await FenceAsync(context, token); return B.Result(row);
        }, ct);
    }
}
