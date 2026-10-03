using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.PurchaseOrderBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;
using S = UnitToko.Platform.Procurement.SupplierBson;

namespace UnitToko.Platform.Procurement;

internal sealed class PurchaseOrderFundingCapture(object owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
    BsonDocument source, Func<CancellationToken, Task> fence, Action current)
{
    private readonly long transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
    private readonly string hash = B.Hash(source);
    internal BsonDocument Source { get; } = source;
    internal void RequireContext(object service, ActorContext caller, AuthorizationSnapshot auth, IClientSessionHandle session) {
        if (!ReferenceEquals(service, owner) || caller != actor || !ReferenceEquals(auth, authority) || !ReferenceEquals(session, tx) || !session.IsInTransaction ||
            session.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction || B.Hash(Source) != hash) throw B.Broken(); current();
    }
    internal void RequireCurrent() => RequireContext(owner, actor, authority, tx);
    internal BsonDocument RequireSource(object service, IClientSessionHandle session, BsonDocument expected) {
        RequireContext(service, actor, authority, session);
        if (!Source.Equals(expected)) throw B.Broken();
        return Source;
    }
    internal async Task FenceAsync(CancellationToken ct) { RequireCurrent(); await fence(ct); RequireCurrent(); ct.ThrowIfCancellationRequested(); }
}

public sealed partial class PurchaseOrderService
{
    internal async Task<BsonDocument> FundingSourceAsync(IMongoDatabase owner, IClientSessionHandle tx, long orderId, long orderVersion, long approvalId, CancellationToken ct) {
        if (!ReferenceEquals(owner.Client, database.Client) || owner.DatabaseNamespace != database.DatabaseNamespace || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
        var order = await records.RowAsync(tx, B.Orders, orderId, ct); Equal(order["version"], orderVersion); Equal(order["approvalId"], approvalId);
        var proposal = await records.ProposalAsync(tx, order["proposalId"].AsInt64, ct); var approval = await records.DecisionAsync(tx, approvalId, proposal, ct); Equal(approval["decision"], "approve"); Equal(approval["orderId"], orderId);
        var input = B.Input(proposal["input"]); var request = await records.RequestAsync(tx, input, ct);
        await requests.RequirePurchaseOrderAllocationsAsync(database, tx, request.Request, request.Decision, ct);
        var allocations = await Rows(ProcurementRequestPurchaseOrderSchemas.Allocations).Find(tx, new BsonDocument("orderId", orderId)).Sort(new BsonDocument("skuId", 1)).ToListAsync(ct);
        if (allocations.Count != input.Lines.Length) throw B.Broken();
        return new BsonDocument { { "order", order }, { "proposal", proposal }, { "approval", approval },
            { "request", request.Request }, { "requestDecision", request.Decision }, { "allocations", new BsonArray(allocations.Select(row => new BsonDocument { { "id", row["_id"] }, { "hash", row["snapshotHash"] } })) } };
    }
    internal async Task<PurchaseOrderFundingCapture> CaptureFundingAsync(IMongoDatabase owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        long orderId, long orderVersion, long approvalId, string permission, CancellationToken ct) {
        if (permission is not (Permissions.FundingRead or Permissions.FundingRequest or Permissions.FundingApprove)) throw B.Broken();
        await StorageAsync(ct); _ = Query(actor, authority, permission);
        var route = await Rows(B.Orders).Find(tx, new BsonDocument("_id", orderId)).Project(new BsonDocument("location", 1)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        var context = await ContextAsync(actor, authority, tx, R.Location(route["location"]), permission, ct);
        var source = await FundingSourceAsync(owner, tx, orderId, orderVersion, approvalId, ct);
        var proposal = source["proposal"].AsBsonDocument; var approval = source["approval"].AsBsonDocument;
        BindPeople(context, proposal, approval); await PeopleAsync(context, approval["actorId"].AsInt64, proposal["actorId"].AsInt64, ct);
        var input = B.Input(proposal["input"]); if (input.PaymentTerms.RequiredAdvance.Value <= 0) throw new PersistenceConflictException("procurement_funding_ineligible", "This approved purchase order has no supplier advance requirement");
        // Current trust is checked without re-pricing or re-allocating the already approved PO.
        foreach (var name in new[] { S.Heads, S.Policies }) await InventoryRepository.RequireStorageAsync(database, name, SupplierSchemas.Validator(name), SupplierSchemas.Keys(name), ct);
        var supplierRecords = new SupplierRecords(database, catalog); var head = await supplierRecords.HeadAsync(tx, input.SupplierId, ct);
        Equal(head["versionId"], input.SupplierVersionId); Equal(head["version"], input.SupplierVersion);
        var supplierProposal = await supplierRecords.ProposalAsync(tx, proposal["supplier"]["proposalId"].AsInt64, ct);
        var supplierDecision = await supplierRecords.DecisionForAsync(tx, supplierProposal, ct) ?? throw B.Broken();
        var supplierObservation = await supplierRecords.ObservationForAsync(tx, supplierProposal, ct) ?? throw B.Broken();
        var originalPeople = new[] { supplierProposal, supplierDecision, supplierObservation }.SelectMany(row => row["people"].AsBsonArray.Select(S.Person)).ToArray();
        var people = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, [.. originalPeople.Select(person => person.UserId).Distinct().Order()], ct);
        if (people.Evidence.Any(person => person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 }) ||
            originalPeople.Any(original => people.Evidence.Single(person => person.UserId == original.UserId).PersonId != original.PersonId)) throw new AuthorizationDeniedException("supplier_person_changed");
        var policy = await supplierRecords.RowAsync(tx, S.Policies, supplierProposal["policy"]["_id"].AsInt64, ct); Equal(S.PolicySnapshot(policy), supplierProposal["policy"]);
        void CurrentSource() { Current(context); people.RequireCurrent(tx, clock.UtcNow); SupplierRecords.PolicyAt(policy, Now); }
        async Task Fence(CancellationToken token) {
            CurrentSource();
            foreach (var (name, row) in new[] { (S.Heads, head), (S.Policies, policy) }) {
                if (row["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(name).UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
                row["fence"] = row["fence"].AsInt64 + 1;
            }
            await new PersonAuthorizationFence(database, clock).TouchAsync(tx, people, token); await FenceAsync(context, token); CurrentSource(); token.ThrowIfCancellationRequested();
        }
        CurrentSource(); return new(this, actor, authority, tx, source, Fence, CurrentSource);
    }
    internal async Task FenceFundingAsync(IMongoDatabase owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, PurchaseOrderFundingCapture capture, CancellationToken ct) {
        if (!ReferenceEquals(owner.Client, database.Client) || owner.DatabaseNamespace != database.DatabaseNamespace) throw B.Broken();
        capture.RequireContext(this, actor, authority, tx); await capture.FenceAsync(ct);
    }
    internal static BsonDocument FundingOrderView(BsonDocument source) {
        var order = source["order"]; var proposal = source["proposal"].AsBsonDocument; var input = B.Input(proposal["input"]);
        return new() { { "id", order["_id"] }, { "version", order["version"] }, { "approvalId", order["approvalId"] }, { "proposalId", order["proposalId"] },
            { "route", source["request"]["input"]["route"] }, { "businessDate", B.Date(input.BusinessDate) }, { "currency", input.Currency },
            { "supplier", B.SupplierView(proposal["supplier"].AsBsonDocument) }, { "total", PurchaseOrderRules.Totals(input).Total.ToString() },
            { "requiredAdvance", input.PaymentTerms.RequiredAdvance.ToString() }, { "paymentTerms", proposal["input"]["paymentTerms"] },
            { "commercialReference", proposal["input"]["commercialReference"] }, { "externalOrderReference", proposal["input"]["externalOrderReference"] },
            { "lines", B.Lines(input, source["request"].AsBsonDocument) } };
    }
}
