using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.RefundPayoutBson;
using R = UnitToko.Platform.Billing.RefundPayoutRecords;

namespace UnitToko.Platform.Billing;

public sealed record RefundPayoutDraftPreview(BsonDocument View, string Binding);
public sealed partial class RefundPayoutService
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly IdAllocator ids;
    private readonly AuthorizationStore authorization;
    private readonly ReturnFinancialApprovalService approvals;
    private readonly BankReceiptService banks;
    private readonly R records;
    private readonly AfterSalesCaseService cases;
    internal Action<string, double>? PhaseObserver { get; set; }
    private void ObservePhase(string phase, long started) => PhaseObserver?.Invoke(phase, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    public RefundPayoutService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        ReturnFinancialApprovalService approvals, BankReceiptService banks, AfterSalesCaseService cases) {
        if (!ReferenceEquals(approvals.PayoutSourceDatabase.Client, database.Client) || !approvals.PayoutSourceDatabase.DatabaseNamespace.Equals(database.DatabaseNamespace) ||
            !ReferenceEquals(banks.PayoutBankDatabase.Client, database.Client) || !banks.PayoutBankDatabase.DatabaseNamespace.Equals(database.DatabaseNamespace) ||
            !ReferenceEquals(cases.FinancialSourceDatabase.Client, database.Client) || !cases.FinancialSourceDatabase.DatabaseNamespace.Equals(database.DatabaseNamespace))
            throw new ArgumentException("Payout sources must join the owning transaction");
        this.database = database; this.clock = clock; this.ids = ids; this.approvals = approvals; this.banks = banks; this.cases = cases;
        authorization = new(database, unit, clock); records = new(database, clock, unit);
    }
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Frame(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, RefundPayoutApprovedSource approval,
        RefundPayoutBankSource bank, BsonDocument policy, BsonDocument beneficiary, BsonDocument source, RefundPayoutInput input, PersonAuthorityProof people) {
        internal ActorContext Actor = actor;
        internal AuthorizationSnapshot Authority = authority;
        internal IClientSessionHandle Tx = tx;
        internal RefundPayoutApprovedSource Approval = approval;
        internal RefundPayoutBankSource Bank = bank;
        internal BsonDocument Policy = policy, Beneficiary = beneficiary, Source = source;
        internal RefundPayoutInput Input = input;
        internal PersonAuthorityProof People = people;
        internal ResourceScope[] Resources => PaymentDraftBson.Scopes(Approval.Invoice["scopes"].AsBsonArray);
    }
    internal async Task StorageAsync(CancellationToken ct) {
        var started = Stopwatch.GetTimestamp();
        await approvals.PayoutStorageAsync(ct); await banks.PayoutBankStorageAsync(ct);
        foreach (var name in RefundPayoutSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, RefundPayoutSchemas.Validator(name), RefundPayoutSchemas.Keys(name), ct);
        ObservePhase("storage", started);
    }
    private AuthorizationQuery[] Require(Frame frame) {
        if (frame.Actor.Assurance is not (FactorAssurance.MultiFactor or FactorAssurance.PhishingResistant) || frame.Actor.FactorCompletedAt > clock.UtcNow ||
            clock.UtcNow - frame.Actor.FactorCompletedAt > TimeSpan.FromMinutes(5)) throw new AuthorizationDeniedException("step_up_required");
        return new[] { Permissions.DisbursementRecord, Permissions.CaseRead, Permissions.InvoiceRead, Permissions.PaymentReviewRead }.Select(permission => {
            var query = ScopePolicy.Compile(frame.Actor, frame.Authority, permission, clock.UtcNow); AuthorizationStore.Require(query);
            if (frame.Resources.Length == 0 || frame.Resources.Any(resource => !ScopePolicy.IsResourceActive(resource, frame.Authority.Directory) || !query.Matches(resource))) throw B.Broken();
            return query;
        }).ToArray();
    }
    private void Current(Frame frame) {
        _ = Require(frame); frame.Approval.RequireCurrent(); frame.Bank.RequireCurrent(); frame.People.RequireCurrent(frame.Tx, clock.UtcNow);
        B.PolicyAt(frame.Policy, Now); B.BeneficiaryAt(frame.Beneficiary, Now);
        _ = B.Review(frame.Source, frame.Input, AccountingCalendar.BusinessDate(clock.UtcNow));
    }
    private async Task FenceAsync(Frame frame, CancellationToken ct) {
        var started = Stopwatch.GetTimestamp();
        await frame.Approval.FenceAsync(ct); ObservePhase("approval-fence", started);
        started = Stopwatch.GetTimestamp();
        await frame.Bank.FenceAsync(ct); ObservePhase("bank-fence", started);
        started = Stopwatch.GetTimestamp();
        await records.TouchAsync(frame.Tx, B.Policies, frame.Policy, ct); await records.TouchAsync(frame.Tx, B.Beneficiaries, frame.Beneficiary, ct);
        var queries = Require(frame); var resources = frame.Resources;
        var clauses = queries.SelectMany(query => query.Clauses.Where(clause => resources.Any(clause.Matches))).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(frame.Actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, frame.Authority.Directory))).Distinct(), [],
            frame.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), frame.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), frame.Tx, ct);
        if (frame.Actor.Session is { } session) {
            var live = await records.Rows("identitySessions").Find(frame.Tx, SessionPersistence.LiveFilter(session, frame.Actor.UserId, frame.Actor.ProviderIssuer, frame.Actor.ProviderSubject, frame.Actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct);
            if (live is null || live["idleExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime || live["absoluteExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime) throw new AuthorizationDeniedException("stale_or_inactive_session");
        }
        Current(frame); ct.ThrowIfCancellationRequested();
        ObservePhase("policy-person-authority-fence", started);
    }
    private static BsonDocument ApprovalSnapshot(RefundPayoutApprovedSource source) {
        return B.ApprovalSnapshot(source.Approval, source.LiabilityAccount);
    }
    private static BsonDocument BankSnapshot(RefundPayoutBankSource source) => new() {
        { "accountId", source.Account["accountId"] }, { "versionId", source.Account["_id"] }, { "headHash", BankReceiptBson.Hash(source.AccountHead) }, { "versionHash", source.Account["snapshotHash"] },
        { "physicalIdentity", source.Account["physicalIdentity"] }, { "identityNamespace", source.IdentityNamespace }, { "anchorHash", source.Anchor["snapshotHash"] },
        { "firstReceiptId", source.FirstReceipt["_id"] }, { "firstReceiptHash", source.FirstReceipt["snapshotHash"] }, { "cashJournalId", source.CashJournal["_id"] }, { "cashJournalFingerprint", source.CashJournal["fingerprint"] },
        { "scope", source.Scope.DeepClone() }, { "book", source.Book.DeepClone() }, { "cashAccountId", source.CashAccount["accountId"] }, { "cashAccountCode", source.CashAccount["accountCode"] }, { "currency", source.Account["details"]["currency"] } };
    private async Task<Frame> PrepareAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long approvalId, RefundPayoutInput input, CancellationToken ct, RefundPayoutApprovedSource? knownApproval = null) {
        input = RefundPayoutPolicy.Copy(input);
        var started = Stopwatch.GetTimestamp();
        var sourceApproval = knownApproval ?? (await approvals.PayoutSourceAsync(actor, authority, tx, approvalId, ct)).ApprovedProof;
        ObservePhase("approval-capture", started); started = Stopwatch.GetTimestamp();
        var sourceBank = await banks.PayoutBankSourceAsync(actor, authority, tx, input.PayingAccountId, input.PayingAccountVersionId, ct);
        ObservePhase("bank-capture", started); started = Stopwatch.GetTimestamp();
        var party = sourceApproval.Approval["party"].AsBsonDocument;
        var scope = new BsonDocument { { "fulfillmentOrganizationId", party["fulfillmentOrganizationId"] }, { "shopId", party["shopId"] }, { "channelId", party["channelId"] }, { "warehouseId", BsonNull.Value } };
        var policy = await records.Rows(B.Policies).Find(tx, new BsonDocument("$and", new BsonArray { new BsonDocument("scope", scope), RefundPayoutSchemas.Validator(B.Policies) })).SingleOrDefaultAsync(ct) ?? throw B.Changed("refund_payout_policy_unavailable");
        var beneficiary = await records.RowAsync(tx, B.Beneficiaries, input.BeneficiaryId, ct);
        R.Equal(beneficiary["scope"], scope); R.Equal(sourceBank.Scope, scope); B.PolicyAt(policy, Now); B.BeneficiaryAt(beneficiary, Now);
        var source = new BsonDocument { { "approval", ApprovalSnapshot(sourceApproval) }, { "beneficiary", B.BeneficiarySnapshot(beneficiary) }, { "payingBank", BankSnapshot(sourceBank) }, { "policy", B.Snapshot(policy) } };
        var users = new[] { actor.UserId, sourceApproval.ApprovedBy.UserId }.Distinct().Order().ToArray();
        var people = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, [.. users], ct);
        if (!PersonIndependencePolicy.AreIndependent(people.Evidence.Single(p => p.UserId == actor.UserId), people.Evidence.Single(p => p.UserId == sourceApproval.ApprovedBy.UserId))) throw new AuthorizationDeniedException("independent_person_required");
        R.Equal(people.Evidence.Single(p => p.UserId == sourceApproval.ApprovedBy.UserId).PersonId ?? throw B.Broken(), sourceApproval.ApprovedBy.PersonId ?? throw B.Broken());
        await new PersonAuthorizationFence(database, clock).TouchAsync(tx, people, ct);
        var frame = new Frame(actor, authority, tx, sourceApproval, sourceBank, policy, beneficiary, source, input, people); Current(frame);
        ObservePhase("policy-person-authority-capture", started); return frame;
    }
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (metadata.IdempotencyKey.Length > 128 || metadata.IdempotencyKey.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.' or ':'))) throw RefundPayoutPolicy.Invalid();
    }
    private CommandAuthorization Command(Frame frame) {
        var resources = frame.Resources;
        var approval = new ApprovalRequest(1, 1, ApprovedBy: frame.Approval.ApprovedBy.UserId);
        return new(ScopePolicy.Authorize(frame.Actor, frame.Authority, Permissions.DisbursementRecord, resources[0], clock.UtcNow, approval),
            [.. resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, frame.Authority.Directory)).Distinct()], Permissions.DisbursementRecord,
            new(resources[0], approval));
    }
    private async Task<BsonDocument?> SavedAsync(Frame frame, CancellationToken ct) {
        var row = await records.Rows(B.Payouts).Find(frame.Tx, new BsonDocument("financialApprovalId", frame.Approval.Approval["_id"])).SingleOrDefaultAsync(ct);
        if (row is not null) return await records.PayoutAsync(frame.Tx, row["_id"].AsInt64, false, ct);
        foreach (var (table, kind, field) in new[] { ("idempotencyRecords", "operation", "result.approvalId"), ("auditEvents", "kind", "result.approvalId"), ("outboxEvents", "kind", "payload.approvalId") })
            if (await records.Rows(table).Find(frame.Tx, new BsonDocument { { kind, B.Operation }, { field, frame.Approval.Approval["_id"] } }).AnyAsync(ct)) throw B.Broken();
        return null;
    }
    private static BsonDocument View(Frame frame, BsonDocument? saved = null) => new() { { "approvalId", frame.Approval.Approval["_id"] }, { "caseId", frame.Approval.Approval["caseId"] },
        { "returnId", frame.Approval.Approval["returnId"] }, { "invoiceId", frame.Approval.Approval["invoiceId"] }, { "amount", frame.Approval.Approval["reservedRefund"] },
        { "beneficiaryId", frame.Beneficiary["_id"] }, { "beneficiaryRevision", frame.Beneficiary["revision"] }, { "beneficiaryName", frame.Beneficiary["details"]["holderName"] },
        { "beneficiaryBankCode", frame.Beneficiary["details"]["bankCode"] }, { "beneficiaryAccountSuffix", frame.Beneficiary["details"]["accountNumber"].AsString[^4..] },
        { "payingAccountId", frame.Bank.Account["accountId"] }, { "payingAccountVersionId", frame.Bank.Account["_id"] }, { "payingBankCode", frame.Bank.Account["details"]["bankCode"] },
        { "payingAccountSuffix", frame.Bank.Account["details"]["accountNumber"].AsString[^4..] }, { "payoutRecordingProcedureReference", frame.Policy["payoutRecordingProcedureReference"] },
        { "beneficiaryVerificationProcedureReference", frame.Policy["beneficiaryVerificationProcedureReference"] }, { "input", B.Input(frame.Input) }, { "payout", saved is null ? BsonNull.Value : B.Result(saved) }, { "settlement", BsonNull.Value } };
    public async Task<RefundPayoutDraftPreview> PreviewDraftFromTransferAsync(ActorContext actor, long approvalId, DateOnly transferDate, string transferReference, CancellationToken ct = default) {
        if (approvalId <= 0 || transferDate.Year < 2000 || !ReceivingAccountPolicy.Text(transferReference, 160)) throw RefundPayoutPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var approval = await approvals.PayoutSourceAsync(actor, authority, tx, approvalId, token);
            var party = approval.Approval["party"];
            var scope = new BsonDocument { { "fulfillmentOrganizationId", party["fulfillmentOrganizationId"] }, { "shopId", party["shopId"] }, { "channelId", party["channelId"] }, { "warehouseId", BsonNull.Value } };
            var policy = await records.Rows(B.Policies).Find(tx, new BsonDocument("$and", new BsonArray { new BsonDocument("scope", scope), RefundPayoutSchemas.Validator(B.Policies) })).SingleOrDefaultAsync(token) ?? throw B.Changed("refund_payout_policy_unavailable");
            var beneficiary = await records.Rows(B.Beneficiaries).Find(tx, new BsonDocument("$and", new BsonArray { new BsonDocument { { "scope", scope }, { "buyingOrganizationId", party["buyingOrganizationId"] } }, RefundPayoutSchemas.Validator(B.Beneficiaries) })).SingleOrDefaultAsync(token) ?? throw B.Changed("refund_beneficiary_unavailable");
            var input = new RefundPayoutInput(beneficiary["_id"].AsInt64, beneficiary["revision"].AsInt64, policy["payingAccountId"].AsInt64, policy["payingAccountVersionId"].AsInt64, InvoiceBson.Money(approval.Approval["reservedRefund"]), transferDate, transferReference);
            var frame = await PrepareAsync(actor, authority, tx, approvalId, input, token, approval.ApprovedProof);
            if (await SavedAsync(frame, token) is not null) throw B.Changed("refund_payout_already_recorded");
            await FenceAsync(frame, token); return new RefundPayoutDraftPreview(View(frame), B.Binding(frame.Source, frame.Input));
        }, ct);
    }
    public async Task<RefundPayoutDraftPreview> PreviewDraftAsync(ActorContext actor, long approvalId, RefundPayoutInput input, CancellationToken ct = default) {
        if (approvalId <= 0) throw RefundPayoutPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var frame = await PrepareAsync(actor, authority, tx, approvalId, input, token);
            if (await SavedAsync(frame, token) is not null) throw B.Changed("refund_payout_already_recorded");
            await FenceAsync(frame, token); return new RefundPayoutDraftPreview(View(frame), B.Binding(frame.Source, frame.Input));
        }, ct);
    }
    public async Task<BsonDocument> CreateDraftAsync(ActorContext actor, long approvalId, string binding, RefundPayoutInput input, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        Metadata(metadata); input = RefundPayoutPolicy.Copy(input);
        if (approvalId <= 0 || binding is null || binding.Length != 64) throw RefundPayoutPolicy.Invalid(); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Drafts, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        Frame? frame = null; BsonDocument? saved = null;
        var body = B.DraftBody(approvalId, binding, input); var bodyHash = AuthorizationStore.CommandHash(body, metadata.Reason);
        return await authorization.ExecuteCommandAsync(actor, B.DraftOperation, metadata, body, async (authority, tx, token) => {
            frame = await PrepareAsync(actor, authority, tx, approvalId, input, token);
            saved = await records.ForKeyAsync(tx, actor.UserId, B.DraftOperation, metadata.IdempotencyKey, token);
            return Command(frame);
        }, async (tx, token) => {
            if (saved is not null) throw B.Broken(); var current = frame!;
            R.Equal(binding, B.Binding(current.Source, current.Input)); if (await SavedAsync(current, token) is not null) throw B.Changed("refund_payout_already_recorded");
            var now = Now; var sourceApproval = current.Source["approval"];
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "operation", B.DraftOperation }, { "revision", 1L }, { "financialApprovalId", approvalId },
                { "approvalHash", sourceApproval["hash"] }, { "caseId", sourceApproval["caseId"] }, { "returnId", sourceApproval["returnId"] }, { "invoiceId", sourceApproval["invoiceId"] },
                { "party", sourceApproval["party"] }, { "scope", current.Policy["scope"] }, { "scopes", current.Approval.Invoice["scopes"] }, { "source", current.Source }, { "input", B.Input(current.Input) },
                { "binding", binding }, { "userId", actor.UserId }, { "personId", current.People.Evidence.Single(p => p.UserId == actor.UserId).PersonId ?? throw B.Broken() },
                { "people", new BsonArray(current.People.Evidence.OrderBy(p => p.UserId).Select(ReceivingAccountBson.Person)) }, { "createdAt", now.UtcDateTime }, { "recordedPayoutId", BsonNull.Value },
                { "fence", 0L }, { "actorGrantVersion", actor.GrantVersion }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", bodyHash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId } };
            row.Add("snapshotHash", B.DraftHash(row));
            await records.Rows(B.Drafts).InsertOneAsync(tx, row, cancellationToken: token); await EffectsAsync(tx, row, B.DraftResult(row), B.DraftAudit(row), now, actor.UserId, token);
            return B.DraftResult(row);
        }, ct, async (_, tx, token) => {
            var row = await records.ForKeyAsync(tx, actor.UserId, B.DraftOperation, metadata.IdempotencyKey, token) ?? throw B.Broken();
            R.Equal(row["bodyHash"], bodyHash); R.Equal(row["source"], frame!.Source); await records.TouchAsync(tx, B.Drafts, row, token); await FenceAsync(frame, token);
        }, validateCurrent: () => Current(frame!));
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, BsonDocument result, BsonDocument audit, DateTimeOffset now, long actor, CancellationToken ct) {
        await records.Rows("auditEvents").InsertOneAsync(tx, audit, cancellationToken: ct);
        await records.Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", row["operation"] },
            { "sourceKey", B.SourceKey(row) }, { "actorId", actor }, { "requestKey", row["requestKey"] }, { "payload", result }, { "createdAt", now.UtcDateTime }, { "dueAt", now.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
    public async Task<BsonDocument?> RecoverDraftAsync(ActorContext actor, long approvalId, string key, CancellationToken ct = default) {
        if (approvalId <= 0 || !ReceivingAccountPolicy.Text(key, 128) || key.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.' or ':'))) throw RefundPayoutPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var row = await records.ForKeyAsync(tx, actor.UserId, B.DraftOperation, key, token);
            if (row is null) return null; R.Equal(row["financialApprovalId"], approvalId);
            var frame = await PrepareAsync(actor, authority, tx, approvalId, B.Input(row["input"].AsBsonDocument), token);
            R.Equal(row["source"], frame.Source); await records.TouchAsync(tx, B.Drafts, row, token); await FenceAsync(frame, token); return B.DraftResult(row);
        }, ct);
    }
}
