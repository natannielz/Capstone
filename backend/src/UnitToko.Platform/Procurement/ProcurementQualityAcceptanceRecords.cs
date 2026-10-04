using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementQualityAcceptanceBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal sealed record ProcurementQualityAcceptanceSources(BsonDocument Receipt, BsonDocument Balance, BsonDocument Pool,
    BsonDocument Policy, ProcurementQualityAcceptanceCapacity Capacity, HeldReceiptPoolCapture? PoolCapture);

internal sealed class ProcurementQualityAcceptanceRecords(IMongoDatabase database, MongoUnitOfWork unit, IClock clock,
    CatalogAuthoringService catalog, PurchaseOrderService orders, StockSourceSetupService sources)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    internal static async Task RequireEffectsAsync(IMongoDatabase database, IClientSessionHandle tx, BsonDocument row, CancellationToken ct,
        BsonDocument? proposal = null) {
        var rows = database.GetCollection<BsonDocument>("auditEvents");
        var audit = await rows.Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(audit, B.Audit(row));
        var receipt = await database.GetCollection<BsonDocument>("idempotencyRecords").Find(tx,
            new BsonDocument { { "actorId", row["actorId"] }, { "operation", row["operation"] }, { "key", row["requestKey"] } }).Limit(2).ToListAsync(ct);
        if (receipt.Count != 1 || receipt[0]["bodyHash"] != row["bodyHash"] || receipt[0]["result"] != B.Result(row)) throw B.Broken();
        if (row["operation"] == B.DecideOperation) {
            if (proposal is null || proposal["_id"] != row["proposalId"] || proposal["snapshotHash"] != row["proposalHash"] ||
                !receipt[0].TryGetValue(PersonApprovalReceipt.Field, out var proof) ||
                proof != PersonApprovalReceipt.Create(Permissions.ProcurementQualityAcceptanceDecide,
                    new ApprovalRequest(1, 1, RequestedBy: proposal["actorId"].AsInt64))) throw B.Broken();
        }
        var effect = await database.GetCollection<BsonDocument>("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.SourceKey(row))).Limit(2).ToListAsync(ct);
        if (effect.Count != 1 || effect[0]["kind"] != row["operation"] || effect[0]["actorId"] != row["actorId"] ||
            effect[0]["requestKey"] != row["requestKey"] || effect[0]["location"] != row["location"] ||
            effect[0]["payload"] != B.Result(row) || effect[0]["createdAt"] != row["recordedAt"]) throw B.Broken();
    }
    private static void PolicyFacts(BsonDocument row, ProcurementQualityAcceptanceInput input, DateTimeOffset now, bool current) {
        if (row["revision"] != input.PolicyRevision || row["location"] != R.Location(input.Location) ||
            row["approvedAt"].ToUniversalTime() > now.UtcDateTime || row["proposedBy"] == row["approvedBy"] ||
            row["maximumObservationAgeMilliseconds"].AsInt64 is < 1 or > 2_592_000_000L ||
            row["people"].AsBsonArray.Count != 2 || row["people"].AsBsonArray.Select(R.Person).Any(person =>
                person.Verification != PersonVerification.Verified || person.PersonId <= 0 || person.Version <= 0) ||
            !row["people"].AsBsonArray.Select(R.Person).Select(person => person.UserId).Order()
                .SequenceEqual(new[] { row["proposedBy"].AsInt64, row["approvedBy"].AsInt64 }.Order())) throw B.Changed("procurement_quality_acceptance_policy_unavailable");
        var people = row["people"].AsBsonArray.Select(R.Person).ToDictionary(person => person.UserId);
        if (!PersonIndependencePolicy.AreIndependent(people[row["proposedBy"].AsInt64], people[row["approvedBy"].AsInt64]))
            throw B.Changed("procurement_quality_acceptance_policy_unavailable");
        foreach (var field in new[] { "qualityProcedureReference", "putawayProcedureReference", "approvalReference" })
            ProcurementStockReceiptRules.Text(row[field].AsString, 256);
        if (row["allowNonExpiring"].AsBoolean != !row["nonExpiringProcedureReference"].IsBsonNull) throw B.Changed("procurement_quality_acceptance_policy_unavailable");
        if (!row["nonExpiringProcedureReference"].IsBsonNull) ProcurementStockReceiptRules.Text(row["nonExpiringProcedureReference"].AsString, 256);
        if (current && (!row["active"].AsBoolean || row["expiresAt"].ToUniversalTime() <= now.UtcDateTime))
            throw B.Changed("procurement_quality_acceptance_policy_unavailable");
        if (input.ExpiresOn is null && !row["allowNonExpiring"].AsBoolean)
            throw B.Changed("procurement_quality_acceptance_nonexpiring_unavailable");
    }
    internal async Task<BsonDocument> PolicyAsync(IClientSessionHandle tx, ProcurementQualityAcceptanceInput input, DateTimeOffset now,
        bool current, CancellationToken ct) {
        var row = await Rows(B.Policies).Find(tx, new BsonDocument { { "_id", input.PolicyId },
            { "$and", new BsonArray { ProcurementQualityAcceptanceSchemas.Validator(B.Policies) } } }).SingleOrDefaultAsync(ct)
            ?? throw B.Changed("procurement_quality_acceptance_policy_unavailable");
        PolicyFacts(row, input, now, current); return row;
    }
    internal static void ObservationTime(DateTimeOffset observed, BsonDocument receipt, BsonDocument policy, DateTimeOffset now) {
        var minimum = new DateTimeOffset(receipt["recordedAt"].ToUniversalTime());
        if (observed < minimum || observed > now || now - observed >= TimeSpan.FromMilliseconds(policy["maximumObservationAgeMilliseconds"].AsInt64))
            throw B.Changed("procurement_quality_acceptance_observation_stale");
    }
    internal static void LotFacts(ProcurementQualityAcceptanceInput input, BsonDocument receipt, DateTimeOffset now) {
        Equal(receipt["location"], R.Location(input.Location)); Equal(receipt["revision"], input.ReceiptRevision);
        Equal(receipt["input"]["lotReference"], input.LotReference);
        Equal(receipt["input"]["expiresOn"], input.ExpiresOn is { } day ? InvoiceBson.Date(day) : BsonNull.Value);
        if (input.ExpiresOn is { } expiry && expiry <= DateOnly.FromDateTime(now.UtcDateTime.AddHours(7)))
            throw B.Changed("procurement_quality_acceptance_expired_lot");
        var line = receipt["inspection"]["input"]["lines"][receipt["inspectionLineNumber"].AsInt32 - 1];
        var package = PurchaseOrderService.FundingOrderView(receipt["source"].AsBsonDocument)["lines"].AsBsonArray
            .SingleOrDefault(value => value["skuId"] == line["requestSkuId"] && value["definitionId"] == line["definitionId"] &&
                value["definitionVersion"] == line["definitionVersion"]) ?? throw B.Broken();
        var expectedCondition = package["packageKind"].AsString == "sealed" ? "sealed_undamaged" : "intact_as_defined";
        if (package["packageKind"].AsString is not ("sealed" or "loose" or "other") || input.AcceptedCondition != expectedCondition)
            throw B.Changed("procurement_quality_acceptance_condition_mismatch");
        if (!line["lotCode"].IsBsonNull && line["lotCode"] != input.LotReference ||
            !line["expiryDate"].IsBsonNull && line["expiryDate"] != receipt["input"]["expiresOn"] ||
            !line["putawayReference"].IsBsonNull && line["putawayReference"] != input.PutawayReference)
            throw B.Changed("procurement_quality_acceptance_observation_mismatch");
    }
    internal async Task<ProcurementQualityAcceptanceSources> SourcesAsync(IClientSessionHandle tx, ProcurementQualityAcceptanceInput input,
        DateTimeOffset now, bool current, UnitToko.Modules.Identity.Contracts.ActorContext? actor,
        AuthorizationSnapshot? authority, CancellationToken ct) {
        var receipt = await new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, sources).ReadAsync(tx, input.ReceiptId, ct);
        LotFacts(input, receipt, now);
        var policy = await PolicyAsync(tx, input, now, current, ct);
        ObservationTime(input.ObservedAt, receipt, policy, now);
        var balance = await Rows("stockBalances").Find(tx, new BsonDocument("_id", receipt["balanceId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var original = InventoryBson.Balance(InventoryHeldReceiptWriter.After(receipt)); Equal(balance, original);
        var pool = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", receipt["poolId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        InventoryGraph.RequirePool(pool);
        Equal(pool["location"], receipt["poolSource"]["configuration"]["source"]["input"]["location"]);
        HeldReceiptPoolCapture? capture = null;
        if (current) {
            if (actor is null || authority is null) throw B.Broken();
            capture = await sources.CaptureHeldReceiptAsync(actor, authority, tx, InventoryGraph.Location(R.Location(input.Location)),
                receipt["poolId"].AsInt64, pool["revision"].AsInt64, Permissions.StockRead, ct);
            Equal(capture.Pool, pool);
        }
        var claims = new SupplierClaimRecords(database, unit, clock, catalog, orders);
        var lineNumber = receipt["inspectionLineNumber"].AsInt32;
        await new ProcurementQuarantineDisposition(database, claims).ReadAsync(tx, SupplierClaimBasis.PhysicalReceived,
            receipt["inspectionId"].AsInt64, lineNumber, receipt["inspection"]["snapshotHash"].AsString,
            receipt["inspection"]["input"]["lines"][lineNumber - 1]["receivedPackages"].AsInt32,
            receipt["location"].AsBsonDocument, receipt["orderId"].AsInt64, receipt["supplierId"].AsInt64, ct);
        var capacity = await new ProcurementQualityAcceptanceCapacityReader(database, sources).ReadAsync(tx, receipt, ct);
        await OrphansAsync(tx, ct);
        if (current && (input.AcceptedPackages > capacity.State["remainingPackages"].AsInt64 ||
            input.AcceptedPackages > balance["held"].AsInt64 - capacity.State["acceptedPackages"].AsInt64)
            ) throw B.Changed("procurement_quality_acceptance_capacity_exceeded");
        return new(receipt, balance, pool, policy, capacity, capture);
    }
    internal async Task<BsonDocument> ProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await Rows(B.Proposals).Find(tx, new BsonDocument { { "_id", id },
            { "$and", new BsonArray { ProcurementQualityAcceptanceSchemas.Validator(B.Proposals) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(row["snapshotHash"], B.Hash(row)); Equal(row["proposalId"], id); Equal(row["version"], 1L);
        var input = B.Input(row["input"]); Equal(row["input"], B.Input(input)); Equal(row["location"], R.Location(input.Location));
        var source = await SourcesAsync(tx, input, new DateTimeOffset(row["recordedAt"].ToUniversalTime()), false, null, null, ct);
        Equal(row["receiptHash"], source.Receipt["snapshotHash"]); Equal(row["inspectionHash"], source.Receipt["inspection"]["snapshotHash"]);
        Equal(row["lotId"], source.Receipt["lotId"]); Equal(row["balanceId"], source.Receipt["balanceId"]); Equal(row["poolId"], source.Receipt["poolId"]);
        Equal(row["heldBalance"], source.Balance); Equal(row["policy"], B.PolicySnapshot(source.Policy));
        InventoryGraph.RequirePool(row["pool"].AsBsonDocument);
        if (row["pool"].AsBsonDocument["_id"] != source.Pool["_id"] ||
            row["pool"].AsBsonDocument["location"] != source.Pool["location"]) throw B.Broken();
        Equal(row["sourceConfigurationHash"], B.SourceConfigurationHash(row["poolSource"].AsBsonDocument));
        await sources.RequireHeldReceiptHistoryAsync(tx, row["poolSource"].AsBsonDocument,
            new DateTimeOffset(row["recordedAt"].ToUniversalTime()), ct);
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(B.ProposeBody(input, row["binding"].AsString), row["reason"].AsString));
        await RequireEffectsAsync(database, tx, row, ct); return row;
    }
    internal async Task<BsonDocument?> DecisionAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct) {
        var rows = await Rows(B.Decisions).Find(tx, new BsonDocument("proposalId", proposal["_id"])).Limit(2).ToListAsync(ct);
        if (rows.Count > 1) throw B.Broken();
        if (rows.Count == 0) {
            if (await Rows(B.Allocations).Find(tx, new BsonDocument("proposalId", proposal["_id"])).AnyAsync(ct) ||
                await Rows("auditEvents").Find(tx, new BsonDocument { { "kind", B.DecideOperation }, { "result.proposalId", proposal["_id"] } }).AnyAsync(ct) ||
                await Rows("outboxEvents").Find(tx, new BsonDocument { { "kind", B.DecideOperation }, { "payload.proposalId", proposal["_id"] } }).AnyAsync(ct) ||
                await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "operation", B.DecideOperation }, { "result.proposalId", proposal["_id"] } }).AnyAsync(ct)) throw B.Broken();
            return null;
        }
        var row = rows[0]; Equal(row["snapshotHash"], B.Hash(row)); Equal(row["proposalHash"], proposal["snapshotHash"]);
        Equal(row["input"], proposal["input"]); Equal(row["location"], proposal["location"]);
        if (row["decision"] == "approve") {
            if (row["observation"].IsBsonNull || row["allocationId"].IsBsonNull) throw B.Broken();
            Equal(row["observation"], B.Observation(B.Observation(row["observation"])));
            var allocation = await Rows(B.Allocations).Find(tx, new BsonDocument("_id", row["allocationId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            Equal(allocation["decisionId"], row["_id"]); Equal(allocation["proposalId"], proposal["_id"]);
            var receipt = await new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, sources)
                .ReadAsync(tx, B.Input(proposal["input"]).ReceiptId, ct);
            var capacity = await new ProcurementQualityAcceptanceCapacityReader(database, sources).ReadAsync(tx, receipt, ct);
            if (!capacity.Allocations.Any(value => value["_id"] == allocation["_id"])) throw B.Broken();
        } else if (row["decision"] != "reject" || !row["observation"].IsBsonNull || !row["allocationId"].IsBsonNull ||
            row["capacityBefore"] != row["capacityAfter"]) throw B.Broken();
        await RequireEffectsAsync(database, tx, row, ct, proposal); return row;
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string operation, string key, CancellationToken ct) {
        var table = operation == B.ProposeOperation ? B.Proposals : B.DecideOperation == operation ? B.Decisions : throw B.Broken();
        var row = await Rows(table).Find(tx, new BsonDocument { { "actorId", actor }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is not null) {
            if (operation == B.ProposeOperation) return await ProposalAsync(tx, row["_id"].AsInt64, ct);
            var proposal = await ProposalAsync(tx, row["proposalId"].AsInt64, ct);
            return await DecisionAsync(tx, proposal, ct) ?? throw B.Broken();
        }
        foreach (var (name, field, keyField) in new[] { ("auditEvents", "kind", "requestKey"),
            ("outboxEvents", "kind", "requestKey"), ("idempotencyRecords", "operation", "key") })
            if (await Rows(name).Find(tx, new BsonDocument { { "actorId", actor }, { field, operation }, { keyField, key } }).AnyAsync(ct)) throw B.Broken();
        return null;
    }
    internal async Task PreflightAsync(IClientSessionHandle tx, CancellationToken ct) {
        await OrphansAsync(tx, ct);
        long decisions = 0;
        foreach (var raw in await Rows(B.Proposals).Find(tx, new BsonDocument()).ToListAsync(ct)) {
            var proposal = await ProposalAsync(tx, raw["_id"].AsInt64, ct);
            if (await DecisionAsync(tx, proposal, ct) is not null) decisions++;
        }
        if (await Rows(B.Decisions).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct) != decisions) throw B.Broken();
        foreach (var raw in await Rows(B.ReceiptHeads).Find(tx, new BsonDocument()).ToListAsync(ct)) {
            var receipt = await new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, sources)
                .ReadAsync(tx, raw["receiptId"].AsInt64, ct);
            _ = await new ProcurementQualityAcceptanceCapacityReader(database, sources).ReadAsync(tx, receipt, ct);
        }
    }
    internal async Task OrphansAsync(IClientSessionHandle tx, CancellationToken ct) {
        foreach (var (table, filter, local, target) in new[] {
            (B.Decisions, new BsonDocument(), "proposalId", B.Proposals),
            (B.Allocations, new BsonDocument(), "proposalId", B.Proposals),
            (B.Allocations, new BsonDocument(), "decisionId", B.Decisions),
            (B.Allocations, new BsonDocument(), "receiptId", ProcurementStockReceiptBson.Receipts),
            (B.ReceiptHeads, new BsonDocument(), "receiptId", ProcurementStockReceiptBson.Receipts),
            ("auditEvents", new BsonDocument("kind", B.ProposeOperation), "result.proposalId", B.Proposals),
            ("auditEvents", new BsonDocument("kind", B.DecideOperation), "result.decisionId", B.Decisions),
            ("outboxEvents", new BsonDocument("kind", B.ProposeOperation), "payload.proposalId", B.Proposals),
            ("outboxEvents", new BsonDocument("kind", B.DecideOperation), "payload.decisionId", B.Decisions),
            ("idempotencyRecords", new BsonDocument("operation", B.ProposeOperation), "result.proposalId", B.Proposals),
            ("idempotencyRecords", new BsonDocument("operation", B.DecideOperation), "result.decisionId", B.Decisions) }) {
            var orphan = await Rows(table).Aggregate<BsonDocument>(tx, new BsonDocument[] {
                new("$match", filter), new("$lookup", new BsonDocument { { "from", target }, { "localField", local }, { "foreignField", "_id" }, { "as", "owner" } }),
                new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1) }).ToListAsync(ct);
            if (orphan.Count != 0) throw B.Broken();
        }
    }
}
