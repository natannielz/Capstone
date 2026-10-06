using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Fulfillment;

internal sealed record AfterSalesStockSourceEvent(string Kind, long Id, BsonDocument Record,
    long[] PoolIds, BsonDocument Audit, BsonValue Time, long? ReturnId)
{
    internal string Hash => CatalogRepository.Digest(new BsonDocument { { "kind", Kind }, { "record", Record } });
}

/// <summary>Dispatched provenance pools, never an assertion that unverified loose returns belong
/// to a particular lot. Existing owners authenticate custody, consent, quantities and original keys.</summary>
internal sealed class AfterSalesStockSourceEvents(IMongoDatabase database, IdAllocator ids, IClock clock)
{
    internal IClock Clock => clock;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private readonly FulfillmentStockSourceEvents fulfillment = new(database, ids, clock);
    internal async Task FenceTopologyAsync(IClientSessionHandle tx, BsonDocument source, CancellationToken ct)
    {
        var organizations = PaymentDraftBson.Scopes(source["scopes"].AsBsonArray)
            .Select(scope => scope.FulfillmentOrganizationId).Distinct().Order().ToArray();
        if (organizations.Length == 0 || organizations.Any(id => id is not > 0)) throw AfterSalesStockSourceRecords.Unavailable();
        foreach (var organization in organizations)
            await new StockSourceTopologyFence(database).FenceAsync(tx, organization!.Value, ct);
    }
    internal async Task<AfterSalesStockSourceEvent> ReadCausalAsync(IClientSessionHandle tx, string kind, long id, CancellationToken ct)
    {
        if (kind == "setup") return await ReadAsync(tx, kind, id, true, ct);
        if (kind is not ("request" or "decision")) throw AfterSalesStockSourceRecords.Unavailable();
        var root = await new AfterSalesCaseRecords(database).ReadAsync(tx, id, ct);
        var invoice = await Rows(InvoiceBson.Invoices).Find(tx, new BsonDocument("_id", root["source"]["invoiceId"])).SingleOrDefaultAsync(ct)
            ?? throw AfterSalesStockSourceRecords.Unavailable();
        await InvoiceRecords.VerifyPersistedAsync(database, tx, invoice, ct);
        if (!InvoiceRecords.Equal(InvoiceService.CaseSnapshot(invoice), root["source"])) throw AfterSalesStockSourceRecords.Unavailable();
        foreach (var line in root["source"]["lines"].AsBsonArray) {
            var dispatched = await fulfillment.ReadAsync(tx, "dispatch", line["shipmentId"].AsInt64, true, ct);
            var accepted = await fulfillment.ReadAsync(tx, "receipt", line["receiptId"].AsInt64, true, ct);
            if (accepted.ShipmentId != line["shipmentId"].AsInt64 || dispatched.Record["orderId"] != line["orderId"])
                throw AfterSalesStockSourceRecords.Unavailable();
        }
        var stock = await InvoiceService.CaseDispatchStockAsync(database, tx, root["source"].AsBsonDocument, ct);
        var selected = new BsonArray(stock.Where(line => root["input"]["lines"].AsBsonArray.Any(item =>
            item["receiptId"] == line["receiptId"] && item["skuId"] == line["skuId"])));
        var pools = CausalPools(selected);
        // Case read fences are mutable concurrency metadata, never part of this immutable cause.
        var immutable = root.DeepClone().AsBsonDocument; immutable.Remove("fence");
        var record = new BsonDocument { { "case", immutable }, { "stock", selected } };
        if (kind == "request") return new(kind, id, record, pools, AfterSalesCaseRecords.Audit(root), root["requestedAt"], null);
        var decisions = new AfterSalesDecisionRecords(database);
        var decision = await decisions.ReadAsync(tx, root, ct) ?? throw AfterSalesStockSourceRecords.Unavailable();
        await decisions.RequireCapacityAsync(tx, root, [], ct);
        record.Add("decision", decision);
        return new(kind, id, record, pools, AfterSalesDecisionRecords.Audit(decision), decision["decidedAt"], null);
    }
    internal static long[] CausalPools(BsonArray stock)
    {
        var pools = stock.SelectMany(line => line["originalLots"].AsBsonArray).Select(lot => lot["poolId"].AsInt64).Distinct().Order().ToArray();
        if (stock.Count == 0 || pools.Length is < 1 or > 100 || pools.Any(pool => pool is <= 0 or >= long.MaxValue - 1))
            throw AfterSalesStockSourceRecords.Unavailable();
        return pools;
    }
    internal async Task<AfterSalesStockSourceEvent> ReadAsync(IClientSessionHandle tx, string kind, long id,
        bool requireSetupReceipt, CancellationToken ct)
    {
        new InventoryRepository(database).RequireSession(tx);
        if (kind == "setup") {
            var setup = await new OrderStockSourceRecords(database).SetupAsync(tx, id, requireSetupReceipt, ct);
            return new(kind, id, setup.Record, setup.PoolIds, setup.Audit, setup.Time, null);
        }
        if (kind is not ("receive" or "inspect" or "verifyLot")) throw AfterSalesStockSourceRecords.Unavailable();
        var receipt = await Rows(ReturnQuarantineLedger.Collection).Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct)
            ?? throw AfterSalesStockSourceRecords.Unavailable();
        var root = await new AfterSalesCaseRecords(database).ReadAsync(tx, receipt["caseId"].AsInt64, ct);
        var decisionOwner = new AfterSalesDecisionRecords(database);
        var decision = await decisionOwner.ReadAsync(tx, root, ct) ?? throw AfterSalesStockSourceRecords.Unavailable();
        if (decision["state"] != "return_authorized") throw AfterSalesStockSourceRecords.Unavailable();
        await decisionOwner.RequireCapacityAsync(tx, root, [], ct);
        var invoice = await Rows(InvoiceBson.Invoices).Find(tx, new BsonDocument("_id", root["source"]["invoiceId"])).SingleOrDefaultAsync(ct)
            ?? throw AfterSalesStockSourceRecords.Unavailable();
        await InvoiceRecords.VerifyPersistedAsync(database, tx, invoice, ct);
        if (!InvoiceRecords.Equal(InvoiceService.CaseSnapshot(invoice), root["source"])) throw AfterSalesStockSourceRecords.Unavailable();
        // The M92 owner proves real dispatch, signed recipient declaration, exact stock effects,
        // challenge/person consent, audit/outbox/receipt. A saved invoice alone is insufficient.
        foreach (var line in root["source"]["lines"].AsBsonArray) {
            var dispatched = await fulfillment.ReadAsync(tx, "dispatch", line["shipmentId"].AsInt64, true, ct);
            var accepted = await fulfillment.ReadAsync(tx, "receipt", line["receiptId"].AsInt64, true, ct);
            if (accepted.ShipmentId != line["shipmentId"].AsInt64 || dispatched.Record["orderId"] != line["orderId"])
                throw AfterSalesStockSourceRecords.Unavailable();
        }
        var stock = await InvoiceService.CaseDispatchStockAsync(database, tx, root["source"].AsBsonDocument, ct);
        var source = ReturnQuarantineLedger.Source(root, decision, new BsonArray(stock.Where(line =>
            decision["input"]["lines"].AsBsonArray.Any(item => item["receiptId"] == line["receiptId"] && item["skuId"] == line["skuId"]))));
        _ = await new ReturnQuarantineLedger(database, clock).ReadAsync(tx, source, ct);
        if (!InvoiceRecords.Equal(receipt["source"], source)) throw AfterSalesStockSourceRecords.Unavailable();
        var pools = ProvenancePools(receipt);
        if (kind == "receive") return new(kind, id, new BsonDocument("return", receipt), pools,
            ReturnQuarantineLedger.Audit(receipt), receipt["receivedAt"], id);
        var inspection = await new ReturnInspectionRecords(database, clock).ReadAsync(tx, receipt, ct)
            ?? throw AfterSalesStockSourceRecords.Unavailable();
        if (kind == "inspect") return new(kind, id, new BsonDocument { { "return", receipt }, { "inspection", inspection } }, pools,
            ReturnInspectionRecords.Audit(inspection), inspection["inspectedAt"], id);
        var verification = await new ReturnedLotVerificationRecords(database, clock).ReadAsync(tx, receipt, inspection, ct)
            ?? throw AfterSalesStockSourceRecords.Unavailable();
        return new(kind, id, new BsonDocument { { "return", receipt }, { "inspection", inspection }, { "verification", verification } }, pools,
            ReturnedLotVerificationRecords.Audit(verification), verification["verifiedAt"], id);
    }
    internal static long[] ProvenancePools(BsonDocument receipt)
    {
        var lines = ReturnQuarantineLedger.Input(receipt["input"].AsBsonDocument).Lines;
        var stock = receipt["source"]["stock"].AsBsonArray;
        var result = new HashSet<long>();
        foreach (var line in lines) {
            var exact = stock.SingleOrDefault(item => item["receiptId"] == line.ReceiptId && item["skuId"] == line.SkuId)
                ?? throw AfterSalesStockSourceRecords.Unavailable();
            foreach (var original in exact["originalLots"].AsBsonArray) {
                var pool = original["poolId"].AsInt64;
                if (pool <= 0 || pool >= long.MaxValue - 1) throw AfterSalesStockSourceRecords.Unavailable();
                result.Add(pool);
            }
        }
        if (result.Count is < 1 or > 100) throw AfterSalesStockSourceRecords.Unavailable();
        return result.Order().ToArray();
    }
    internal async Task OrphansAsync(IClientSessionHandle tx, CancellationToken ct)
    {
        foreach (var (name, validator) in new[] {
            (AfterSalesCaseRecords.Collection, AfterSalesCaseSchemas.Validator()), (AfterSalesDecisionRecords.Collection, AfterSalesDecisionSchemas.Validator()),
            (ReturnQuarantineLedger.Collection, ReturnQuarantineSchemas.Validator()), (ReturnInspectionRecords.Collection, ReturnInspectionSchemas.Validator()),
            (ReturnedLotVerificationRecords.Collection, ReturnedLotVerificationSchemas.Validator()), (ReturnResolutionRecords.Collection, ReturnResolutionSchemas.Validator()) })
            if (await Rows(name).Find(tx, new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct) ||
                await Rows(name).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct) > AfterSalesStockSourceRecords.MaximumRows)
                throw AfterSalesStockSourceRecords.Unavailable();
        foreach (var (child, parent, field) in new[] {
            (AfterSalesDecisionRecords.Collection, AfterSalesCaseRecords.Collection, "_id"),
            (ReturnQuarantineLedger.Collection, AfterSalesCaseRecords.Collection, "caseId"),
            (ReturnInspectionRecords.Collection, ReturnQuarantineLedger.Collection, "_id"),
            (ReturnedLotVerificationRecords.Collection, ReturnInspectionRecords.Collection, "_id"),
            (ReturnResolutionRecords.Collection, ReturnInspectionRecords.Collection, "_id") }) {
            BsonDocument[] pipeline = [new("$lookup", new BsonDocument { { "from", parent }, { "localField", field }, { "foreignField", "_id" }, { "as", "owner" } }),
                new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1)];
            if (await Rows(child).Aggregate<BsonDocument>(tx, pipeline).AnyAsync(ct)) throw AfterSalesStockSourceRecords.Unavailable();
        }
        foreach (var (operation, parent, field) in new[] {
            (AfterSalesCaseRecords.Operation, AfterSalesCaseRecords.Collection, "caseId"),
            (AfterSalesDecisionRecords.Operation, AfterSalesDecisionRecords.Collection, "caseId"),
            (ReturnQuarantineLedger.Operation, ReturnQuarantineLedger.Collection, "returnId"),
            (ReturnInspectionRecords.Operation, ReturnInspectionRecords.Collection, "returnId"),
            (ReturnedLotVerificationRecords.Operation, ReturnedLotVerificationRecords.Collection, "returnId"),
            (ReturnResolutionRecords.Operation, ReturnResolutionRecords.Collection, "returnId") })
            foreach (var table in new[] { "auditEvents", "idempotencyRecords", "outboxEvents" }) {
                BsonDocument[] pipeline = [new("$match", new BsonDocument(table == "outboxEvents" ? "kind" : "operation", operation)),
                    new("$lookup", new BsonDocument { { "from", parent }, { "localField", (table == "outboxEvents" ? "payload." : "result.") + field },
                        { "foreignField", "_id" }, { "as", "owner" } }), new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1)];
                if (await Rows(table).Aggregate<BsonDocument>(tx, pipeline).AnyAsync(ct)) throw AfterSalesStockSourceRecords.Unavailable();
            }
    }
}
