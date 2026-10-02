using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using C = UnitToko.Platform.Billing.PaymentAllocationCorrectionBson;
using R = UnitToko.Platform.Billing.PaymentAllocationRecords;

namespace UnitToko.Platform.Billing;

internal sealed record RefundPayoutApprovedSource(BsonDocument Approval, BsonDocument Request, BsonDocument Invoice,
    BsonDocument Journal, PersonEvidence ApprovedBy, BsonDocument LiabilityAccount,
    Action RequireCurrent, Func<CancellationToken, Task> FenceAsync);

internal sealed record RefundPayoutApprovalSource(BsonDocument Approval, BsonDocument Request, BsonDocument Invoice,
    BsonDocument Journal, BsonDocument InvoiceBalance, BsonDocument? InvoiceHead, BsonArray Receipts,
    IReadOnlyDictionary<long, BsonDocument?> ReceiptHeads, PersonEvidence ApprovedBy, BsonDocument LiabilityAccount,
    Action RequireCurrent, Func<CancellationToken, Task> FenceAsync)
{
    // Financial callers keep the full source's actual money fence when consuming
    // these common fields; this conversion never substitutes an evidence-only fence.
    internal RefundPayoutApprovedSource ApprovedProof => new(Approval.DeepClone().AsBsonDocument, Request.DeepClone().AsBsonDocument,
        Invoice.DeepClone().AsBsonDocument, Journal.DeepClone().AsBsonDocument, ApprovedBy, LiabilityAccount.DeepClone().AsBsonDocument,
        RequireCurrent, FenceAsync);
}

public sealed partial class ReturnFinancialApprovalService
{
    internal IMongoDatabase PayoutSourceDatabase => database;
    internal Task PayoutStorageAsync(CancellationToken ct) => StorageAsync(ct);
    private static BsonDocument PayoutStable(BsonDocument row) {
        var copy = row.DeepClone().AsBsonDocument; copy.Remove("fence"); return copy;
    }
    private AuthorizationQuery[] RequirePayoutRead(ActorContext actor, AuthorizationSnapshot authority, ResourceScope[] resources) =>
        new[] { Permissions.CaseRead, Permissions.InvoiceRead, Permissions.PaymentReviewRead }.Select(permission => {
            var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query);
            if (resources.Length == 0 || resources.Any(scope => !ScopePolicy.IsResourceActive(scope, authority.Directory) || !query.Matches(scope))) throw B.Broken();
            return query;
        }).ToArray();

    // Private evidence belongs to this actual approved refund and issued source. Current
    // monetary heads govern financial recording, not access to its private document.
    internal async Task<RefundPayoutApprovedSource> PayoutEvidenceSourceAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long financialApprovalId, CancellationToken ct) {
        if (actor is null || authority is null || tx is null || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) ||
            actor.UserId != authority.UserId || actor.GrantVersion != authority.GrantVersion || financialApprovalId <= 0) throw B.Broken();
        _ = actor.Validate();
        foreach (var permission in new[] { Permissions.CaseRead, Permissions.InvoiceRead, Permissions.PaymentReviewRead })
            AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, permission, clock.UtcNow));
        // RawApproval validates the real immutable request, original funding, people,
        // journal and command effects; no empty or synthesized financial proof is used.
        var initial = await records.RawApprovalAsync(tx, financialApprovalId, ct);
        if (initial["action"] != "refund" || initial["reservedRefund"].AsDecimal <= 0) throw B.Broken();
        var resources = PaymentDraftBson.Scopes(initial["invoice"]["scopes"].AsBsonArray); _ = RequirePayoutRead(actor, authority, resources);
        var source = await entitlements.ApprovalSourceAsync(actor, authority, tx, initial["caseId"].AsInt64, initial["returnId"].AsInt64, "refund", false, ct, deferReadCaptureFences: true);
        R.Equal(initial["request"], source.Request); R.Equal(initial["invoice"], source.Invoice.MoneySource);
        var journal = await records.Rows("journalEntries").Find(tx, new BsonDocument("_id", initial["journalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var draft = AccountingBson.ReadDraft(journal);
        var liability = draft.Lines.Single(line => line.MappingKey == initial["policy"]["refundLiabilityMapping"].AsString);
        if (liability.Debit.Value != 0 || liability.Credit.Value != initial["reservedRefund"].AsDecimal ||
            liability.AccountId != initial["policy"]["refundLiabilityAccountId"].AsInt64 || liability.AccountCode != initial["policy"]["refundLiabilityAccountCode"].AsString) throw B.Broken();
        var approving = initial["people"].AsBsonArray.Select(ReceivingAccountBson.Person).Single(person => person.UserId == initial["actorId"].AsInt64);
        if (approving.Verification != PersonVerification.Verified || approving.PersonId is null) throw B.Broken();
        var frozenApproval = PayoutStable(initial); var frozenJournal = journal.DeepClone().AsBsonDocument;
        void Current() { _ = RequirePayoutRead(actor, authority, resources); source.RequireCurrent(); ct.ThrowIfCancellationRequested(); }
        async Task Fence(CancellationToken token) {
            Current(); await source.FenceReadAsync(token);
            var live = await records.RawApprovalAsync(tx, financialApprovalId, token); R.Equal(PayoutStable(live), frozenApproval);
            await records.TouchAsync(tx, A.Events, live, token);
            R.Equal(await records.Rows("journalEntries").Find(tx, new BsonDocument("_id", live["journalId"])).SingleOrDefaultAsync(token) ?? throw B.Broken(), frozenJournal);
            var queries = RequirePayoutRead(actor, authority, resources); var clauses = queries.SelectMany(query => query.Clauses.Where(clause => resources.Any(clause.Matches))).ToArray();
            await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
                clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory))).Distinct(), [],
                authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, token);
            if (actor.Session is { } session) {
                var liveSession = await records.Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(token);
                if (liveSession is null || liveSession["idleExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime || liveSession["absoluteExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime) throw new AuthorizationDeniedException("stale_or_inactive_session");
            }
            Current(); token.ThrowIfCancellationRequested();
        }
        // The Documents target must finish this real source fence before returning or
        // committing. Financial preview/record/recovery require PayoutSourceAsync instead.
        Current(); return new(frozenApproval.DeepClone().AsBsonDocument, source.Request.DeepClone().AsBsonDocument,
            source.Invoice.MoneySource.DeepClone().AsBsonDocument, frozenJournal.DeepClone().AsBsonDocument, approving,
            new() { { "mappingKey", liability.MappingKey }, { "accountId", liability.AccountId }, { "accountCode", liability.AccountCode } }, Current, Fence);
    }

    internal async Task<RefundPayoutApprovalSource> PayoutSourceAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long financialApprovalId, CancellationToken ct) {
        if (actor is null || authority is null || tx is null || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) ||
            actor.UserId != authority.UserId || actor.GrantVersion != authority.GrantVersion || financialApprovalId <= 0) throw B.Broken();
        _ = actor.Validate();
        foreach (var permission in new[] { Permissions.CaseRead, Permissions.InvoiceRead, Permissions.PaymentReviewRead })
            AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, permission, clock.UtcNow));
        var initial = await records.RawApprovalAsync(tx, financialApprovalId, ct);
        if (initial["action"] != "refund" || initial["reservedRefund"].AsDecimal <= 0) throw B.Broken();
        var resources = PaymentDraftBson.Scopes(initial["invoice"]["scopes"].AsBsonArray); _ = RequirePayoutRead(actor, authority, resources);
        var source = await entitlements.ApprovalSourceAsync(actor, authority, tx, initial["caseId"].AsInt64, initial["returnId"].AsInt64, "refund", false, ct, deferReadCaptureFences: true);
        R.Equal(initial["request"], source.Request); R.Equal(initial["invoice"], source.Invoice.MoneySource);
        var selected = initial["links"].AsBsonArray.Select(link => link["originalAllocationId"].AsInt64).ToArray();
        var money = await allocations.FinancialSourcesAsync(actor, authority, tx, source.Invoice.MoneySource, selected, source, ct);
        var journal = await records.Rows("journalEntries").Find(tx, new BsonDocument("_id", initial["journalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var draft = AccountingBson.ReadDraft(journal);
        var liability = draft.Lines.Single(line => line.MappingKey == initial["policy"]["refundLiabilityMapping"].AsString);
        if (liability.Debit.Value != 0 || liability.Credit.Value != initial["reservedRefund"].AsDecimal ||
            liability.AccountId != initial["policy"]["refundLiabilityAccountId"].AsInt64 || liability.AccountCode != initial["policy"]["refundLiabilityAccountCode"].AsString) throw B.Broken();
        var approving = initial["people"].AsBsonArray.Select(ReceivingAccountBson.Person).Single(person => person.UserId == initial["actorId"].AsInt64);
        if (approving.Verification != PersonVerification.Verified || approving.PersonId is null) throw B.Broken();
        var frozenApproval = PayoutStable(initial); var frozenJournal = journal.DeepClone().AsBsonDocument;
        var frozenBalance = money.InvoiceBalance.DeepClone().AsBsonDocument; var frozenHead = money.InvoiceHead is null ? null : PayoutStable(money.InvoiceHead);
        var frozenReceipts = money.Receipts.DeepClone().AsBsonArray;
        var frozenHeads = money.ReceiptHeads.ToDictionary(pair => pair.Key, pair => pair.Value is null ? null : PayoutStable(pair.Value));
        void Current() { _ = RequirePayoutRead(actor, authority, resources); source.RequireCurrent(); money.RequireCurrent(); ct.ThrowIfCancellationRequested(); }
        async Task Fence(CancellationToken token) {
            // The same-transaction money source registers this actual request/pool/return/
            // issued-invoice full source fence once; do not execute that graph again here.
            Current(); await money.FenceAsync(token);
            var live = await records.RawApprovalAsync(tx, financialApprovalId, token); R.Equal(PayoutStable(live), frozenApproval);
            await records.TouchAsync(tx, A.Events, live, token);
            await allocations.RecheckFinancialBalancesAsync(actor, authority, tx, source.Invoice.MoneySource, money, token);
            R.Equal(await records.Rows("journalEntries").Find(tx, new BsonDocument("_id", live["journalId"])).SingleOrDefaultAsync(token) ?? throw B.Broken(), frozenJournal);
            var queries = RequirePayoutRead(actor, authority, resources); var clauses = queries.SelectMany(query => query.Clauses.Where(clause => resources.Any(clause.Matches))).ToArray();
            await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
                clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory))).Distinct(), [],
                authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, token);
            if (actor.Session is { } session) {
                var liveSession = await records.Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(token);
                if (liveSession is null || liveSession["idleExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime || liveSession["absoluteExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime) throw new AuthorizationDeniedException("stale_or_inactive_session");
            }
            Current(); token.ThrowIfCancellationRequested();
        }
        // This same-transaction capture does not complete admission. Every successful caller
        // must invoke the returned full FenceAsync before returning or committing its phase.
        Current();
        return new(frozenApproval.DeepClone().AsBsonDocument, source.Request.DeepClone().AsBsonDocument, source.Invoice.MoneySource.DeepClone().AsBsonDocument,
            frozenJournal.DeepClone().AsBsonDocument, frozenBalance.DeepClone().AsBsonDocument, frozenHead?.DeepClone().AsBsonDocument,
            frozenReceipts.DeepClone().AsBsonArray, frozenHeads.ToDictionary(pair => pair.Key, pair => pair.Value?.DeepClone().AsBsonDocument), approving,
            new() { { "mappingKey", liability.MappingKey }, { "accountId", liability.AccountId }, { "accountCode", liability.AccountCode } }, Current, Fence);
    }

    // Migration validation uses real immutable proofs, never a synthetic actor or current staff grants.
    internal static async Task PreflightPayoutApprovalsAsync(IMongoDatabase database, IClock clock, MigrationLease lease, CancellationToken ct) {
        await ReturnFinancialApprovalMigration.PreflightRequestsAsync(database, clock, lease, ct);
        using var tx = await database.Client.StartSessionAsync(cancellationToken: ct);
        tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        await PaymentAllocationStorage.RequireAsync(database, ct); await PaymentAllocationStorage.RequireFinancialAsync(database, ct);
        var reader = new R(database, clock); var sources = new HashSet<long>(); var receipts = new HashSet<long>(); var invoices = new HashSet<long>(); var maximum = 0L;
        var counts = new Dictionary<string, long> { [B.Operation] = 0, [C.Operation] = 0, [A.Operation] = 0 };
        async Task Validate(BsonDocument row) {
            if (!sources.Add(row["_id"].AsInt64)) return;
            if (row["operation"] == A.Operation) {
                _ = await reader.RawApprovalAsync(tx, row["_id"].AsInt64, ct);
                foreach (var link in row["links"].AsBsonArray) await reader.PreflightSourceAsync(tx, await reader.RawAsync(tx, link["originalAllocationId"].AsInt64, ct), ct);
            } else if (row["operation"] == B.Operation || row["operation"] == C.Operation) await reader.PreflightSourceAsync(tx, row, ct);
            else throw B.Broken();
        }
        foreach (var table in new[] { B.Events, C.Events, A.Events }) {
            using var cursor = await reader.Rows(table).Find(tx, new BsonDocument()).ToCursorAsync(ct);
            while (await cursor.MoveNextAsync(ct)) foreach (var candidate in cursor.Current) {
                await lease.RenewAsync(ct);
                var row = table == A.Events ? await reader.RawApprovalAsync(tx, candidate["_id"].AsInt64, ct) : await reader.RawEventAsync(tx, table == C.Events, candidate["_id"].AsInt64, ct);
                maximum = Math.Max(maximum, row["_id"].AsInt64); counts[row["operation"].AsString]++; await Validate(row);
                if (table == A.Events) {
                    foreach (var receipt in row["receipts"].AsBsonArray) if (receipts.Add(receipt["receiptId"].AsInt64))
                        _ = await reader.BalanceAsync(tx, true, receipt["receiptId"].AsInt64, row["party"].AsBsonDocument, receipt["receiptTotal"], receipt["receiptHash"], Validate, ct);
                    if (invoices.Add(row["invoiceId"].AsInt64)) _ = await reader.BalanceAsync(tx, false, row["invoiceId"].AsInt64, row["party"].AsBsonDocument, row["invoice"]["total"], row["invoice"]["snapshotHash"], Validate, ct);
                } else {
                    if (receipts.Add(row["receiptId"].AsInt64)) _ = await reader.BalanceAsync(tx, true, row["receiptId"].AsInt64, row["party"].AsBsonDocument, row["receiptTotal"], row["receiptHash"], Validate, ct);
                    foreach (var line in row["lines"].AsBsonArray) if (invoices.Add(line["invoiceId"].AsInt64))
                        _ = await reader.BalanceAsync(tx, false, line["invoiceId"].AsInt64, row["party"].AsBsonDocument, line["source"]["total"], line["source"]["snapshotHash"], Validate, ct);
                }
            }
        }
        if (await reader.Rows(B.Receipts).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct) != receipts.Count ||
            await reader.Rows(B.Invoices).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct) != invoices.Count) throw B.Broken();
        foreach (var pair in counts) foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind") })
            if (await reader.Rows(table).CountDocumentsAsync(tx, new BsonDocument(field, pair.Key), cancellationToken: ct) != pair.Value) throw B.Broken();
        foreach (var pair in new[] { ("billing.payment-allocation", counts[B.Operation] + counts[C.Operation]), ("billing.return-financial", counts[A.Operation]) })
            if (await reader.Rows("journalEntries").CountDocumentsAsync(tx, new BsonDocument("source.namespace", pair.Item1), cancellationToken: ct) != pair.Item2) throw B.Broken();
        var counter = await reader.Rows("idCounters").Find(tx, new BsonDocument("_id", B.Events)).SingleOrDefaultAsync(ct);
        if (counter is null ? maximum > 0 : !counter["value"].IsInt64 || counter["value"].AsInt64 < maximum || counter["value"].AsInt64 is < 0 or >= long.MaxValue - 1) throw B.Broken();
        await tx.AbortTransactionAsync(ct);
    }
}
