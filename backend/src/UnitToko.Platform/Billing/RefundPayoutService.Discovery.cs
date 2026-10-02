using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;
using B = UnitToko.Platform.Billing.RefundPayoutBson;
using R = UnitToko.Platform.Billing.RefundPayoutRecords;

namespace UnitToko.Platform.Billing;
public sealed partial class RefundPayoutService {
    public async Task<BsonDocument> ReadApprovedRefundAsync(ActorContext actor, long caseId, long returnId, CancellationToken ct = default) {
        if (caseId <= 0 || returnId <= 0) throw RefundPayoutPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var caseSource = await cases.RefundDiscoverySourceAsync(actor, authority, tx, caseId, returnId, token);
            var resources = PaymentDraftBson.Scopes(caseSource.Case["scopes"].AsBsonArray);
            AuthorizationQuery[] RequireRead() {
                if (actor.Assurance is not (FactorAssurance.MultiFactor or FactorAssurance.PhishingResistant) || actor.FactorCompletedAt > clock.UtcNow || clock.UtcNow - actor.FactorCompletedAt > TimeSpan.FromMinutes(5)) throw new AuthorizationDeniedException("step_up_required");
                return new[] { Permissions.DisbursementRecord, Permissions.CaseRead, Permissions.InvoiceRead, Permissions.PaymentReviewRead }.Select(permission => {
                    var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query);
                    if (resources.Length == 0 || resources.Any(scope => !ScopePolicy.IsResourceActive(scope, authority.Directory) || !query.Matches(scope))) throw B.Broken(); return query;
                }).ToArray();
            }
            _ = RequireRead();
            var candidate = await records.Rows(A.Events).Find(tx, new BsonDocument { { "caseId", caseId }, { "returnId", returnId }, { "action", "refund" } }).SingleOrDefaultAsync(token);
            BsonDocument? approvalResult = null, payoutResult = null, draftResult = null;
            PersonAuthorityProof? people = null; RefundPayoutApprovalSource? approvalSource = null;
            if (candidate is not null) {
                approvalSource = await approvals.PayoutSourceAsync(actor, authority, tx, candidate["_id"].AsInt64, token);
                R.Equal(approvalSource.Approval["caseId"], caseId); R.Equal(approvalSource.Approval["returnId"], returnId); R.Equal(approvalSource.Approval["invoiceId"], caseSource.Case["source"]["invoiceId"]);
                var evidence = new ApprovalRequest(1, 1, ApprovedBy: approvalSource.ApprovedBy.UserId);
                var accountDecision = ApprovalPolicy.Evaluate(actor.UserId, Permissions.DisbursementRecord, evidence);
                if (!accountDecision.Allowed) throw new AuthorizationDeniedException(accountDecision.Code);
                people = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, PersonApprovalPolicy.RequiredAccounts(actor.UserId, Permissions.DisbursementRecord, evidence), token);
                var personDecision = PersonApprovalPolicy.Evaluate(actor.UserId, Permissions.DisbursementRecord, evidence, people.Evidence);
                if (!personDecision.Allowed) throw new AuthorizationDeniedException(personDecision.Code);
                R.Equal(people.Evidence.Single(person => person.UserId == approvalSource.ApprovedBy.UserId).PersonId ?? throw B.Broken(), approvalSource.ApprovedBy.PersonId ?? throw B.Broken());
                await new PersonAuthorizationFence(database, clock).TouchAsync(tx, people, token);
                approvalResult = A.Result(approvalSource.Approval);
                var payout = await records.Rows(B.Payouts).Find(tx, new BsonDocument("financialApprovalId", approvalSource.Approval["_id"])).SingleOrDefaultAsync(token);
                if (payout is not null) payoutResult = B.Result(await records.PayoutAsync(tx, payout["_id"].AsInt64, false, token));
                else foreach (var (table, field, payload) in new[] { ("idempotencyRecords", "operation", "result"), ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload") })
                    if (await records.Rows(table).Find(tx, new BsonDocument { { field, B.Operation }, { payload + ".approvalId", approvalSource.Approval["_id"] } }).AnyAsync(token)) throw B.Broken();
                var draft = await records.Rows(B.Drafts).Find(tx, new BsonDocument { { "financialApprovalId", approvalSource.Approval["_id"] }, { "userId", actor.UserId } }).Sort(new BsonDocument("_id", -1)).FirstOrDefaultAsync(token);
                if (draft is not null && payout is null) {
                    var exact = await records.DraftAsync(tx, draft["_id"].AsInt64, token);
                    // A summary admits the exact historical draft and its original approval.
                    // Resuming or changing it still goes through the current policy/bank reader.
                    R.Equal(exact["source"]["approval"], ApprovalSnapshot(approvalSource.ApprovedProof));
                    var party = approvalSource.Approval["party"].AsBsonDocument;
                    R.Equal(exact["scope"], new BsonDocument { { "fulfillmentOrganizationId", party["fulfillmentOrganizationId"] }, { "shopId", party["shopId"] }, { "channelId", party["channelId"] }, { "warehouseId", BsonNull.Value } });
                    R.Equal(exact["scopes"], approvalSource.Invoice["scopes"]);
                    await records.TouchAsync(tx, B.Drafts, exact, token); draftResult = B.DraftResult(exact);
                }
            }
            else {
                // The factual case/receipt admission above precedes every absence check. Persisted
                // owner effects cannot be treated as a new, unapproved refund after source loss.
                foreach (var (table, field, payload) in new[] { ("idempotencyRecords", "operation", "result"), ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload") })
                    if (await records.Rows(table).Find(tx, new BsonDocument { { field, A.Operation }, { payload + ".caseId", caseId }, { payload + ".returnId", returnId }, { payload + ".action", "refund" } }).AnyAsync(token)) throw B.Broken();
                foreach (var table in new[] { B.Drafts, B.Payouts })
                    if (await records.Rows(table).Find(tx, new BsonDocument { { "caseId", caseId }, { "returnId", returnId } }).AnyAsync(token)) throw B.Broken();
            }
            await caseSource.FenceAsync(token); if (approvalSource is not null) await approvalSource.FenceAsync(token);
            var queries = RequireRead(); var clauses = queries.SelectMany(query => query.Clauses.Where(clause => resources.Any(clause.Matches))).ToArray();
            await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
                clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory))).Distinct(), [],
                authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, token);
            _ = RequireRead(); caseSource.RequireCurrent(); approvalSource?.RequireCurrent(); people?.RequireCurrent(tx, clock.UtcNow);
            return new BsonDocument { { "approval", approvalResult is null ? BsonNull.Value : approvalResult }, { "payout", payoutResult is null ? BsonNull.Value : payoutResult }, { "draft", draftResult is null ? BsonNull.Value : draftResult } };
        }, ct);
    }
}
