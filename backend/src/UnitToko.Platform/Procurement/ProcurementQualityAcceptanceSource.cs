using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using Q = UnitToko.Platform.Procurement.ProcurementQualityAcceptanceBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal sealed record ProcurementApprovedQualitySource(BsonDocument Receipt, BsonDocument Proposal, BsonDocument Decision,
    BsonDocument Allocation, ProcurementQualityAcceptanceCapacity Capacity, BsonDocument Snapshot);

// A proof cycle can occur when a genuine supplier claim names an invoice match whose
// quality allocation is being authenticated. Only that exact immutable allocation edge
// may be revisited while the outer, full physical/claim/document proof is completing.
internal sealed partial class ProcurementQualityProofGraph(IClientSessionHandle tx, HeldReceiptOriginProof? origin = null)
{
    private readonly HeldReceiptOriginProof? originProof = origin;
    private (ProcurementStockValuationService.ReleaseOriginReader Reader, ProcurementStockValuationService Owner,
        IMongoDatabase Database, IClock Clock, DateTimeOffset At, object Token)? originRead;
    private readonly long transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
    private readonly Dictionary<long, ProcurementApprovedQualitySource> active = [];
    // Current-chain readers share only immutable genesis. The historical originProof
    // shortcut above remains separate; every current movement chain is reconstructed.
    private readonly Dictionary<long, HeldReceiptOriginProof> currentMovementOrigins = [];
    internal void Require(IClientSessionHandle current) {
        if (!ReferenceEquals(tx, current) || !current.IsInTransaction ||
            current.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction) throw ProcurementQualityAcceptanceBson.Broken();
    }
    internal object BeginOriginRead(ProcurementStockValuationService.ReleaseOriginReader reader, ProcurementStockValuationService owner,
        IMongoDatabase database, IClock clock, IClientSessionHandle current, HeldReceiptOriginProof expected) {
        RequireDatabase(database, current, expected.ReceiptId);
        if (originRead is not null || !ReferenceEquals(originProof, expected) || clock.UtcNow == default || clock.UtcNow.Offset != TimeSpan.Zero)
            throw ProcurementQualityAcceptanceBson.Broken();
        reader.Require(owner, database, current, expected);
        var token = new object(); originRead = (reader, owner, database, clock, clock.UtcNow, token); return token;
    }
    private void RequireOriginRead(IMongoDatabase database, IClientSessionHandle current, HeldReceiptOriginProof expected) {
        var scope = originRead ?? throw ProcurementQualityAcceptanceBson.Broken();
        RequireDatabase(database, current, expected.ReceiptId);
        if (!ReferenceEquals(expected, originProof) || !ReferenceEquals(database.Client, scope.Database.Client) ||
            !database.DatabaseNamespace.Equals(scope.Database.DatabaseNamespace) || scope.Clock.UtcNow < scope.At || scope.Clock.UtcNow.Offset != TimeSpan.Zero)
            throw ProcurementQualityAcceptanceBson.Broken();
        scope.Reader.Require(scope.Owner, database, current, expected);
    }
    internal void EndOriginRead(object token) {
        if (originRead is not { } scope || !ReferenceEquals(scope.Token, token)) throw ProcurementQualityAcceptanceBson.Broken();
        originRead = null;
    }
    internal bool TryActive(IClientSessionHandle current, BsonDocument allocation, out ProcurementApprovedQualitySource source) {
        Require(current);
        if (active.TryGetValue(allocation["_id"].AsInt64, out source!)) {
            if (source.Allocation != allocation || source.Allocation["snapshotHash"] != allocation["snapshotHash"])
                throw ProcurementQualityAcceptanceBson.Broken();
            return true;
        }
        source = null!; return false;
    }
    internal void Enter(IClientSessionHandle current, ProcurementApprovedQualitySource source) {
        Require(current); if (!active.TryAdd(source.Allocation["_id"].AsInt64, source)) throw ProcurementQualityAcceptanceBson.Broken();
    }
    internal void Leave(IClientSessionHandle current, long id) {
        // Cleanup also runs after a nested reader aborts. Preserve its original
        // failure; all proof reads still require an active, exactly bound attempt.
        if (!ReferenceEquals(tx, current) || current.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction) throw Q.Broken();
        active.Remove(id);
    }
}

/// <summary>Authenticates quality and the full physical-disposition graph within one transaction.</summary>
internal sealed class ProcurementQualityAcceptanceSourceReader(IMongoDatabase database, MongoUnitOfWork unit, IClock clock,
    CatalogAuthoringService catalog, PurchaseOrderService orders, StockSourceSetupService sources, ProcurementQualityProofGraph? graph = null)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);

    /// <summary>The approved-quality snapshot is a pure function of the exact allocation, proposal, decision and receipt rows.</summary>
    internal static BsonDocument Snapshot(BsonDocument allocation, BsonDocument proposal, BsonDocument decision, BsonDocument receipt) {
        var input = Q.Input(proposal["input"]);
        return new BsonDocument { { "allocationId", allocation["_id"] }, { "allocationHash", allocation["snapshotHash"] },
            { "proposalId", proposal["_id"] }, { "proposalHash", proposal["snapshotHash"] },
            { "decisionId", decision["_id"] }, { "decisionHash", decision["snapshotHash"] },
            { "receiptId", receipt["_id"] }, { "receiptHash", receipt["snapshotHash"] },
            { "receiptReference", receipt["input"]["receiptReference"] }, { "acceptedPackages", allocation["packages"] },
            { "lotReference", receipt["input"]["lotReference"] }, { "expiresOn", receipt["input"]["expiresOn"] },
            { "acceptedCondition", input.AcceptedCondition }, { "putawayReference", input.PutawayReference },
            { "segregationReference", input.SegregationReference }, { "recordedAt", decision["recordedAt"] } };
    }
    internal async Task<ProcurementApprovedQualitySource> ReadAsync(IClientSessionHandle tx, long allocationId, CancellationToken ct) {
        if (allocationId <= 0 || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw Q.Broken();
        var allocation = await Rows(Q.Allocations).Find(tx, new BsonDocument { { "_id", allocationId },
            { "$and", new BsonArray { ProcurementQualityAcceptanceSchemas.Validator(Q.Allocations) } } }).SingleOrDefaultAsync(ct) ?? throw Q.Broken();
        if (allocation["snapshotHash"] != Q.Hash(allocation)) throw Q.Broken();
        var proof = graph ?? new ProcurementQualityProofGraph(tx);
        if (proof.TryActive(tx, allocation, out var active)) return active;
        BsonDocument receipt;
        using (SupplierDocumentMatchDiagnostics.Begin("quality.receipt"))
            receipt = await new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, sources, proof)
                .ReadAsync(tx, allocation["receiptId"].AsInt64, ct);
        ProcurementQualityAcceptanceCapacity capacity;
        using (SupplierDocumentMatchDiagnostics.Begin("quality.capacity"))
            capacity = await new ProcurementQualityAcceptanceCapacityReader(database, sources).ReadAsync(tx, receipt, ct);
        if (capacity.Head is null || !capacity.Allocations.Any(row => row["_id"] == allocation["_id"])) throw Q.Broken();
        var proposal = await Rows(Q.Proposals).Find(tx, new BsonDocument { { "_id", allocation["proposalId"] },
            { "$and", new BsonArray { ProcurementQualityAcceptanceSchemas.Validator(Q.Proposals) } } }).SingleOrDefaultAsync(ct) ?? throw Q.Broken();
        var decision = await Rows(Q.Decisions).Find(tx, new BsonDocument { { "_id", allocation["decisionId"] },
            { "$and", new BsonArray { ProcurementQualityAcceptanceSchemas.Validator(Q.Decisions) } } }).SingleOrDefaultAsync(ct) ?? throw Q.Broken();
        var input = Q.Input(proposal["input"]);
        if (proposal["snapshotHash"] != Q.Hash(proposal) || decision["snapshotHash"] != Q.Hash(decision) ||
            proposal["receiptHash"] != receipt["snapshotHash"] || proposal["input"] != Q.Input(input) ||
            proposal["location"] != receipt["location"] || input.ReceiptId != receipt["_id"].AsInt64 ||
            proposal["lotId"] != receipt["lotId"] || proposal["balanceId"] != receipt["balanceId"] ||
            proposal["poolId"] != receipt["poolId"] || proposal["inspectionHash"] != receipt["inspection"]["snapshotHash"] ||
            decision["decision"] != "approve" || decision["proposalId"] != proposal["_id"] ||
            decision["proposalHash"] != proposal["snapshotHash"] || decision["allocationId"] != allocation["_id"] ||
            decision["input"] != proposal["input"] || allocation["proposalHash"] != proposal["snapshotHash"] ||
            allocation["receiptHash"] != receipt["snapshotHash"] || allocation["packages"] != input.AcceptedPackages)
            throw Q.Broken();
        var proposalAt = new DateTimeOffset(proposal["recordedAt"].ToUniversalTime());
        var decisionAt = new DateTimeOffset(decision["recordedAt"].ToUniversalTime());
        if (proposalAt > clock.UtcNow || decisionAt > clock.UtcNow || decisionAt < proposalAt ||
            proposal["policy"]["approvedAt"].ToUniversalTime() > proposalAt.UtcDateTime ||
            proposal["policy"]["expiresAt"].ToUniversalTime() <= decisionAt.UtcDateTime) throw Q.Broken();
        BsonDocument policy;
        using (SupplierDocumentMatchDiagnostics.Begin("quality.policy"))
            policy = await new ProcurementQualityAcceptanceRecords(database, unit, clock, catalog, orders, sources)
                .PolicyAsync(tx, input, proposalAt, false, ct);
        if (proposal["policy"] != Q.PolicySnapshot(policy)) throw Q.Broken();
        // The receipt owner has freshly authenticated current movement provenance. This
        // independent approval binds the original held capture, not a current balance.
        var original = InventoryBson.Balance(InventoryHeldReceiptWriter.After(receipt));
        if (proposal["heldBalance"] != original)
            throw Q.Broken();
        var pool = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", receipt["poolId"]))
            .SingleOrDefaultAsync(ct) ?? throw Q.Broken();
        InventoryGraph.RequirePool(pool);
        if (proposal["pool"]["_id"] != pool["_id"] || proposal["pool"]["location"] != pool["location"] ||
            pool["location"] != receipt["poolSource"]["configuration"]["source"]["input"]["location"] ||
            proposal["sourceConfigurationHash"] != Q.SourceConfigurationHash(proposal["poolSource"].AsBsonDocument))
            throw Q.Broken();
        using (SupplierDocumentMatchDiagnostics.Begin("quality.configuration"))
            await sources.RequireHeldReceiptHistoryAsync(tx, proposal["poolSource"].AsBsonDocument, proposalAt, ct);
        ProcurementQualityAcceptanceRecords.LotFacts(input, receipt, proposalAt);
        ProcurementQualityAcceptanceRecords.LotFacts(input, receipt, decisionAt);
        ProcurementQualityAcceptanceRecords.ObservationTime(input.ObservedAt, receipt, proposal["policy"].AsBsonDocument,
            proposalAt);
        ProcurementQualityAcceptanceRecords.ObservationTime(input.ObservedAt, receipt, proposal["policy"].AsBsonDocument, decisionAt);
        var maker = R.Person(proposal["people"][0]);
        var participants = decision["people"].AsBsonArray.Select(R.Person).ToArray();
        if (maker.UserId != proposal["actorId"].AsInt64 || maker.Verification != PersonVerification.Verified ||
            participants.Length != 2 || participants.Select(person => person.UserId).ToHashSet().Count != 2 ||
            !participants.Any(person => person.UserId == maker.UserId && person.PersonId == maker.PersonId) ||
            !participants.Any(person => person.UserId == decision["actorId"].AsInt64) ||
            !PersonIndependencePolicy.AreIndependent(participants.Single(person => person.UserId == maker.UserId),
                participants.Single(person => person.UserId == decision["actorId"].AsInt64))) throw Q.Broken();
        if (proposal["binding"] != Q.Binding(proposal["actorId"].AsInt64, proposal["actorGrantVersion"].AsInt64,
                input, receipt, proposal["pool"].AsBsonDocument, proposal["sourceConfigurationHash"].AsString,
                proposal["policy"].AsBsonDocument, proposal["capacity"].AsBsonDocument) ||
            proposal["bodyHash"] != AuthorizationStore.CommandHash(Q.ProposeBody(input, proposal["binding"].AsString), proposal["reason"].AsString)) throw Q.Broken();
        var observation = Q.Observation(decision["observation"]);
        ProcurementQualityAcceptanceRecords.ObservationTime(observation.ObservedAt, receipt, proposal["policy"].AsBsonDocument, decisionAt);
        if (observation.AcceptedPackages != input.AcceptedPackages || observation.PutawayReference != input.PutawayReference ||
            observation.SegregationReference != input.SegregationReference || observation.AcceptedCondition != input.AcceptedCondition ||
            observation.LotReference != input.LotReference || observation.ExpiresOn != input.ExpiresOn ||
            observation.NonExpiringReason != input.NonExpiringReason ||
            decision["binding"] != Q.DecisionBinding(decision["actorId"].AsInt64, decision["actorGrantVersion"].AsInt64,
                proposal, ProcurementQualityAcceptanceDecision.Approve, observation, proposal["pool"].AsBsonDocument,
                proposal["sourceConfigurationHash"].AsString, proposal["policy"].AsBsonDocument,
                decision["capacityBefore"].AsBsonDocument) ||
            decision["bodyHash"] != AuthorizationStore.CommandHash(Q.DecideBody(proposal["_id"].AsInt64, 1,
                ProcurementQualityAcceptanceDecision.Approve, observation, decision["binding"].AsString), decision["reason"].AsString)) throw Q.Broken();
        using (SupplierDocumentMatchDiagnostics.Begin("quality.effects")) {
            await ProcurementQualityAcceptanceRecords.RequireEffectsAsync(database, tx, proposal, ct);
            await ProcurementQualityAcceptanceRecords.RequireEffectsAsync(database, tx, decision, ct, proposal);
        }
        var snapshot = Snapshot(allocation, proposal, decision, receipt);
        var result = new ProcurementApprovedQualitySource(receipt, proposal, decision, allocation, capacity, snapshot);
        proof.Enter(tx, result);
        try {
            using var dispositionStage = SupplierDocumentMatchDiagnostics.Begin("quality.disposition");
            var inspected = receipt["inspection"]["input"]["lines"][receipt["inspectionLineNumber"].AsInt32 - 1];
            var claims = new SupplierClaimRecords(database, unit, clock, catalog, orders, proof);
            var disposition = await new ProcurementQuarantineDisposition(database, claims).ReadAsync(tx,
                SupplierClaimBasis.PhysicalReceived, receipt["inspectionId"].AsInt64, receipt["inspectionLineNumber"].AsInt32,
                receipt["inspection"]["snapshotHash"].AsString, inspected["receivedPackages"].AsInt32,
                receipt["location"].AsBsonDocument, receipt["orderId"].AsInt64, receipt["supplierId"].AsInt64, ct);
            if (disposition.Head is null || !disposition.IssuePackages.All(issue => issue.Value >= 0)) throw Q.Broken();
            return result;
        } finally { proof.Leave(tx, allocationId); }
    }


}
