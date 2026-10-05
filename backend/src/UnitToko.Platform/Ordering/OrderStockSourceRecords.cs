using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;

namespace UnitToko.Platform.Ordering;

internal sealed record OrderStockSourceSnapshot(BsonDocument Head, BsonDocument[] Orders, BsonDocument[] Reservations,
    long OpenOrders, long OpenReservations);

internal sealed class OrderStockSourceRecords(IMongoDatabase database)
{
    internal const string Heads = "orderingStockSourceHeads", Transitions = "orderingStockSourceTransitions";
    internal const int MaximumRows = 4096;
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static DomainValidationException Unavailable() => new("order_source_obligation_unavailable",
        "The stock source needs complete authenticated Ordering producer state; it cannot be declared drained");
    internal static BsonDocument State(BsonDocument row) => new(new[] { "revision", "remaining", "released", "dispatched" }
        .Select(field => new BsonElement(field, row[field].DeepClone())));
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name != "transitionHash")));
    internal async Task<BsonDocument[]> BoundedAsync(string name, FilterDefinition<BsonDocument> filter, IClientSessionHandle tx, CancellationToken ct)
    {
        var rows = await Rows(name).Find(tx, filter).Sort(new BsonDocument("_id", 1)).Limit(MaximumRows + 1).ToListAsync(ct);
        if (rows.Count > MaximumRows) throw Unavailable(); return rows.ToArray();
    }
    internal async Task<OrderStockSourceEvent> SetupAsync(IClientSessionHandle tx, long decisionId, bool requireReceipt, CancellationToken ct)
    {
        async Task<BsonDocument> One(string name, FilterDefinition<BsonDocument> filter) =>
            await Rows(name).Find(tx, filter).SingleOrDefaultAsync(ct) ?? throw Unavailable();
        var decision = await One("stockSourceDecisions", F.Eq("_id", decisionId));
        var proposal = await One("stockSourceProposals", F.Eq("_id", decision["proposalId"]));
        StockSourceSetupBson.RequireProposal(proposal); StockSourceSetupBson.RequireDecision(decision, proposal);
        if (decision["decision"] != "approve") throw Unavailable();
        var input = StockSourceSetupBson.ReadInput(proposal["input"].AsBsonDocument);
        var registration = await One("inventoryPoolRegistrations", F.Eq("_id", decision["poolId"]));
        var expectedRegistration = StockSourceSetupBson.Header("inventoryPoolRegistrations", decision["poolId"].AsInt64);
        expectedRegistration.Add("organizationId", input.Location.OrganizationId.Value); expectedRegistration.Add("physicalKey", input.PhysicalKey);
        expectedRegistration.Add("proposalId", proposal["_id"]); expectedRegistration.Add("decisionId", decision["_id"]);
        if (registration != expectedRegistration) throw Unavailable();
        async Task<BsonDocument> Audit(BsonDocument row, string operation, string actorField, string timeField) {
            var audit = await One("auditEvents", F.Eq("_id", row["auditId"]));
            if (audit.ElementCount != 8 || audit["schemaVersion"] != 1 || audit["kind"] != "stock.source.audit" ||
                audit["actorId"] != row[actorField] || !audit["actorGrantVersion"].IsInt64 || audit["actorGrantVersion"].AsInt64 <= 0 ||
                audit["operation"] != operation || audit["createdAt"] != row[timeField] || !CatalogSchemas.SemanticEquals(audit["record"], row)) throw Unavailable();
            return audit;
        }
        _ = await Audit(proposal, "stock.source.propose", "proposedBy", "proposedAt");
        var audit = await Audit(decision, "stock.source.approve", "decidedBy", "decidedAt");
        var proposalResult = new BsonDocument { { "proposalId", proposal["_id"] }, { "revision", 1L } };
        var proposalReceipt = await One("idempotencyRecords", F.Eq("operation", "stock.source.propose") & F.Eq("result.proposalId", proposal["_id"]));
        if (proposalReceipt["actorId"] != proposal["proposedBy"] || proposalReceipt["bodyHash"] != AuthorizationStore.CommandHash(input, proposal["reason"].AsString) ||
            !CatalogSchemas.SemanticEquals(proposalReceipt["result"], proposalResult) || proposalReceipt.Contains(PersonApprovalReceipt.Field) ||
            StockSourceSetupBson.Time(proposalReceipt["createdAt"]) > StockSourceSetupBson.Time(proposal["proposedAt"])) throw Unavailable();
        if (requireReceipt) {
            var receipt = await One("idempotencyRecords", F.Eq("operation", "stock.source.approve") & F.Eq("result.decisionId", decision["_id"]));
            var proposalId = proposal["_id"].AsInt64; var expectedRevision = decision["revision"].AsInt64;
            var reviewReference = decision["reviewReference"].AsString; var outcome = StockSourceSetupDecision.Approve;
            var result = new BsonDocument { { "proposalId", proposal["_id"] }, { "decisionId", decision["_id"] }, { "poolId", decision["poolId"] }, { "status", "approve" } };
            // Property names match the original setup command exactly.
            var body = new { proposalId, expectedRevision, decision = outcome, reviewReference };
            if (receipt["actorId"] != decision["decidedBy"] || receipt["bodyHash"] != AuthorizationStore.CommandHash(body, decision["reason"].AsString) ||
                !CatalogSchemas.SemanticEquals(receipt["result"], result) ||
                StockSourceSetupBson.Time(receipt["createdAt"]) > StockSourceSetupBson.Time(decision["decidedAt"]) ||
                PersonApprovalReceipt.Read(receipt, UnitToko.Modules.Identity.Permissions.StockSourceApprove) != new ApprovalRequest(1, 1, ProposedBy: proposal["proposedBy"].AsInt64)) throw Unavailable();
        }
        return new("setup", decisionId, new() { { "proposal", proposal }, { "decision", decision }, { "registration", registration } },
            [decision["poolId"].AsInt64], audit, decision["decidedAt"], []);
    }
    internal async Task<(BsonDocument Head, BsonDocument[] History, Dictionary<long, BsonDocument> State)> HistoryAsync(
        IClientSessionHandle tx, long poolId, CancellationToken ct)
    {
        new InventoryRepository(database).RequireSession(tx);
        var transactionNumber = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        var head = await Rows(Heads).Find(tx, F.Eq("_id", poolId)).SingleOrDefaultAsync(ct) ?? throw Unavailable();
        if (!await Rows(Heads).Find(tx, F.Eq("_id", poolId) & OrderStockSourceSchemas.Validator(Heads)).AnyAsync(ct)) throw Unavailable();
        var history = await Rows(Transitions).Find(tx, F.Eq("poolId", poolId)).Sort(new BsonDocument("revision", 1)).Limit(MaximumRows + 1).ToListAsync(ct);
        if (history.Count is < 1 or > MaximumRows || head["revision"].AsInt64 != history.Count) throw Unavailable();
        var state = new Dictionary<long, BsonDocument>(); BsonDocument? previous = null; BsonValue? setupLocation = null;
        var events = new HashSet<string>();
        foreach (var row in history) {
            if (!await Rows(Transitions).Find(tx, F.Eq("_id", row["_id"]) & OrderStockSourceSchemas.Validator(Transitions)).AnyAsync(ct) ||
                row["revision"].AsInt64 != (previous is null ? 1L : previous["revision"].AsInt64 + 1) ||
                row["previousId"] != (previous is null ? BsonNull.Value : previous["_id"]) ||
                row["previousHash"] != (previous is null ? BsonNull.Value : previous["transitionHash"]) || row["transitionHash"] != Hash(row) ||
                previous is not null && StockSourceSetupBson.Time(row["recordedAt"]) < StockSourceSetupBson.Time(previous["recordedAt"])) throw Unavailable();
            var kind = row["sourceKind"].AsString; var id = row["sourceId"].AsInt64;
            if (!events.Add(kind + ":" + id) || (previous is null) != (kind == "setup") || kind == "setup" && id != head["setupDecisionId"].AsInt64) throw Unavailable();
            var source = kind == "setup" ? await SetupAsync(tx, id, true, ct) : await OrderSubmissionService.ReadStockSourceEventAsync(database, tx, kind, id, true, ct);
            if (kind == "setup") setupLocation = source.Record["proposal"]["input"]["location"];
            var effects = Effects(source, poolId);
            if (!source.PoolIds.Contains(poolId) || row["sourceHash"] != source.Hash || row["sourceAuditId"] != source.Audit["_id"] ||
                row["actorId"] != source.Audit["actorId"] || row["actorGrantVersion"] != source.Audit["actorGrantVersion"] ||
                row["recordedAt"] != source.Time || !CatalogSchemas.SemanticEquals(row["effects"], effects)) throw Unavailable();
            foreach (var value in effects) {
                var effect = value.AsBsonDocument; var key = effect["reservationId"].AsInt64;
                if (effect["before"].IsBsonNull) {
                    if (state.ContainsKey(key) || effect["after"]["revision"] != 1L) throw Unavailable();
                    state.Add(key, effect.DeepClone().AsBsonDocument);
                } else {
                    if (!state.TryGetValue(key, out var before) || before["orderId"] != effect["orderId"] || before["suborderId"] != effect["suborderId"] ||
                        !CatalogSchemas.SemanticEquals(before["allocation"], effect["allocation"]) ||
                        !CatalogSchemas.SemanticEquals(before["after"], effect["before"])) throw Unavailable();
                    before["after"] = effect["after"].DeepClone();
                }
            }
            previous = row;
        }
        if (previous is null || setupLocation is null || head["lastTransitionId"] != previous["_id"] ||
            head["lastHash"] != previous["transitionHash"] || head["updatedAt"] != previous["recordedAt"] || head["location"] != setupLocation) throw Unavailable();
        new InventoryRepository(database).RequireSession(tx);
        if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transactionNumber) throw Unavailable();
        return (head, history.ToArray(), state);
    }
    internal static BsonArray Effects(OrderStockSourceEvent source, long poolId) => new(source.Effects
        .Where(value => value["allocation"]["poolId"].AsInt64 == poolId).Select(value => value.DeepClone()));
    internal async Task<OrderStockSourceSnapshot> ReadAsync(IClientSessionHandle tx, OrderSubmissionService owner,
        StockSourceObligationExpectation expected, CancellationToken ct)
    {
        var (head, history, state) = await HistoryAsync(tx, expected.PoolId, ct);
        if (head["location"] != StockSourceSetupBson.Location(expected.Location)) throw Unavailable();
        var pool = await Rows("inventoryPools").Find(tx, F.Eq("_id", expected.PoolId)).SingleOrDefaultAsync(ct); InventoryGraph.RequirePool(pool);
        if (pool!["location"] != head["location"] || pool["revision"] != expected.PoolRevision || pool["fence"] != expected.PoolFence) throw Unavailable();
        foreach (var name in OrderSubmissionSchemas.Collections)
            if (await Rows(name).Find(tx, new BsonDocument("$nor", new BsonArray { OrderSubmissionSchemas.Validator(name) })).Limit(1).AnyAsync(ct)) throw Unavailable();
        // Losing a linkage cannot turn an orphan into an apparently empty pool.
        foreach (var name in new[] { B.Suborders, B.Reservations, "orderDecisions", "shipments" }) {
            BsonDocument[] pipeline = [new("$lookup", new BsonDocument { { "from", B.Orders }, { "localField", "orderId" }, { "foreignField", "_id" }, { "as", "linkedOrder" } }),
                new("$match", new BsonDocument("linkedOrder", new BsonDocument("$size", 0))), new("$limit", 1)];
            if (await Rows(name).Aggregate<BsonDocument>(tx, pipeline).AnyAsync(ct)) throw Unavailable();
        }
        var orders = await BoundedAsync(B.Orders, F.Eq("allocations.poolId", expected.PoolId), tx, ct);
        var ids = orders.Select(row => row["_id"]).ToArray();
        var reservations = await BoundedAsync(B.Reservations, F.Eq("allocation.poolId", expected.PoolId) |
            F.In("_id", state.Keys.Select(key => new BsonInt64(key))), tx, ct);
        var actualEvents = new HashSet<string> { "setup:" + head["setupDecisionId"].AsInt64 };
        foreach (var order in orders) {
            _ = await owner.ReadStockSourceGraphAsync(tx, order["_id"].AsInt64, ct);
            actualEvents.Add("submission:" + order["_id"].AsInt64);
        }
        foreach (var (name, kind) in new[] { ("orderDecisions", "decision"), ("shipments", "shipment") })
            foreach (var row in await BoundedAsync(name, F.In("orderId", ids), tx, ct)) actualEvents.Add(kind + ":" + row["_id"].AsInt64);
        if (!actualEvents.SetEquals(history.Select(row => row["sourceKind"].AsString + ":" + row["sourceId"].AsInt64)) || reservations.Length != state.Count) throw Unavailable();
        foreach (var reservation in reservations) {
            if (!state.TryGetValue(reservation["_id"].AsInt64, out var recorded) || !ids.Contains(reservation["orderId"]) ||
                recorded["orderId"] != reservation["orderId"] || recorded["suborderId"] != reservation["suborderId"] ||
                !CatalogSchemas.SemanticEquals(recorded["allocation"], reservation["allocation"]) ||
                !CatalogSchemas.SemanticEquals(recorded["after"], State(reservation))) throw Unavailable();
        }
        var open = reservations.Where(row => row["remaining"].AsInt64 > 0).ToArray();
        return new(head, orders, reservations, open.Select(row => row["orderId"].AsInt64).Distinct().LongCount(), open.LongLength);
    }
}
