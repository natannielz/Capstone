using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Inventory;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;

namespace UnitToko.Platform.Fulfillment;

internal sealed record FinancialReturnSource(BsonDocument Root, BsonDocument Receipt, BsonDocument Inspection, BsonDocument Proposal,
    ImmutableArray<long> Participants, Func<CancellationToken, Task> FenceReadAsync, Func<CancellationToken, Task> FenceNewAsync, Action RequireCurrent,
    FinancialInvoiceSource? IssuedInvoice = null);

public sealed partial class AfterSalesCaseService
{
    internal IMongoDatabase FinancialSourceDatabase => database;
    internal Task FinancialStorageAsync(CancellationToken ct) => StorageAsync(ct);

    // A financial consumer uses the actual caller's transaction and source read grants. Historical
    // custody/inspection authority does not confer financial approval or become a new stock grant.
    internal async Task<FinancialReturnSource> FinancialResolutionSourceAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long caseId, long returnId, string expectedAction, CancellationToken ct,
        FinancialInvoiceSource? issuedInvoice = null, bool captureIssuedInvoice = false) {
        if (caseId <= 0 || returnId <= 0 || expectedAction is not ("credit" or "refund")) throw ReturnResolutionPolicy.Invalid();
        void Guard() {
            if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || actor.UserId <= 0 || actor.GrantVersion < 0 ||
                actor.UserId != authority.UserId || actor.GrantVersion != authority.GrantVersion) throw R.Broken();
        }
        static BsonDocument Copy(BsonDocument row, bool root = false) {
            var copy = row.DeepClone().AsBsonDocument;
            // The case's shared writer fence is mutable; every business/source field is immutable.
            if (root) copy.Remove("fence"); return copy;
        }
        if (captureIssuedInvoice) {
            if (issuedInvoice is not null) throw R.Broken(); Guard(); _ = Query(actor, authority, Permissions.CaseRead);
            var candidate = await records.Rows(R.Collection).Find(tx, new BsonDocument("_id", caseId)).SingleOrDefaultAsync(ct) ?? throw R.Broken();
            Require(actor, authority, candidate, Permissions.CaseRead);
            // The real case owner validates its schema/hash/input/audit/idempotency/outbox
            // before following the actual stored invoice identity, never a caller-supplied ID.
            var root = await records.ReadAsync(tx, caseId, ct); Require(actor, authority, root, Permissions.CaseRead);
            if (root["requestedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime) throw R.Broken();
            issuedInvoice = await invoices.FinancialEntitlementSourceAsync(actor, authority, tx, root["source"]["invoiceId"].AsInt64, ct);
            issuedInvoice.RequireContext(actor, authority, tx, root["source"]["invoiceId"].AsInt64, root["source"]["invoiceHash"].AsString);
            if (!InvoiceRecords.Equal(InvoiceService.CaseSnapshot(issuedInvoice.Invoice), root["source"])) throw R.Broken();
        }
        async Task<(BsonDocument Root, BsonDocument Receipt, BsonDocument Inspection, BsonDocument Proposal, long DecidedBy)> ReadAsync(CancellationToken token) {
            Guard(); var root = await AuthorizedAsync(actor, authority, tx, caseId, Permissions.CaseRead, token, issuedInvoice);
            var receipt = await InspectionReceiptAsync(actor, authority, tx, root, returnId, Permissions.CaseRead, token, issuedInvoice);
            var inspection = await new ReturnInspectionRecords(database, clock).ReadAsync(tx, receipt, token) ?? throw R.Broken();
            var proposal = await new ReturnResolutionRecords(database, clock).ReadAsync(tx, inspection, token) ?? throw R.Broken();
            var decision = await new AfterSalesDecisionRecords(database).ReadAsync(tx, root, token) ?? throw R.Broken();
            if (proposal["input"]["action"] != expectedAction || proposal["revision"] != 1L || proposal["state"] != "resolution_requested" ||
                inspection["input"]["lines"].AsBsonArray.Any(line => line["identity"] == "different") ||
                receipt["source"]["decidedAt"].CompareTo(root["requestedAt"]) < 0 || receipt["receivedAt"].CompareTo(receipt["source"]["decidedAt"]) < 0 ||
                inspection["inspectedAt"].CompareTo(receipt["receivedAt"]) < 0 || proposal["requestedAt"].CompareTo(inspection["inspectedAt"]) < 0) throw R.Broken();
            Guard(); return (root, receipt, inspection, proposal, decision["actorId"].AsInt64);
        }
        static ImmutableArray<long> People((BsonDocument Root, BsonDocument Receipt, BsonDocument Inspection, BsonDocument Proposal, long DecidedBy) source) =>
            [.. new[] { source.Root["requestedBy"].AsInt64, source.DecidedBy, source.Receipt["actorId"].AsInt64,
                source.Inspection["actorId"].AsInt64, source.Proposal["actorId"].AsInt64 }.Distinct().Order()];
        var source = await ReadAsync(ct);
        var rootSnapshot = Copy(source.Root, true); var receiptSnapshot = Copy(source.Receipt); var inspectionSnapshot = Copy(source.Inspection); var proposalSnapshot = Copy(source.Proposal);
        var participants = People(source);
        void Current() {
            Guard(); Require(actor, authority, rootSnapshot, Permissions.CaseRead);
            issuedInvoice?.RequireContext(actor, authority, tx, rootSnapshot["source"]["invoiceId"].AsInt64, rootSnapshot["source"]["invoiceHash"].AsString);
            if (proposalSnapshot["requestedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime) throw R.Broken();
        }
        async Task FenceAsync(bool newEffect, CancellationToken token) {
            // Observation reuse is confined to deferred M55 reads; producer/new-effect
            // consumers retain their existing independent invoice/stock fence path.
            if (newEffect && issuedInvoice is not null) throw R.Broken();
            Current(); var latest = await ReadAsync(token);
            if (!InvoiceRecords.Equal(Copy(latest.Root, true), rootSnapshot) || !InvoiceRecords.Equal(latest.Receipt, receiptSnapshot) ||
                !InvoiceRecords.Equal(latest.Inspection, inspectionSnapshot) || !InvoiceRecords.Equal(latest.Proposal, proposalSnapshot) ||
                !People(latest).SequenceEqual(participants)) throw R.Broken();
            await this.FenceAsync(actor, authority, tx, latest.Root, Permissions.CaseRead, token);
            if (newEffect) await invoices.CaseReturnFenceAsync(tx, latest.Root["source"].AsBsonDocument, token);
            Current();
        }
        Current(); return new(Copy(rootSnapshot), Copy(receiptSnapshot), Copy(inspectionSnapshot), Copy(proposalSnapshot), participants,
            token => FenceAsync(false, token), token => FenceAsync(true, token), Current, issuedInvoice);
    }
}
