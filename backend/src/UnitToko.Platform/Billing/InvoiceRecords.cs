using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Billing;

internal sealed class InvoiceRecords(IMongoDatabase database, ShipmentReceiptService receipts)
{
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static AuthorizationDeniedException Invalid() => new("invoice_unavailable");
    internal static bool Equal(BsonValue left, BsonValue right) => CatalogSchemas.SemanticEquals(left, right);
    internal async Task<BsonDocument> VerifyAsync(ActorContext actor, IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        foreach (var source in InvoiceBson.Sources(row["sources"].AsBsonArray)) await receipts.RequireInvoiceHistoryAsync(actor, tx, source, ct);
        return await VerifyPersistedAsync(database, tx, row, ct);
    }
    // Deployment-only consumers validate persisted business anchors without inventing a live actor.
    // Runtime reads still validate every receipt/shipment/commercial owner above.
    internal static async Task<BsonDocument> VerifyPersistedAsync(IMongoDatabase database, IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
        if (!await Rows(InvoiceBson.Invoices).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", row["_id"]) & InvoiceSchemas.Validator(InvoiceBson.Invoices)).AnyAsync(ct)) throw Invalid();
        var sources = InvoiceBson.Sources(row["sources"].AsBsonArray); var terms = InvoiceBson.Terms(row["terms"].AsBsonDocument);
        var issued = ShipmentReceiptRecords.Time(row["issuedAt"]); var date = AccountingCalendar.BusinessDate(issued);
        InvoiceAdmissionPolicy.Require(sources, terms, issued);
        var orderIds = sources.Select(source => source.Snapshot["commercial"]["orderId"].AsInt64).Distinct().Order().ToArray();
        if (!Equal(row["orderIds"], new BsonArray(orderIds.Select(id => new BsonInt64(id)))) || !Equal(row["terms"], InvoiceBson.Terms(terms)) ||
            !Equal(row["scopes"], new BsonArray(sources.Select(source => ShipmentPreparationBson.Scope(source.Resource)))) ||
            row["numberEntity"] != sources[0].Resource.FulfillmentOrganizationId || row["numberChannel"] != sources[0].Resource.ChannelId || row["numberYear"] != (long)date.Year ||
            InvoiceBson.Date(row["issueDate"]) != date || InvoiceBson.Date(row["dueDate"]) != InvoiceAdmissionPolicy.DueDate(sources, terms, date) ||
            row["postingFence"] != 1L || row["journalId"].IsBsonNull || row["journalFingerprint"].IsBsonNull || row["snapshotHash"] != InvoiceBson.Hash(row) ||
            row["review"] != InvoiceBson.Review(row["sources"].AsBsonArray, row["terms"].AsBsonDocument, row["lines"].AsBsonArray, date, InvoiceBson.Date(row["dueDate"])) ||
            row["bodyHash"] != AuthorizationStore.CommandHash(InvoiceBson.Body(InvoiceBson.Selected(row), row["review"].AsString), row["reason"].AsString)) throw Invalid();
        var lines = row["lines"].AsBsonArray;
        var required = sources.Sum(source => source.Snapshot["receipt"]["lines"].AsBsonArray.Count);
        if (lines.Count != required || lines.Select(line => (line["receiptId"].AsInt64, line["skuId"].AsInt64)).Distinct().Count() != required ||
            lines.Select(line => line["allocationId"].AsInt64).Distinct().Count() != required) throw Invalid();
        foreach (var value in lines) {
            var line = value.AsBsonDocument;
            var source = sources.SingleOrDefault(source => source.Snapshot["receipt"]["id"] == line["receiptId"]) ?? throw Invalid();
            var calculated = Line(source, terms, line["skuId"].AsInt64, line["previousBaseUnits"].AsInt64, InvoiceBson.Money(line["previousNet"]));
            calculated.Add("allocationId", line["allocationId"]); if (!Equal(calculated, line)) throw Invalid();
        }
        if (!Equal(InvoiceBson.Totals(lines), row["totals"])) throw Invalid();
        var html = row["document"].AsString;
        if (html != InvoiceRenderer.Render(row) || row["documentHash"] != InvoiceRenderer.Hash(html) || row["documentLength"] != System.Text.Encoding.UTF8.GetByteCount(html)) throw Invalid();
        var allocations = await Rows(InvoiceBson.Allocations).Find(tx, new BsonDocument("invoiceId", row["_id"])).Limit(InvoicePolicy.MaximumLines + 1).ToListAsync(ct);
        if (allocations.Count != lines.Count) throw Invalid();
        foreach (var line in lines) if (!allocations.Any(allocation => Equal(allocation, InvoiceBson.Allocation(row, line.AsBsonDocument)))) throw Invalid();
        var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["issuedBy"] }, { "operation", InvoiceBson.Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
        if (audit is null || !Equal(audit, InvoiceBson.Audit(row)) || receipt is null || receipt.GetValue("bodyHash", BsonNull.Value) != row["bodyHash"] ||
            !Equal(receipt.GetValue("result", BsonNull.Value), InvoiceBson.Result(row))) throw Invalid();
        var journal = await Rows("journalEntries").Find(tx, new BsonDocument("_id", row["journalId"])).SingleOrDefaultAsync(ct) ?? throw Invalid();
        var draft = AccountingBson.ReadDraft(journal); var sourceSnapshot = InvoicePostingSource.Snapshot(row);
        if (draft.Source != sourceSnapshot.Source || draft.SourceRevision != 1 || draft.Date != sourceSnapshot.Date || draft.Book != sourceSnapshot.Book ||
            draft.Dimensions != sourceSnapshot.Dimensions || draft.ReversalOf is not null || draft.Fingerprint != row["journalFingerprint"].AsString ||
            journal["actorId"] != row["issuedBy"] || !draft.Lines.Select(line => new PostingLine(line.MappingKey, line.Debit, line.Credit)).SequenceEqual(sourceSnapshot.Lines)) throw Invalid();
        return row;
    }
    // Invoice lines reference the frozen ordered price/package and the accepted quantity only.
    internal static BsonDocument Line(InvoiceReceiptSource source, InvoiceTerms terms, long skuId, long previousBaseUnits, Money previousNet) {
        var snapshot = source.Snapshot; var receipt = snapshot["receipt"]; var commercial = snapshot["commercial"];
        var accepted = receipt["lines"].AsBsonArray.SingleOrDefault(line => line["skuId"] == skuId) ?? throw Invalid();
        var original = commercial["quote"]["lines"].AsBsonArray.SingleOrDefault(line => line["definition"]["skuId"] == skuId) ?? throw Invalid();
        var dispatch = snapshot["shipment"]["lines"].AsBsonArray.SingleOrDefault(line => line["skuId"] == skuId) ?? throw Invalid();
        var definition = CatalogRepository.Definition(original["definition"].AsBsonDocument);
        if (!Equal(original["definition"], dispatch["definition"]) || accepted["definitionId"] != definition.Id.Value ||
            accepted["acceptedBaseUnits"].AsInt64 > dispatch["baseUnits"].AsInt64 || original["owner"]["organizationId"] != source.Resource.FulfillmentOrganizationId ||
            original["owner"]["shopId"] != source.Resource.ShopId || original["owner"]["channelId"] != source.Resource.ChannelId) throw Invalid();
        var approved = terms.Lines.SingleOrDefault(line => line.ReceiptId == receipt["id"].AsInt64 && line.SkuId == skuId) ?? throw Invalid();
        var net = InvoicePolicy.AcceptedDelta(InvoiceBson.Money(original["price"]["unitPrice"]), definition.BaseUnitsPerPackage,
            original["baseUnits"].AsInt64, previousBaseUnits, previousNet, accepted["acceptedBaseUnits"].AsInt64, terms.Rounding);
        return new() { { "orderId", commercial["orderId"] }, { "shipmentId", snapshot["shipment"]["shipmentId"] }, { "receiptId", receipt["id"] }, { "skuId", skuId },
            { "skuCode", original["skuCode"] }, { "productName", original["familyName"] }, { "definition", original["definition"].DeepClone() }, { "price", original["price"].DeepClone() },
            { "orderedBaseUnits", original["baseUnits"] }, { "acceptedBaseUnits", accepted["acceptedBaseUnits"] }, { "previousBaseUnits", previousBaseUnits }, { "previousNet", InvoiceBson.Money(previousNet) },
            { "net", InvoiceBson.Money(net) }, { "tax", InvoiceBson.Money(approved.Tax) }, { "charges", InvoiceBson.Money(approved.Charges) } };
    }
    internal async Task<IReadOnlyList<BsonDocument>> OrderAllocationsAsync(ActorContext actor, IClientSessionHandle tx, long orderId, CancellationToken ct) {
        var invoices = new Dictionary<long, BsonDocument>();
        using (var cursor = await Rows(InvoiceBson.Invoices).Find(tx, new BsonDocument("orderIds", orderId)).ToCursorAsync(ct))
            while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current) invoices.Add(row["_id"].AsInt64, await VerifyAsync(actor, tx, row, ct));
        var allocations = await Rows(InvoiceBson.Allocations).Find(tx, new BsonDocument("orderId", orderId)).ToListAsync(ct);
        var expected = invoices.Values.SelectMany(invoice => invoice["lines"].AsBsonArray.Where(line => line["orderId"] == orderId).Select(line => InvoiceBson.Allocation(invoice, line.AsBsonDocument))).ToArray();
        if (expected.Length != allocations.Count || allocations.Any(row => !expected.Any(value => Equal(value, row)))) throw Invalid();
        foreach (var (name, filter, field) in new[] {
            ("auditEvents", new BsonDocument { { "operation", InvoiceBson.Operation }, { "orderIds", orderId } }, "result"),
            ("idempotencyRecords", new BsonDocument { { "operation", InvoiceBson.Operation }, { "result.orderIds", orderId } }, "result") }) {
            using var cursor = await Rows(name).Find(tx, filter).ToCursorAsync(ct); var seen = new HashSet<long>();
            while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current)
                if (!invoices.ContainsKey(row[field]["invoiceId"].AsInt64) || !seen.Add(row[field]["invoiceId"].AsInt64)) throw Invalid();
            if (seen.Count != invoices.Count) throw Invalid();
        }
        foreach (var group in allocations.GroupBy(row => row["skuId"].AsInt64)) {
            long units = 0; var net = new Money(0m); string? rounding = null;
            foreach (var row in group.OrderBy(row => row["previousBaseUnits"].AsInt64)) {
                if (row["previousBaseUnits"] != units || InvoiceBson.Money(row["previousNet"]) != net || rounding is not null && rounding != row["rounding"].AsString) throw Invalid();
                rounding = row["rounding"].AsString; units = checked(units + row["acceptedBaseUnits"].AsInt64); net = Money.Add(net, InvoiceBson.Money(row["net"]));
            }
        }
        if (invoices.Count > 0) {
            var original = invoices.Values.First()["sources"].AsBsonArray.First(source => source["snapshot"]["commercial"]["orderId"] == orderId)["snapshot"]["commercial"]["terms"];
            foreach (var field in new[] { "tax", "charges" })
                if (allocations.Aggregate(new Money(0m), (total, row) => Money.Add(total, InvoiceBson.Money(row[field]))).Value > InvoiceBson.Money(original[field]).Value) throw Invalid();
        }
        return allocations;
    }
}
