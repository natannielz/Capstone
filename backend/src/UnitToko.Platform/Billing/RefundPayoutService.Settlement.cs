using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using D = UnitToko.Platform.Billing.RefundSettlementBson;
using M = UnitToko.Platform.Billing.PaymentAllocationBson;
using MR = UnitToko.Platform.Billing.PaymentAllocationRecords;
using R = UnitToko.Platform.Billing.RefundPayoutRecords;

namespace UnitToko.Platform.Billing;

public sealed record RefundSettlementPreview(string Binding, BsonDocument View);
public sealed partial class RefundPayoutService
{
    private sealed record SettlementFrame(ActorContext Actor, AuthorizationSnapshot Authority, IClientSessionHandle Tx,
        BsonDocument Payout, RefundPayoutApprovalSource Approval, RefundSettlementClaimSource Bank, BsonDocument Policy,
        RefundSettlementInput Input, PersonAuthorityProof People, BsonDocument Review) {
        internal ResourceScope[] Resources => PaymentDraftBson.Scopes(Approval.Invoice["scopes"].AsBsonArray);
    }
    private async Task SettlementStorageAsync(CancellationToken ct) {
        await StorageAsync(ct);
        foreach (var table in RefundSettlementSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, table, RefundSettlementSchemas.Validator(table), RefundSettlementSchemas.Keys(table), ct);
    }
    private AuthorizationQuery[] RequireSettlement(ActorContext actor, AuthorizationSnapshot authority, ResourceScope[] resources) {
        if (actor.Assurance is not (FactorAssurance.MultiFactor or FactorAssurance.PhishingResistant) || actor.FactorCompletedAt > clock.UtcNow ||
            clock.UtcNow - actor.FactorCompletedAt > TimeSpan.FromMinutes(5)) throw new AuthorizationDeniedException("step_up_required");
        return new[] { Permissions.DisbursementVerify, Permissions.CaseRead, Permissions.InvoiceRead, Permissions.PaymentReviewRead }.Select(permission => {
            var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query);
            if (resources.Length == 0 || resources.Any(resource => !ScopePolicy.IsResourceActive(resource, authority.Directory) || !query.Matches(resource))) throw M.Broken();
            return query;
        }).ToArray();
    }
    private void SettlementCurrent(SettlementFrame frame) {
        _ = RequireSettlement(frame.Actor, frame.Authority, frame.Resources); frame.Approval.RequireCurrent(); frame.Bank.RequireCurrent();
        frame.People.RequireCurrent(frame.Tx, clock.UtcNow); D.PolicyAt(frame.Policy, Now); _ = RefundSettlementPolicy.Copy(frame.Input);
    }
    private async Task SettlementAuthorityFenceAsync(SettlementFrame frame, CancellationToken ct) {
        var live = await records.Rows(D.Policies).Find(frame.Tx, new BsonDocument("_id", frame.Policy["_id"])).SingleOrDefaultAsync(ct) ?? throw M.Broken();
        R.Equal(M.Snapshot(live), M.Snapshot(frame.Policy)); await records.TouchAsync(frame.Tx, D.Policies, live, ct);
        await SettlementReadAuthorityFenceAsync(frame.Actor, frame.Authority, frame.Tx, frame.Resources, ct);
        SettlementCurrent(frame); ct.ThrowIfCancellationRequested();
    }
    private async Task SettlementReadAuthorityFenceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ResourceScope[] resources, CancellationToken ct) {
        var clauses = RequireSettlement(actor, authority, resources).SelectMany(query => query.Clauses.Where(clause => resources.Any(clause.Matches))).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory))).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        if (actor.Session is { } session) {
            var live = await records.Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct);
            if (live is null || live["idleExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime || live["absoluteExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime) throw new AuthorizationDeniedException("stale_or_inactive_session");
        }
        _ = RequireSettlement(actor, authority, resources); ct.ThrowIfCancellationRequested();
    }
    private async Task<SettlementFrame> PrepareSettlementAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long payoutId, RefundSettlementInput input, CancellationToken ct) {
        input = RefundSettlementPolicy.Copy(input);
        if (payoutId <= 0 || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || actor.UserId != authority.UserId || actor.GrantVersion != authority.GrantVersion) throw M.Broken();
        var payout = await records.PayoutAsync(tx, payoutId, false, ct);
        var resources = PaymentDraftBson.Scopes(payout["scopes"].AsBsonArray); _ = RequireSettlement(actor, authority, resources);
        var approval = await approvals.PayoutSourceAsync(actor, authority, tx, payout["financialApprovalId"].AsInt64, ct);
        R.Equal(payout["approvalHash"], approval.Approval["snapshotHash"]);
        R.Equal(payout["source"]["approval"], ApprovalSnapshot(approval.ApprovedProof));
        var originalDraft = await records.DraftAsync(tx, payout["draftId"].AsInt64, ct);
        _ = await BankReceiptService.ReadPayoutBankHistoryAsync(database, tx, payout["source"]["payingBank"].AsBsonDocument,
            new(originalDraft["createdAt"].ToUniversalTime()), ct);
        var bank = await banks.CaptureSettlementClaimAsync(actor, authority, tx, payout["input"]["payingAccountId"].AsInt64,
            payout["input"]["payingAccountVersionId"].AsInt64, input.BankImportId, input.BankRowNumber, ct);
        var policy = await records.Rows(D.Policies).Find(tx, new BsonDocument("$and", new BsonArray {
            new BsonDocument("scope", payout["scope"]), RefundSettlementSchemas.Validator(D.Policies) })).SingleOrDefaultAsync(ct) ?? throw M.Changed("refund_settlement_policy_unavailable");
        D.PolicyAt(policy, Now);
        var recorder = payout["recordedBy"].AsInt64; var importer = bank.Debit["importedBy"].AsInt64;
        var people = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, [.. new[] { actor.UserId, recorder, importer }.Distinct().Order()], ct);
        if (!PersonIndependencePolicy.AreIndependent(people.Evidence.Single(person => person.UserId == actor.UserId),
            people.Evidence.Single(person => person.UserId == recorder), people.Evidence.Single(person => person.UserId == importer))) throw new AuthorizationDeniedException("independent_person_required");
        await new PersonAuthorizationFence(database, clock).TouchAsync(tx, people, ct);
        var originalImporter = ReceivingAccountBson.Person(bank.BankSource.Import["personAtImport"]);
        if (originalImporter.Verification == PersonVerification.Verified)
            R.Equal(people.Evidence.Single(person => person.UserId == importer).PersonId ?? throw M.Broken(), originalImporter.PersonId ?? throw M.Broken());
        var receiptIds = approval.Receipts.Select(value => value["receiptId"].AsInt64).ToArray();
        var paid = await new MR(database, clock).PaidRefundsAsync(tx, receiptIds, payout["party"].AsBsonDocument, ct);
        var review = SettlementReview(actor, payout, approval, bank, input, people, paid);
        var frame = new SettlementFrame(actor, authority, tx, payout, approval, bank, policy, input, people, review); SettlementCurrent(frame); return frame;
    }
    private static System.Collections.Immutable.ImmutableArray<ReturnFinancialLinkDelta> SettlementReleases(BsonArray lines) =>
        [.. lines.Select(line => new ReturnFinancialLinkDelta(new(line["originalAllocationId"].AsInt64), new(line["receiptId"].AsInt64),
            InvoiceBson.Money(line["originallyAllocated"]), InvoiceBson.Money(line["previouslyReleased"]), InvoiceBson.Money(line["amount"]), InvoiceBson.Money(line["releasedAfter"])) )];
    private BsonDocument SettlementReview(ActorContext actor, BsonDocument payout, RefundPayoutApprovalSource approval, RefundSettlementClaimSource bank,
        RefundSettlementInput input, PersonAuthorityProof people, BsonArray paid) {
        var party = M.Party(payout["party"].AsBsonDocument); var source = bank.BankSource; var selected = source.Row;
        var account = new RefundPayoutPayingAccount(bank.Debit["accountId"].AsInt64, bank.Debit["accountVersionId"].AsInt64,
            AccountingBson.ReadKey(source.PayingBank.Book));
        R.Equal(bank.Debit["accountId"], payout["input"]["payingAccountId"]); R.Equal(bank.Debit["accountVersionId"], payout["input"]["payingAccountVersionId"]);
        R.Equal(source.PayingBank.CashAccount["accountId"], payout["source"]["payingBank"]["cashAccountId"]);
        R.Equal(source.PayingBank.CashAccount["accountCode"], payout["source"]["payingBank"]["cashAccountCode"]);
        if (InvoiceBson.Date(selected["bookedDate"]) < InvoiceBson.Date(payout["input"]["transferDate"]) ||
            InvoiceBson.Date(selected["bookedDate"]) > AccountingCalendar.BusinessDate(clock.UtcNow)) throw RefundSettlementPolicy.Invalid("refund_settlement_bank_mismatch");
        var releases = D.Releases(approval.Approval);
        var result = RefundSettlementPolicy.Review(new(new(payout["_id"].AsInt64), new(approval.Approval["_id"].AsInt64), party,
                InvoiceBson.Money(payout["input"]["amount"]), account, payout["recordedBy"].AsInt64), SettlementReleases(releases),
            [.. approval.Receipts.Select(receipt => new ReceiptFunds(new(receipt["receiptId"].AsInt64), receipt["before"]["version"].AsInt64, party, "IDR",
                InvoiceBson.Money(receipt["receiptTotal"]), InvoiceBson.Money(receipt["before"]["allocated"]), InvoiceBson.Money(receipt["before"]["reservedForRefund"])))],
            [.. paid.Select(row => new RefundSettlementPaid(new(row["payoutId"].AsInt64), new(row["financialApprovalId"].AsInt64), M.Party(row["party"].AsBsonDocument),
                row["canonicalIdentity"].AsString, InvoiceBson.Money(row["amount"]), SettlementReleases(row["releases"].AsBsonArray)))],
            new(account, bank.Debit["physicalIdentity"].AsString, BankStatementBson.Format(source.Import["profile"]["format"]),
                new(selected["number"].AsInt32, selected["transactionId"].AsString, InvoiceBson.Date(selected["bookedDate"]), selected["direction"].AsString,
                    InvoiceBson.Money(selected["amount"]), selected["payerName"].IsBsonNull ? null : selected["payerName"].AsString,
                    selected["reference"].IsBsonNull ? null : selected["reference"].AsString, selected["identityCandidate"].AsString),
                source.Import["preview"]["rows"].AsBsonArray.Count(row => row["identityCandidate"] == selected["identityCandidate"]), bank.Debit["importedBy"].AsInt64),
            people.Evidence.Single(person => person.UserId == actor.UserId), people.Evidence.Single(person => person.UserId == payout["recordedBy"].AsInt64),
            people.Evidence.Single(person => person.UserId == bank.Debit["importedBy"].AsInt64));
        var receipts = new BsonArray(result.Receipts.Select(receipt => {
            var original = approval.Receipts.Single(row => row["receiptId"] == receipt.Before.ReceiptId.Value).DeepClone().AsBsonDocument;
            original.Add("version", receipt.After.Version); original.Add("paidBefore", InvoiceBson.Money(receipt.PaidBefore)); original.Add("paidAfter", InvoiceBson.Money(receipt.PaidAfter));
            original.Add("pending", InvoiceBson.Money(receipt.Pending)); original.Add("available", InvoiceBson.Money(receipt.Available)); return original;
        }));
        return new() { { "amount", InvoiceBson.Money(result.Amount) }, { "canonicalIdentity", result.CanonicalIdentity }, { "receipts", receipts }, { "releases", releases } };
    }
}
