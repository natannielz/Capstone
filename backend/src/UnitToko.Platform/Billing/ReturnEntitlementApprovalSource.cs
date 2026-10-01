using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;

namespace UnitToko.Platform.Billing;

internal sealed record FinancialEntitlementRequestSource(BsonDocument Request, FinancialReturnSource Returned, FinancialInvoiceSource Invoice,
    BsonDocument Pool, Action RequireCurrent, Func<CancellationToken, Task> FenceReadAsync, Func<CancellationToken, Task> FenceNewAsync);

public sealed partial class ReturnEntitlementService
{
    internal IMongoDatabase ApprovalSourceDatabase => database;
    internal Task ApprovalStorageAsync(CancellationToken ct) => StorageAsync(ct);

    // Approval consumes the immutable maker event under source read grants. Its finance permission,
    // independent reviewer and money effects belong to the financial command owner, never this adapter.
    internal async Task<FinancialEntitlementRequestSource> ApprovalSourceAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long caseId, long returnId, string action, bool newEffect, CancellationToken ct) {
        if (caseId <= 0 || returnId <= 0 || action is not ("credit" or "refund")) throw ReturnEntitlementPolicy.Invalid();
        void Session() {
            if (actor is null || authority is null || tx is null || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) ||
                actor.UserId != authority.UserId || actor.GrantVersion != authority.GrantVersion) throw B.Broken();
            _ = actor.Validate();
        }
        void RequireRead(FinancialInvoiceSource invoice) {
            Session(); var resources = PaymentDraftBson.Scopes(invoice.MoneySource["scopes"].AsBsonArray);
            foreach (var permission in new[] { Permissions.CaseRead, Permissions.InvoiceRead }) {
                var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query);
                if (resources.Length == 0 || resources.Any(resource => !ScopePolicy.IsResourceActive(resource, authority.Directory) || !query.Matches(resource))) throw B.Broken();
            }
        }
        async Task<(BsonDocument Request, FinancialReturnSource Returned, FinancialInvoiceSource Invoice)> ReadAsync(CancellationToken token) {
            Session(); token.ThrowIfCancellationRequested();
            var returned = await cases.FinancialResolutionSourceAsync(actor, authority, tx, caseId, returnId, action, token);
            var invoice = await invoices.FinancialEntitlementSourceAsync(actor, authority, tx, returned.Root["source"]["invoiceId"].AsInt64, token); RequireRead(invoice);
            var request = await records.RawAsync(tx, returnId, token);
            if (request["caseId"] != caseId || request["action"] != action || request["invoiceId"] != invoice.Invoice["_id"]) throw B.Broken();
            RequireHistorySource(request, returned, invoice); return (request, returned, invoice);
        }
        async Task<(BsonDocument State, List<FinancialReturnSource> History)> PoolAsync(
            (BsonDocument Request, FinancialReturnSource Returned, FinancialInvoiceSource Invoice) target, CancellationToken token) {
            var history = new List<FinancialReturnSource>();
            var pool = await records.PoolAsync(tx, target.Invoice.MoneySource, ReturnEntitlementBson.IssuedLines(target.Invoice.Invoice), async request => {
                var returned = request["_id"] == returnId ? target.Returned :
                    await cases.FinancialResolutionSourceAsync(actor, authority, tx, request["caseId"].AsInt64, request["_id"].AsInt64, request["action"].AsString, token);
                RequireHistorySource(request, returned, target.Invoice); await returned.FenceReadAsync(token); history.Add(returned);
            }, token);
            return (pool.State, history);
        }
        var frozen = await ReadAsync(ct); var pool = await PoolAsync(frozen, ct);
        var requestBaseline = frozen.Request.DeepClone().AsBsonDocument; var poolBaseline = pool.State.DeepClone().AsBsonDocument;
        PersonAuthorityProof? people = null;
        if (newEffect) {
            people = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx,
                [.. frozen.Returned.Participants.Append(frozen.Request["actorId"].AsInt64).Append(actor.UserId).Distinct().Order()], ct);
            await new PersonAuthorizationFence(database, clock).TouchAsync(tx, people, ct);
        }
        void Current() {
            RequireRead(frozen.Invoice); frozen.Returned.RequireCurrent(); frozen.Invoice.RequireCurrent();
            foreach (var returned in pool.History) returned.RequireCurrent(); people?.RequireCurrent(tx, clock.UtcNow);
            if (requestBaseline["requestedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime) throw B.Broken();
        }
        async Task Fence(bool commandEffect, CancellationToken token) {
            Current(); if (commandEffect && !newEffect) throw B.Broken();
            var latest = await ReadAsync(token); ReturnEntitlementRecords.Equal(latest.Request, requestBaseline);
            var currentPool = await PoolAsync(latest, token); ReturnEntitlementRecords.Equal(currentPool.State, poolBaseline);
            await latest.Invoice.FenceAsync(token);
            if (commandEffect) await latest.Returned.FenceNewAsync(token); else await latest.Returned.FenceReadAsync(token);
            foreach (var returned in currentPool.History) returned.RequireCurrent();
            if (people is not null) await new PersonAuthorizationFence(database, clock).TouchAsync(tx, people, token);
            Current(); token.ThrowIfCancellationRequested();
        }
        Current(); return new(requestBaseline.DeepClone().AsBsonDocument, frozen.Returned, frozen.Invoice, poolBaseline.DeepClone().AsBsonDocument,
            Current, token => Fence(false, token), token => Fence(true, token));
    }
}
