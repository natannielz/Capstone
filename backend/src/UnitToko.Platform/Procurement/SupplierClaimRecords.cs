using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.SupplierClaimBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;
using D = UnitToko.Platform.Procurement.SupplierDocumentBson;
using Q = UnitToko.Platform.Procurement.ProcurementQuarantineDispositionBson;

namespace UnitToko.Platform.Procurement;

internal sealed record SupplierClaimSources(BsonArray Lines, IReadOnlyDictionary<long, ProcurementInspectionHistory> Inspections,
    IReadOnlyDictionary<long, SupplierDocumentHistory> Documents, IReadOnlyList<(string Table, BsonDocument Row)> Fences);
internal sealed class SupplierClaimRecords(IMongoDatabase database, MongoUnitOfWork unit, IClock clock, CatalogAuthoringService catalog, PurchaseOrderService orders,
    ProcurementQualityProofGraph? qualityGraph = null)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string table, long id, CancellationToken ct) =>
        await Rows(table).Find(tx, new BsonDocument { { "_id", id }, { "$and", new BsonArray { table == Q.PhysicalAllocations ? ProcurementStockReceiptSchemas.DispositionValidator() : SupplierClaimSchemas.Validator(table) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
    internal ProcurementStockReceiptRecords HeldReceipts => new(database, unit, clock, catalog, orders, new StockSourceSetupService(database, unit, new IdAllocator(database), clock));
    internal async Task<ProcurementInspectionHistory> InspectionAsync(IClientSessionHandle tx, long id, CancellationToken ct) =>
        await new SupplierDocumentRecords(database, unit, clock, catalog, orders).InspectionAsync(tx, id, ct);
    internal async Task<SupplierDocumentHistory> DocumentAsync(IClientSessionHandle tx, long id, CancellationToken ct, List<(string Table, BsonDocument Row)>? fences = null) {
        var route = await Rows(D.Submissions).Find(tx, new BsonDocument("_id", id)).Project(new BsonDocument("draftId", 1)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var history = await new SupplierDocumentRecords(database, unit, clock, catalog, orders, qualityGraph).HistoryAsync(tx, route["draftId"].AsInt64, ct);
        if (history.Document is null || history.Document["_id"] != id || history.Document["input"]["kind"] != "invoice") throw B.Broken();
        var sourceFences = await new SupplierDocumentRecords(database, unit, clock, catalog, orders, qualityGraph).RequireSourceCapacitiesAsync(tx, history, ct);
        fences?.AddRange(sourceFences); return history;
    }
    internal async Task<SupplierClaimSources> ResolveAsync(IClientSessionHandle tx, SupplierClaimInput input, BsonDocument source, CancellationToken ct) {
        var inspections = new Dictionary<long, ProcurementInspectionHistory>(); var documents = new Dictionary<long, SupplierDocumentHistory>(); var lines = new BsonArray(); var fences = new List<(string Table, BsonDocument Row)>();
        Equal(source["order"]["_id"], input.OrderId); Equal(source["order"]["version"], input.OrderVersion); Equal(source["order"]["approvalId"], input.OrderApprovalId);
        Equal(source["order"]["location"], R.Location(input.Location)); var supplier = source["proposal"]["input"]["supplierId"];
        foreach (var line in input.Lines) {
            if (!inspections.TryGetValue(line.InspectionId, out var history)) { history = await InspectionAsync(tx, line.InspectionId, ct); inspections.Add(line.InspectionId, history); }
            var inspection = history.Submissions.SingleOrDefault(row => row["_id"] == line.InspectionId) ?? throw B.Broken();
            Equal(history.Source["notice"]["source"], source); Equal(inspection["location"], R.Location(input.Location));
            if (line.InspectionLineNumber > inspection["input"]["lines"].AsBsonArray.Count || input.BusinessDate < PurchaseOrderBson.Date(inspection["input"]["inspectionDate"])) throw B.Changed();
            var observed = inspection["input"]["lines"][line.InspectionLineNumber - 1];
            var total = Q.Total(line.Basis, observed); if (line.Packages > total) throw B.Changed("supplier_claim_capacity_exceeded");
            var condition = line.Issue switch { "damaged" => "damagedPackages", "expired" => "expiredPackages", "opened" => "openedPackages", _ => null };
            if (condition is not null && line.Packages > observed[condition].AsInt32) throw B.Changed("supplier_claim_condition_exceeded");
            BsonValue invoice = BsonNull.Value;
            if (line.InvoiceDocumentId is { } documentId) {
                if (!documents.TryGetValue(documentId, out var documentHistory)) { documentHistory = await DocumentAsync(tx, documentId, ct, fences); documents.Add(documentId, documentHistory); }
                var document = documentHistory.Document!; Equal(document["location"], inspection["location"]); Equal(document["supplierId"], supplier);
                // An unmatched original needs the exact PO anchor. A genuine matched line can prove a different PO on a multi-order invoice.
                if (line.InvoiceMatchId is null) Equal(document["input"]["anchorOrderId"], input.OrderId);
                var index = line.InvoiceDocumentLineNumber!.Value; if (index > document["input"]["lines"].AsBsonArray.Count) throw B.Changed();
                BsonValue amount = BsonNull.Value;
                if (line.InvoiceMatchId is { } matchId) {
                    var match = documentHistory.Matches.SingleOrDefault(row => row["_id"] == matchId) ?? throw B.Broken(); var matchIndex = line.InvoiceMatchLineNumber!.Value;
                    if (matchIndex > match["lines"].AsBsonArray.Count) throw B.Changed(); var matched = match["lines"][matchIndex - 1];
                    if (matched["state"] != "matched" || matched["documentLineNumber"] != index || matched["inspectionId"] != line.InspectionId ||
                        matched["inspectionLineNumber"] != line.InspectionLineNumber || line.Basis != SupplierClaimBasis.PhysicalReceived || line.Packages > matched["packages"].AsInt32) throw B.Changed();
                    amount = new Money(new[] { "netAmount", "taxAmount", "chargeAmount" }.Sum(field => Money.Parse(matched[field].AsString).Value)).ToString();
                }
                invoice = new BsonDocument { { "documentId", documentId }, { "documentRevision", 1L }, { "documentReference", document["input"]["documentReference"] },
                    { "documentDate", document["input"]["documentDate"] }, { "documentLineNumber", index }, { "documentLine", document["input"]["lines"][index - 1] },
                    { "matchId", line.InvoiceMatchId is { } m ? m : BsonNull.Value }, { "matchLineNumber", line.InvoiceMatchLineNumber is { } n ? n : BsonNull.Value }, { "sourceInvoiceAmount", amount } };
            }
            lines.Add(new BsonDocument { { "lineNumber", lines.Count + 1 }, { "input", B.Line(line) }, { "inspection", InspectionView(inspection, line.InspectionLineNumber) }, { "invoice", invoice } });
        }
        return new(lines, inspections, documents, fences);
    }
    internal static BsonDocument InspectionView(BsonDocument row, int line) => new() { { "id", row["_id"] }, { "version", 1L }, { "hash", row["snapshotHash"] },
        { "inspectionDate", row["input"]["inspectionDate"] }, { "inspectionReference", row["input"]["inspectionReference"] }, { "noticeId", row["noticeId"] }, { "intakeId", row["intakeId"] }, { "line", row["input"]["lines"][line - 1] } };
    private static void People(BsonDocument row, params long[] users) {
        var people = row["people"].AsBsonArray.Select(R.Person).ToArray();
        if (!people.Select(person => person.UserId).SequenceEqual(users.Distinct().Order()) || people.Any(person => person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 })) throw B.Broken();
    }
    // Deliberately does not consult disposition heads: the disposition reader proves claims against this owner without recursion.
    internal async Task<BsonDocument> RequestAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await RowAsync(tx, B.Requests, id, ct); Equal(row["snapshotHash"], B.Hash(row)); var input = B.Input(row["input"]); Equal(row["input"], B.Input(input));
        Equal(row["claimId"], id); Equal(row["version"], 1L); Equal(row["location"], R.Location(input.Location)); Equal(row["orderId"], input.OrderId); Equal(row["claimReference"], input.ClaimReference); Equal(row["reason"], input.Reason);
        var source = await orders.FundingSourceAsync(database, tx, input.OrderId, input.OrderVersion, input.OrderApprovalId, ct); Equal(row["source"], source); Equal(row["supplierId"], source["proposal"]["input"]["supplierId"]);
        var at = new DateTimeOffset(row["recordedAt"].ToUniversalTime()); if (at > clock.UtcNow || input.BusinessDate > AccountingCalendar.BusinessDate(at) || source["approval"]["recordedAt"].CompareTo(row["recordedAt"]) > 0) throw B.Broken();
        var facts = await ResolveAsync(tx, input, source, ct); Equal(row["lines"], facts.Lines); People(row, row["actorId"].AsInt64);
        if (row["capacity"].AsBsonArray.Count != input.Lines.Length || row["allocations"].AsBsonArray.Count != input.Lines.Length) throw B.Broken();
        for (var index = 0; index < input.Lines.Length; index++) {
            var line = input.Lines[index]; var allocation = row["allocations"][index]; var before = row["capacity"][index].AsBsonDocument;
            var actual = facts.Lines[index]["inspection"]; Q.RequireCapacity(before, line.Basis, line.InspectionId, line.InspectionLineNumber, actual["hash"].AsString, Q.Total(line.Basis, actual["line"]));
            foreach (var field in new[] { "basis", "inspectionId", "inspectionLineNumber", "inspectionHash" }) Equal(allocation[field], before[field]);
            Equal(allocation["packages"], line.Packages); Equal(allocation["before"], before);
            Equal(allocation["after"], Q.After(before, allocation["headId"].AsInt64, line.Packages));
            if (allocation["allocationId"].AsInt64 <= 0 || allocation["headId"].AsInt64 <= 0 || (before["version"] == 0L) != allocation["previousAllocationId"].IsBsonNull ||
                allocation["previousAllocationId"].IsBsonNull != allocation["previousHash"].IsBsonNull) throw B.Broken();
            if (facts.Inspections[line.InspectionId].Submissions.Single(value => value["_id"] == line.InspectionId)["recordedAt"].CompareTo(row["recordedAt"]) > 0) throw B.Broken();
        }
        Equal(row["binding"], B.Binding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, input, source, facts.Lines, row["capacity"].AsBsonArray));
        await EffectsAsync(tx, row, B.RequestBody(input, row["binding"].AsString), null, ct); return row;
    }
    internal async Task<BsonDocument?> DecisionAsync(IClientSessionHandle tx, BsonDocument request, CancellationToken ct) {
        var raw = await Rows(B.Decisions).Find(tx, new BsonDocument("claimId", request["_id"])).SingleOrDefaultAsync(ct);
        if (raw is null) {
            foreach (var (table, field, result) in new[] { ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload"), ("idempotencyRecords", "operation", "result") })
                if (await Rows(table).Find(tx, new BsonDocument { { field, new BsonDocument("$in", new BsonArray { B.Prefix + "approve", B.Prefix + "reject" }) }, { result + ".claimId", request["_id"] } }).AnyAsync(ct)) throw B.Broken();
            return null;
        }
        var row = await RowAsync(tx, B.Decisions, raw["_id"].AsInt64, ct); Equal(row["snapshotHash"], B.Hash(row));
        foreach (var field in new[] { "claimId", "location", "orderId", "supplierId" }) Equal(row[field], request[field]); Equal(row["requestHash"], request["snapshotHash"]); Equal(row["version"], 1L);
        var input = B.DecisionInput(row["input"]); Equal(row["input"], B.DecisionInput(input)); Equal(row["decision"], B.Decision(input.Decision));
        if (row["recordedAt"].CompareTo(request["recordedAt"]) < 0 || row["recordedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime || row["capacity"].AsBsonArray.Count != request["capacity"].AsBsonArray.Count) throw B.Broken();
        for (var index = 0; index < row["capacity"].AsBsonArray.Count; index++) {
            var cap = row["capacity"][index].AsBsonDocument; var original = request["allocations"][index]["after"];
            Q.RequireCapacity(cap, B.Basis(original["basis"]), original["inspectionId"].AsInt64, original["inspectionLineNumber"].AsInt32, original["inspectionHash"].AsString, original["totalPackages"].AsInt64);
            if (cap["headId"] != original["headId"] || cap["version"].AsInt64 < original["version"].AsInt64 || cap["occupiedPackages"].AsInt64 < original["occupiedPackages"].AsInt64) throw B.Broken();
            var allocation = await Rows(Q.Allocations(B.Basis(cap["basis"]))).Find(tx, new BsonDocument { { "headId", cap["headId"] }, { "sequence", cap["version"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            if (allocation["recordedAt"].CompareTo(row["recordedAt"]) > 0) throw B.Broken();
            if (allocation["schemaVersion"] == 1 && allocation["kind"] == "supplier_claim") {
                BsonDocument claim;
                if (allocation["claimId"] == request["_id"]) {
                    // The caller supplied this fully authenticated request in the same
                    // transaction. Check its current immutable bytes before reusing it;
                    // a different claim still takes the full retained-owner path.
                    var current = await Rows(B.Requests).Find(tx, new BsonDocument("_id", request["_id"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
                    Equal(current, request); claim = request;
                } else claim = await RequestAsync(tx, allocation["claimId"].AsInt64, ct);
                var claimIndex = allocation["claimLineNumber"].AsInt32 - 1;
                if (claimIndex < 0 || claimIndex >= claim["allocations"].AsBsonArray.Count) throw B.Broken(); Equal(allocation, Q.Allocation(claim, claimIndex));
            } else if (B.Basis(cap["basis"]) == SupplierClaimBasis.PhysicalReceived && allocation["schemaVersion"] == 2 && allocation["kind"] == "held_receipt")
                Equal(allocation, ProcurementStockReceiptBson.Allocation(await HeldReceipts.ReadAsync(tx, allocation["receiptId"].AsInt64, ct)));
            else throw B.Broken();
            Equal(cap, allocation["after"]);
        }
        People(row, row["actorId"].AsInt64, request["actorId"].AsInt64); var people = row["people"].AsBsonArray.Select(R.Person).ToDictionary(person => person.UserId); var maker = R.Person(request["people"][0]);
        if (people[maker.UserId].PersonId != maker.PersonId || !PersonIndependencePolicy.AreIndependent(people[row["actorId"].AsInt64], people[maker.UserId], maker)) throw B.Broken();
        Equal(row["binding"], B.DecisionBinding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, request, input.Decision, row["capacity"].AsBsonArray));
        await EffectsAsync(tx, row, B.DecisionBody(request["_id"].AsInt64, 1, input, row["binding"].AsString), request, ct); return row;
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, object body, BsonDocument? request, CancellationToken ct) {
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, row["reason"].AsString)); Equal(await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", row["operation"] }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        string[] fields = ["_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result"];
        if (request is not null) { fields = [.. fields, PersonApprovalReceipt.Field]; if (PersonApprovalReceipt.Read(receipt, Permissions.SupplierClaimApprove) != new ApprovalRequest(1, 1, RequestedBy: request["actorId"].AsInt64)) throw B.Broken(); }
        if (receipt.ElementCount != fields.Length || fields.Any(field => !receipt.Contains(field)) || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || receipt["createdAt"].CompareTo(row["recordedAt"]) > 0) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row));
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.EffectKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        foreach (var field in new[] { "actorId", "requestKey", "location", "orderId" }) Equal(effect[field], row[field]); Equal(effect["kind"], row["operation"]); Equal(effect["payload"], B.Result(row)); Equal(effect["createdAt"], row["recordedAt"]);
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string operation, string key, CancellationToken ct) {
        var table = operation == B.Prefix + "request" ? B.Requests : B.Decisions;
        var row = await Rows(table).Find(tx, new BsonDocument { { "actorId", actor }, { "operation", operation }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is null) {
            foreach (var (name, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                if (await Rows(name).Find(tx, new BsonDocument { { "actorId", actor }, { field, operation }, { name == "idempotencyRecords" ? "key" : "requestKey", key } }).AnyAsync(ct)) throw B.Broken(); return null;
        }
        var request = await RequestAsync(tx, row["claimId"].AsInt64, ct); return table == B.Requests ? request : await DecisionAsync(tx, request, ct) ?? throw B.Broken();
    }
    internal async Task OrphansAsync(IClientSessionHandle tx, CancellationToken ct) {
        foreach (var (table, field, result) in new[] { ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload"), ("idempotencyRecords", "operation", "result") })
        foreach (var operation in new[] { B.Prefix + "request", B.Prefix + "approve", B.Prefix + "reject" }) {
            var request = operation == B.Prefix + "request"; var rows = await Rows(table).Aggregate<BsonDocument>(tx, new BsonDocument[] {
                new("$match", new BsonDocument(field, operation)), new("$lookup", new BsonDocument { { "from", request ? B.Requests : B.Decisions }, { "localField", result + (request ? ".claimId" : ".decisionId") }, { "foreignField", "_id" }, { "as", "owner" } }),
                new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1) }).ToListAsync(ct);
            if (rows.Count != 0) throw B.Broken();
        }
        var orphan = await Rows(B.Decisions).Aggregate<BsonDocument>(tx, new BsonDocument[] { new("$lookup", new BsonDocument { { "from", B.Requests }, { "localField", "claimId" }, { "foreignField", "_id" }, { "as", "owner" } }),
            new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1) }).ToListAsync(ct); if (orphan.Count != 0) throw B.Broken();
    }
}
