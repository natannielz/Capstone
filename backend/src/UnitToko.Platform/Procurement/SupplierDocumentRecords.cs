using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Documents;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.SupplierDocumentBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal sealed record SupplierDocumentHistory(BsonDocument Draft, BsonDocument? Document, BsonDocument Head, BsonDocument Identity, BsonDocument Capacity, IReadOnlyList<BsonDocument> Matches);
internal sealed class SupplierDocumentRecords(IMongoDatabase database, MongoUnitOfWork unit, IClock clock, CatalogAuthoringService catalog, PurchaseOrderService orders)
{
    private IMongoCollection<BsonDocument> Rows(string table) => database.GetCollection<BsonDocument>(table);
    internal async Task<ProcurementInspectionHistory> InspectionAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await Rows(ProcurementInspectionBson.Submissions).Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        return await new ProcurementInspectionRecords(database, unit, clock, catalog, orders).HistoryAsync(tx, row["noticeId"].AsInt64, row["intakeId"].AsInt64, ct);
    }
    internal static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string table, long id, CancellationToken ct) => await Rows(table).Find(tx,
        new BsonDocument { { "_id", id }, { "$and", new BsonArray { SupplierDocumentSchemas.Validator(table) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
    internal async Task<BsonDocument> DraftAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await RowAsync(tx, B.Drafts, id, ct); var input = B.Input(row["input"]); Equal(row["input"], B.Input(input)); Equal(row["snapshotHash"], B.DraftHash(row));
        Equal(row["draftId"], id); Equal(row["location"], R.Location(input.Location));
        var source = await orders.FundingSourceAsync(database, tx, input.AnchorOrderId, input.AnchorOrderVersion, input.AnchorOrderApprovalId, ct); Equal(row["source"], source);
        Equal(row["supplierId"], source["proposal"]["input"]["supplierId"]); Equal(source["order"]["location"], row["location"]);
        if (input.DocumentDate is { } day && day > AccountingCalendar.BusinessDate(Time(row["recordedAt"])) || source["approval"]["recordedAt"].CompareTo(row["recordedAt"]) > 0) throw B.Broken();
        Person(row); Equal(row["binding"], B.Binding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, row["input"].AsBsonDocument, source,
            new() { { "identityKey", B.IdentityKey(input, row["supplierId"].AsInt64) }, { "identityVersion", row["identityVersion"].AsInt64 - 1 } }));
        await EffectsAsync(tx, row, B.DraftBody(input, row["binding"].AsString), ct); return row;
    }
    internal async Task<BsonDocument> SubmissionAsync(IClientSessionHandle tx, long id, CancellationToken ct, BsonDocument? draft = null, bool pendingPin = false) {
        var row = await RowAsync(tx, B.Submissions, id, ct); draft ??= await DraftAsync(tx, row["draftId"].AsInt64, ct);
        Equal(row["snapshotHash"], B.Hash(row)); Equal(row["draftHash"], draft["snapshotHash"]); Equal(draft["submittedDocumentId"], row["_id"]);
        foreach (var field in new[] { "location", "supplierId", "identityId", "input", "source", "actorId" }) Equal(row[field], draft[field]);
        Person(row); Equal(row["people"][0]["personId"], draft["people"][0]["personId"]); if (row["recordedAt"].CompareTo(draft["recordedAt"]) < 0) throw B.Broken();
        var reference = PaymentSubmissionBson.Reference(row["evidence"].AsBsonDocument);
        Equal(row["binding"], B.SubmitBinding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, row["draftHash"].AsString, row["reservationId"].AsInt64, reference));
        await EffectsAsync(tx, row, B.SubmitBody(row["draftId"].AsInt64, 1, row["reservationId"].AsInt64, reference, row["binding"].AsString), ct);
        var job = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.OcrKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(job["kind"], "supplierdocument.ocr.requested"); Equal(job["payload"], B.OcrPayload(row)); Equal(job["createdAt"], row["recordedAt"]);
        if (!pendingPin) await RequirePinAsync(tx, row, ct); return row;
    }
    internal async Task RequirePinAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        var pin = await EvidencePinRecords.ReadAsync(database, tx, row["pinId"].AsInt64, ct); await new EvidenceService(database, unit, new(database), clock).ValidateHistoricalPinAsync(tx, pin, ct);
        foreach (var (key, value) in new (string, BsonValue)[] { ("associationKind", "SupplierDocument"), ("associationId", row["_id"]), ("supplierDocumentDraftId", row["draftId"]),
            ("supplierDocumentDraftRevision", 1L), ("reference", row["evidence"]), ("reservationId", row["reservationId"]), ("actorId", row["actorId"]), ("actorGrantVersion", row["actorGrantVersion"]),
            ("associationHash", row["snapshotHash"]), ("createdAt", row["recordedAt"]), ("reason", row["reason"]), ("correlationId", row["correlationId"]) }) Equal(pin[key], value);
    }
    internal async Task<BsonDocument> IdentityAsync(IClientSessionHandle tx, BsonDocument draft, CancellationToken ct) {
        var row = await RowAsync(tx, B.Identities, draft["identityId"].AsInt64, ct); var input = B.Input(draft["input"]); var key = B.IdentityKey(input, draft["supplierId"].AsInt64);
        Equal(row["identityKey"], key); Equal(row["supplierId"], draft["supplierId"]); Equal(row["kind"], draft["input"]["kind"]); Equal(row["reference"], draft["input"]["documentReference"]);
        Equal(row["organizationId"], input.Location.OrganizationId); Equal(row["shopId"], input.Location.ShopId); Equal(row["channelId"], input.Location.ChannelId);
        var drafts = await Rows(B.Drafts).Find(tx, new BsonDocument("identityId", row["_id"])).Sort(new BsonDocument("identityVersion", 1)).ToListAsync(ct);
        Equal(row["version"], (long)drafts.Count); Equal(row["draftCount"], (long)drafts.Count);
        if (drafts.Count == 0 || drafts[0]["_id"] != row["firstDraftId"]) throw B.Broken();
        for (var i = 0; i < drafts.Count; i++) {
            var original = drafts[i]["_id"] == draft["_id"] ? draft : await DraftAsync(tx, drafts[i]["_id"].AsInt64, ct);
            Equal(original["identityVersion"], (long)i + 1); Equal(B.IdentityKey(B.Input(original["input"]), original["supplierId"].AsInt64), key); Equal(original["snapshotHash"], B.DraftHash(original));
        }
        Equal(row["createdAt"], drafts[0]["recordedAt"]); Equal(row["updatedAt"], drafts[^1]["recordedAt"]); return row;
    }
    internal async Task<SupplierDocumentHistory> HistoryAsync(IClientSessionHandle tx, long draftId, CancellationToken ct) {
        var draft = await DraftAsync(tx, draftId, ct); var identity = await IdentityAsync(tx, draft, ct);
        var documents = await Rows(B.Submissions).Find(tx, new BsonDocument("draftId", draftId)).Limit(2).ToListAsync(ct);
        if (documents.Count > 1 || draft["submittedDocumentId"].IsBsonNull != (documents.Count == 0)) throw B.Broken();
        var document = documents.Count == 0 ? null : await SubmissionAsync(tx, documents[0]["_id"].AsInt64, ct, draft);
        var head = await RowAsync(tx, B.Heads, draftId, ct); Equal(head["draftId"], draftId); Equal(head["draftHash"], draft["snapshotHash"]); Equal(head["location"], draft["location"]); Equal(head["documentId"], draft["submittedDocumentId"]);
        var matches = await Rows(B.Matches).Find(tx, new BsonDocument("draftId", draftId)).Sort(new BsonDocument("sequence", 1)).ToListAsync(ct);
        var capacity = B.Capacity(draft["input"]["lines"].AsBsonArray); BsonValue previous = BsonNull.Value, hash = BsonNull.Value, at = draft["recordedAt"], reviewed = BsonNull.Value;
        foreach (var raw in matches) {
            if (document is null) throw B.Broken(); var row = await MatchAsync(tx, raw["_id"].AsInt64, ct, document);
            Equal(row["before"], capacity); Equal(row["previousMatchId"], previous); Equal(row["previousHash"], hash); var matchInput = B.MatchInput(row["input"]);
            if (matchInput.Lines.Any(line => line.Discrepancy is null)) { if (!reviewed.IsBsonNull) Equal(reviewed, row["input"]["reviewedInput"]); reviewed = row["input"]["reviewedInput"]; }
            capacity = B.After(capacity, matchInput); Equal(row["after"], capacity); Equal(row["sequence"], capacity["version"]);
            if (row["recordedAt"].CompareTo(at) < 0) throw B.Broken(); previous = row["_id"]; hash = row["snapshotHash"]; at = row["recordedAt"];
        }
        Equal(head["version"], capacity["version"]); Equal(head["lines"], capacity["lines"]); Equal(head["lastMatchId"], previous); Equal(head["lastHash"], hash);
        if (document is not null && document["input"]["kind"] == "sph") {
            foreach (var group in matches.SelectMany(row => row["lines"].AsBsonArray).Where(line => line["state"] == "matched").GroupBy(line => (line["invoiceMatchId"].AsInt64, line["invoiceMatchLineNumber"].AsInt32)))
                if (group.Sum(line => Money.Parse(line["statementAmount"].AsString).Value) > Money.Parse(group.First()["invoiceMatchedAmount"].AsString).Value) throw B.Broken();
        }
        Equal(head["reviewedInput"], reviewed); Equal(head["createdAt"], draft["recordedAt"]); Equal(head["updatedAt"], at); await OrphansAsync(tx, ct);
        return new(draft, document, head, identity, capacity, matches);
    }
    internal async Task<BsonDocument> MatchAsync(IClientSessionHandle tx, long id, CancellationToken ct, BsonDocument? document = null) {
        var row = await RowAsync(tx, B.Matches, id, ct); Equal(row["snapshotHash"], B.Hash(row)); document ??= await SubmissionAsync(tx, row["documentId"].AsInt64, ct);
        Equal(row["documentId"], document["_id"]); Equal(row["documentHash"], document["snapshotHash"]); foreach (var key in new[] { "draftId", "location", "supplierId", "identityId" }) Equal(row[key], document[key]);
        Person(row); if (row["recordedAt"].CompareTo(document["recordedAt"]) < 0) throw B.Broken(); var input = B.MatchInput(row["input"]); Equal(input.DocumentId, document["_id"]);
        var resolved = await ResolveAsync(tx, document, input, ct); Equal(row["lines"], resolved.Lines); Equal(row["links"], resolved.Links);
        var inspectionLinks = resolved.Links.Where(link => !link.IsBsonNull && link["kind"] == "inspection").ToArray();
        var expected = inspectionLinks.Select(link => (Kind: "inspection", Id: link["id"].AsInt64, Sku: (long?)null))
            .Concat(inspectionLinks.Select(link => (Kind: "order", Id: link["orderId"].AsInt64, Sku: (long?)link["requestSkuId"].AsInt64))).Distinct().ToArray();
        var claims = row["claims"].AsBsonArray;
        if (claims.Count != expected.Length) throw B.Broken();
        foreach (var key in expected) {
            var claim = claims.SingleOrDefault(value => value["kind"] == key.Kind && value["sourceId"] == key.Id && value["skuId"] == (key.Sku is { } sku ? (BsonValue)sku : BsonNull.Value)) ?? throw B.Broken();
            var review = row["review"][key.Kind == "inspection" ? "inspectionCapacities" : "orderCapacities"].AsBsonArray.SingleOrDefault(value =>
                value[key.Kind == "inspection" ? "inspectionId" : "orderId"] == key.Id && (key.Kind == "inspection" || value["requestSkuId"] == key.Sku!.Value)) ?? throw B.Broken();
            Equal(review["capacity"], claim["before"]);
        }
        if (input.OcrResultId is { } resultId) {
            var job = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.OcrKey(document))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            var location = R.Location(document["location"]);
            var source = new SupplierDocumentOcrSourceSnapshot(document["_id"].AsInt64, document["draftId"].AsInt64, 1, document["reservationId"].AsInt64, document["pinId"].AsInt64,
                document["actorId"].AsInt64, PaymentSubmissionBson.Reference(document["evidence"].AsBsonDocument), document["snapshotHash"].AsString, document["draftHash"].AsString,
                new(GrantScopeKind.FulfillmentShop, location.OrganizationId, ShopId: location.ShopId, ChannelId: location.ChannelId));
            _ = await new SupplierDocumentExtractionRecords(database).RequireResultAsync(tx, job, source, resultId, ct);
        }
        if (input.ReviewedInput.DocumentDate is { } day && day > AccountingCalendar.BusinessDate(Time(row["recordedAt"]))) throw B.Broken();
        Equal(row["binding"], B.Binding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, row["input"].AsBsonDocument, new() { { "documentHash", document["snapshotHash"] }, { "links", row["links"] } }, row["review"].AsBsonDocument));
        Equal(row["review"]["capacity"], row["before"]); Equal(row["after"], B.After(row["before"].AsBsonDocument, input)); await EffectsAsync(tx, row, B.MatchBody(input, row["binding"].AsString), ct); return row;
    }
    internal async Task<(BsonArray Lines, BsonArray Links, Dictionary<long, ProcurementInspectionHistory> Inspections)> ResolveAsync(IClientSessionHandle tx, BsonDocument document, SupplierDocumentMatchInput input, CancellationToken ct) {
        var result = new BsonArray(); var links = new BsonArray(); var inspections = new Dictionary<long, ProcurementInspectionHistory>(); var value = input.ReviewedInput;
        SupplierDocumentRules.SameIdentity(B.Input(document["input"]), value);
        if (input.Lines.Any(line => line.Discrepancy is null) && (!SupplierDocumentRules.ExactTotal(value) || value.Discrepancy is not null)) throw B.Changed("supplier_document_discrepancy_requires_resolution");
        foreach (var requested in input.Lines) {
            if (requested.DocumentLineNumber > value.Lines.Length) throw B.Changed(); var declared = value.Lines[requested.DocumentLineNumber - 1];
            if (requested.Packages is { } count && declared.Packages is { } declaredCount && count > declaredCount) throw B.Changed();
            var line = B.MatchLine(requested);
            line.Add("state", requested.Discrepancy is null ? "matched" : "discrepancy_pending"); line.Add("order", BsonNull.Value); line.Add("inspectionLine", BsonNull.Value); line.Add("invoiceDocumentReference", BsonNull.Value); line.Add("invoiceMatchedAmount", BsonNull.Value);
            if (requested.Discrepancy is not null) { result.Add(line); links.Add(BsonNull.Value); continue; }
            if (value.Kind == SupplierDocumentKind.Invoice) {
                if (requested.InspectionId is not { } inspectionId || requested.InspectionLineNumber is not { } index) throw B.Changed();
                if (!inspections.TryGetValue(inspectionId, out var history)) {
                    var route = await Rows(ProcurementInspectionBson.Submissions).Find(tx, new BsonDocument("_id", inspectionId)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
                    Equal(route["location"], document["location"]);
                    history = await new ProcurementInspectionRecords(database, unit, clock, catalog, orders).HistoryAsync(tx, route["noticeId"].AsInt64, route["intakeId"].AsInt64, ct); inspections.Add(inspectionId, history);
                }
                var inspection = history.Submissions.SingleOrDefault(row => row["_id"] == inspectionId) ?? throw B.Broken();
                if (index > inspection["input"]["lines"].AsBsonArray.Count) throw B.Changed(); var observed = inspection["input"]["lines"][index - 1]; var source = history.Source["notice"]["source"].AsBsonDocument;
                Equal(source["proposal"]["input"]["supplierId"], document["supplierId"]);
                var quoted = source["proposal"]["input"]["lines"].AsBsonArray.SingleOrDefault(row => row["requestSkuId"] == observed["requestSkuId"]) ?? throw B.Broken();
                Equal(observed["definitionId"], quoted["definitionId"]); Equal(observed["definitionVersion"], quoted["definitionVersion"]);
                if (requested.Packages > observed["receivedPackages"].AsInt32 || declared.SourceSystem is null || declared.SourceAccount is null || declared.SourceCode is null ||
                    declared.SourceSystem != quoted["sourceSystem"].AsString || declared.SourceAccount != quoted["sourceAccountReference"].AsString || declared.SourceCode != quoted["externalItemCode"].AsString ||
                    declared.NetPricePerPackage != Money.Parse(quoted["netPricePerPackage"].AsString) || observed["damagedPackages"].AsInt32 > 0 || observed["expiredPackages"].AsInt32 > 0 || observed["openedPackages"].AsInt32 > 0 ||
                    !observed["discrepancy"].IsBsonNull || observed["declaredPackages"] != observed["receivedPackages"]) throw B.Changed("supplier_document_discrepancy_requires_resolution");
                Equal(line["netAmount"], Money.Multiply(Money.Parse(quoted["netPricePerPackage"].AsString), requested.Packages!.Value).ToString());
                if (requested.TaxAmount!.Value.Value > Money.Parse(quoted["taxAmount"].AsString).Value || requested.ChargeAmount!.Value.Value > Money.Parse(quoted["supplierChargeAmount"].AsString).Value) throw B.Changed("supplier_document_discrepancy_requires_resolution");
                line["order"] = PurchaseOrderService.FundingOrderView(source); line["inspectionLine"] = observed;
                links.Add(new BsonDocument { { "kind", "inspection" }, { "id", inspectionId }, { "lineNumber", index }, { "hash", inspection["snapshotHash"] },
                    { "noticeId", inspection["noticeId"] }, { "intakeId", inspection["intakeId"] }, { "orderId", source["order"]["_id"] }, { "orderHash", source["approval"]["snapshotHash"] }, { "requestSkuId", observed["requestSkuId"] } });
            } else {
                if (requested.InvoiceMatchId is not { } invoiceId || requested.InvoiceMatchLineNumber is not { } index) throw B.Changed();
                var raw = await RowAsync(tx, B.Matches, invoiceId, ct); var invoice = await SubmissionAsync(tx, raw["documentId"].AsInt64, ct);
                if (invoice["input"]["kind"] != "invoice") throw B.Changed(); Equal(invoice["location"], document["location"]); Equal(invoice["supplierId"], document["supplierId"]);
                var match = await MatchAsync(tx, invoiceId, ct, invoice); if (index > match["lines"].AsBsonArray.Count) throw B.Changed(); var actual = match["lines"][index - 1];
                if (actual["state"] != "matched" || declared.RelatedInvoiceReference != invoice["input"]["documentReference"].AsString) throw B.Changed();
                var maximum = new Money(new[] { "netAmount", "taxAmount", "chargeAmount" }.Sum(field => Money.Parse(actual[field].AsString).Value));
                if (requested.StatementAmount!.Value.Value > maximum.Value) throw B.Changed("supplier_document_capacity_exceeded");
                line["order"] = actual["order"]; line["inspectionLine"] = actual["inspectionLine"]; line["invoiceDocumentReference"] = invoice["input"]["documentReference"]; line["invoiceMatchedAmount"] = maximum.ToString();
                links.Add(new BsonDocument { { "kind", "invoice_match" }, { "id", invoiceId }, { "lineNumber", index }, { "hash", match["snapshotHash"] }, { "documentId", invoice["_id"] }, { "documentHash", invoice["snapshotHash"] } });
            }
            result.Add(line);
        }
        return (result, links, inspections);
    }
    internal async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, object body, CancellationToken ct) {
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, row["reason"].AsString)); Equal(await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", row["operation"] }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (receipt.ElementCount != 8 || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || receipt["createdAt"].CompareTo(row["recordedAt"]) > 0) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row)); var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.EffectKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        foreach (var field in new[] { "actorId", "requestKey", "location" }) Equal(effect[field], row[field]); Equal(effect["kind"], row["operation"]); Equal(effect["payload"], B.Result(row)); Equal(effect["createdAt"], row["recordedAt"]);
    }
    private static void Person(BsonDocument row) { var proof = row["people"].AsBsonArray.Select(R.Person).ToArray(); if (proof.Length != 1 || proof[0] is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 } || proof[0].UserId != row["actorId"].AsInt64) throw B.Broken(); }
    internal async Task OrphansAsync(IClientSessionHandle tx, CancellationToken ct) {
        foreach (var (table, op, id) in new[] { (B.Drafts, B.DraftOperation, "draftId"), (B.Submissions, B.SubmitOperation, "documentId"), (B.Matches, B.MatchOperation, "matchId") })
        foreach (var (effects, kind, result) in new[] { ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload"), ("idempotencyRecords", "operation", "result") }) {
            var missing = await Rows(effects).Aggregate<BsonDocument>(tx, new BsonDocument[] { new("$match", new BsonDocument(kind, op)),
                new("$lookup", new BsonDocument { { "from", table }, { "localField", result + "." + id }, { "foreignField", "_id" }, { "as", "owner" } }), new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1) }).ToListAsync(ct);
            if (missing.Count != 0) throw B.Broken();
        }
        foreach (var (table, local, owner, foreign) in new[] { (B.Heads, "draftId", B.Drafts, "_id"), (B.Identities, "firstDraftId", B.Drafts, "_id"),
            (B.InspectionHeads, "lastMatchId", B.Matches, "_id"), (B.OrderHeads, "lastMatchId", B.Matches, "_id") }) {
            var missing = await Rows(table).Aggregate<BsonDocument>(tx, new BsonDocument[] {
                new("$lookup", new BsonDocument { { "from", owner }, { "localField", local }, { "foreignField", foreign }, { "as", "owner" } }),
                new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1) }).ToListAsync(ct);
            if (missing.Count != 0) throw B.Broken();
        }
        var missingJobOwner = await Rows("outboxEvents").Aggregate<BsonDocument>(tx, new BsonDocument[] {
            new("$match", new BsonDocument("kind", "supplierdocument.ocr.requested")),
            new("$lookup", new BsonDocument { { "from", B.Submissions }, { "localField", "payload.submissionId" }, { "foreignField", "_id" }, { "as", "owner" } }),
            new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1) }).ToListAsync(ct);
        if (missingJobOwner.Count != 0) throw B.Broken();
    }
    private static DateTimeOffset Time(BsonValue value) => new(value.ToUniversalTime());
}
