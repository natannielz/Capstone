using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Inventory.StockSourceReconciliationBson;
using R = UnitToko.Platform.Procurement.ProcurementStockReceiptBson;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    private async Task ReconciliationStorageAsync(CancellationToken ct)
    {
        await OperatingPolicyStorageAsync(ct);
        foreach (var table in new[] { R.Receipts, "lots", "stockBalances", "movements" })
        {
            var (validator, keys) = table switch
            {
                R.Receipts => (ProcurementStockReceiptSchemas.Validator(table), ProcurementStockReceiptSchemas.Keys(table)),
                "lots" => (InventoryLotSchemas.Validator(), new[] { new BsonDocument("_id", 1) }),
                "stockBalances" => (InventorySchemas.BalanceValidator(), new[] { InventorySchemas.BalanceKey() }),
                _ => (InventoryMovementSchemas.Validator(), new[] { InventoryMovementSchemas.Key() })
            };
            await InventoryRepository.RequireStorageAsync(database, table, validator, keys, ct);
        }
        foreach (var table in new[] { B.Proposals, B.Decisions })
            await InventoryRepository.RequireStorageAsync(database, table, StockSourceReconciliationSchemas.Validator(table),
                StockSourceReconciliationSchemas.Keys(table), ct);
    }

    private static void RequireObservationTime(StockSourceReconciliationObservation observation, BsonDocument baseline, DateTimeOffset now)
    {
        StockSourceReconciliationRules.Validate(observation);
        var at = observation.ObservedAt;
        var maxAge = baseline["maximumAgeMilliseconds"].AsInt64;
        if (at > now || at < new DateTimeOffset(baseline["latestSourceAt"].ToUniversalTime()) ||
            now - at >= TimeSpan.FromMilliseconds(maxAge)) throw B.Changed();
    }
    private static BsonArray ObservedRows(StockSourceReconciliationObservation observation, BsonDocument baseline)
    {
        var expected = baseline["balances"].AsBsonArray.Select(value => value["balanceId"].AsInt64).ToArray();
        var supplied = observation.Entries.Select(entry => entry.BalanceId).Order().ToArray();
        if (expected.Length != supplied.Length || !expected.SequenceEqual(supplied)) throw B.Changed();
        return B.Observation(observation)["entries"].AsBsonArray;
    }
    private static BsonArray Differences(BsonDocument baseline, BsonDocument observation)
    {
        var values = observation["entries"].AsBsonArray.ToDictionary(value => value["balanceId"].AsInt64, value => value["packages"].AsInt64);
        return new BsonArray(baseline["balances"].AsBsonArray.Where(value => values[value["balanceId"].AsInt64] != value["onHand"].AsInt64)
            .Select(value => new BsonDocument { { "balanceId", value["balanceId"] }, { "baselinePackages", value["onHand"] },
                { "observedPackages", values[value["balanceId"].AsInt64] } }));
    }
    private static string BaselineHash(BsonDocument baseline) => B.Hash(new BsonDocument {
        { "poolId", baseline["poolId"] }, { "poolRevision", baseline["poolRevision"] },
        { "policyId", baseline["policyId"] }, { "policyRevision", baseline["policyRevision"] },
        { "configurationHash", baseline["configurationHash"] }, { "balances", baseline["balances"] } });
    private static string Binding(BsonDocument baseline) => B.Hash(new BsonDocument {
        { "poolId", baseline["poolId"] }, { "poolRevision", baseline["poolRevision"] }, { "poolFence", baseline["poolFence"] },
        { "policyId", baseline["policyId"] }, { "policyRevision", baseline["policyRevision"] },
        { "configurationHash", baseline["configurationHash"] }, { "baselineHash", baseline["baselineHash"] } });

    private async Task<BsonDocument> ReconciliationBaselineAsync(IClientSessionHandle tx, OperatingPolicyContext context, CancellationToken ct)
    {
        RequireOperatingPolicyReady(context.Source);
        var view = context.History.Latest ?? throw B.Unavailable();
        if (view["policy"].IsBsonNull) throw B.Unavailable();
        var policy = view["policy"].AsBsonDocument;
        var source = StockSourceSetupBson.ReadInput(context.Source.Current["input"].AsBsonDocument);
        if (source.Mode != StockAuthorityMode.WebLedger || source.Control is not (StockPoolControl.ExclusiveWebAllocation or StockPoolControl.SharedWebLedger) ||
            view["proposal"]["input"]["writer"] != "unitToko" ||
            view["proposal"]["configurationHash"] != context.Hash ||
            view["proposal"]["suspensionRevision"].AsInt64 != context.SuspensionRevision ||
            view["proposal"]["poolRevision"] != context.Source.Pool["revision"]) throw B.Unavailable();
        var pool = context.Source.Pool; var poolId = pool["_id"].AsInt64;
        // ponytail: The full 1000-row ceiling is explicit; a later batched owner may extend it without partial coverage.
        var receipts = await Rows(R.Receipts).Find(tx, F.Eq("poolId", poolId)).Sort(new BsonDocument("_id", 1)).Limit(1001).ToListAsync(ct);
        var balances = await Rows("stockBalances").Find(tx, F.Eq("physicalPoolId", poolId)).Sort(new BsonDocument("_id", 1)).Limit(1001).ToListAsync(ct);
        if (receipts.Count > 1000 || balances.Count > 1000) throw B.Unavailable();
        var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog,
            new ProcurementRequestService(database, unit, ids, clock, catalog), new SupplierService(database, unit, ids, clock, catalog));
        var records = new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, this);
        var balanceById = balances.ToDictionary(row => row["_id"].AsInt64);
        var rows = new BsonArray(); var receiptIds = new HashSet<long>(); var verifiedReceipts = new List<BsonDocument>();
        var latestSource = context.Source.ApprovedAt;
        var policyAt = StockSourceSetupBson.Time(view["decision"]["decidedAt"]);
        if (policyAt > latestSource) latestSource = policyAt;
        foreach (var raw in receipts)
        {
            var receipt = await records.ReadAsync(tx, raw["_id"].AsInt64, ct);
            verifiedReceipts.Add(receipt);
            var receiptId = receipt["_id"].AsInt64; var balanceId = receipt["balanceId"].AsInt64;
            if (!receiptIds.Add(receiptId) || !balanceById.Remove(balanceId, out var balanceRaw)) throw B.Unavailable();
            var balance = InventoryBson.ReadBalance(balanceRaw);
            if (balance.PhysicalPoolId != poolId || balance.Balance.Key.LotId.Value != receipt["lotId"].AsInt64 ||
                !balanceRaw.Equals(InventoryBson.Balance(InventoryHeldReceiptWriter.After(receipt)))) throw B.Unavailable();
            await new InventoryGraph(database).RequireAsync(tx, balance, false, ct);
            var sourceLines = PurchaseOrderService.FundingOrderView(receipt["source"].AsBsonDocument)["lines"].AsBsonArray;
            var line = receipt["inspection"]["input"]["lines"][receipt["inspectionLineNumber"].AsInt32 - 1];
            var package = sourceLines.SingleOrDefault(value => value["skuId"] == line["requestSkuId"] &&
                value["definitionId"] == line["definitionId"] && value["definitionVersion"] == line["definitionVersion"])
                ?? throw B.Unavailable();
            var at = new DateTimeOffset(receipt["recordedAt"].ToUniversalTime()); if (at > latestSource) latestSource = at;
            rows.Add(new BsonDocument { { "balanceId", balanceId }, { "receiptId", receiptId }, { "lotId", receipt["lotId"] },
                { "location", balanceRaw["location"] }, { "skuId", balanceRaw["skuId"] }, { "skuCode", package["skuCode"] },
                { "definitionId", balanceRaw["definitionId"] }, { "definitionVersion", package["definitionVersion"] },
                { "packageLabel", package["packageLabel"] }, { "packageKind", package["packageKind"] },
                { "baseUnitCode", package["baseUnitCode"] }, { "baseUnitsPerPackage", package["baseUnitsPerPackage"] },
                { "lotReference", receipt["input"]["lotReference"] }, { "expiresOn", receipt["input"]["expiresOn"] },
                { "onHand", balanceRaw["onHand"] }, { "reserved", balanceRaw["reserved"] }, { "held", balanceRaw["held"] },
                { "expired", balanceRaw["expired"] }, { "version", balanceRaw["version"] } });
        }
        if (balanceById.Count != 0) throw B.Unavailable();
        // The receipt owner deliberately does not recurse into its disposition head. Complete
        // the genuine inspection-capacity owner proof once per represented inspection line.
        var claims = new SupplierClaimRecords(database, unit, clock, catalog, orders);
        var dispositions = new ProcurementQuarantineDisposition(database, claims);
        foreach (var receipt in verifiedReceipts.GroupBy(row => (row["inspectionId"].AsInt64, row["inspectionLineNumber"].AsInt32)).Select(group => group.First()))
        {
            var lineNumber = receipt["inspectionLineNumber"].AsInt32;
            var inspection = receipt["inspection"].AsBsonDocument;
            await dispositions.ReadAsync(tx, SupplierClaimBasis.PhysicalReceived, receipt["inspectionId"].AsInt64,
                lineNumber, inspection["snapshotHash"].AsString,
                inspection["input"]["lines"][lineNumber - 1]["receivedPackages"].AsInt32,
                receipt["location"].AsBsonDocument, receipt["orderId"].AsInt64, receipt["supplierId"].AsInt64, ct);
        }
        // Reverse owners make a deleted receipt+balance pair observable through retained movement/lot evidence.
        var movements = await Rows("movements").Find(tx, F.Eq("physicalPoolId", poolId)).Limit(1001).ToListAsync(ct);
        if (movements.Count != receiptIds.Count || movements.Any(row => row["source"]["namespace"] != R.Operation ||
            !receiptIds.Contains(row["source"]["documentId"].AsInt64))) throw B.Unavailable();
        List<BsonDocument> lots = receiptIds.Count == 0 ? new() : await Rows("lots").Find(tx,
            F.Eq("origin", "procurementReceipt") & F.In("sourceId", receiptIds)).ToListAsync(ct);
        if (lots.Count != receiptIds.Count || lots.Any(row => !receiptIds.Contains(row["sourceId"].AsInt64))) throw B.Unavailable();
        var ordered = new BsonArray(rows.OrderBy(value => value["balanceId"].AsInt64));
        var result = new BsonDocument { { "poolId", poolId }, { "poolRevision", pool["revision"] }, { "poolFence", pool["fence"] },
            { "poolLabel", source.Label }, { "physicalKey", source.PhysicalKey }, { "ownerLocation", StockSourceSetupBson.Location(source.Location) },
            { "policyId", policy["_id"] }, { "policyRevision", policy["policyRevision"] },
            { "reconciliationProcedureReference", view["proposal"]["input"]["reconciliationProcedureReference"] },
            { "configurationHash", context.Hash }, { "maximumAgeMilliseconds", source.MaximumAgeMilliseconds },
            { "latestSourceAt", latestSource.UtcDateTime }, { "balances", ordered }, { "capturedAt", Now().UtcDateTime } };
        result.Add("baselineHash", BaselineHash(result)); result.Add("binding", Binding(result)); return result;
    }
}
