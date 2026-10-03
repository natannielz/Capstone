using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementDisbursementBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;
using S = UnitToko.Platform.Procurement.SupplierBankBson;

namespace UnitToko.Platform.Procurement;

internal sealed class ProcurementDisbursementRecords(IMongoDatabase database, MongoUnitOfWork unit, IClock clock, CatalogAuthoringService catalog, ProcurementFundingService funding)
{
    // A historical graph is immutable within this transaction's snapshot. Retain its
    // proof only here; each owner row, receipt, pin and allocation is still checked.
    internal sealed class HistoricalSources(ProcurementDisbursementRecords owner, IClientSessionHandle session, Action current) {
        private readonly long transaction = session.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        private readonly Dictionary<string, (DateTimeOffset From, Action<DateTimeOffset> At)> sources = new();
        private void RequireContext(ProcurementDisbursementRecords caller, IClientSessionHandle tx) {
            if (!ReferenceEquals(owner, caller) || !ReferenceEquals(session, tx) || !tx.IsInTransaction ||
                tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction) throw B.Broken();
            current();
        }
        internal bool Require(ProcurementDisbursementRecords caller, IClientSessionHandle tx, ProcurementDisbursementInput input, BsonDocument source, DateTimeOffset at) {
            RequireContext(caller, tx);
            if (!sources.TryGetValue(B.Hash(source), out var proof) || at < proof.From) return false;
            ProcurementDisbursementProof.RequireLinks(input, source, at); proof.At(at); return true;
        }
        internal void Remember(ProcurementDisbursementRecords caller, IClientSessionHandle tx, BsonDocument source, DateTimeOffset from, Action<DateTimeOffset> at) {
            RequireContext(caller, tx); sources[B.Hash(source)] = (from, at);
        }
    }
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static void Equal(BsonValue a, BsonValue b) { if (!a.Equals(b)) throw B.Broken(); }
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string name, long id, CancellationToken ct) => await Rows(name)
        .Find(tx, new BsonDocument { { "_id", id }, { "$and", new BsonArray { ProcurementDisbursementSchemas.Validator(name) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
    internal static void PolicyAt(BsonDocument policy, DateTimeOffset at) {
        if (!policy["active"].AsBoolean || !policy["reserveUnverified"].AsBoolean || policy["approvedAt"].ToUniversalTime() > at.UtcDateTime || policy["expiresAt"].ToUniversalTime() <= at.UtcDateTime) throw B.Changed("procurement_disbursement_policy_unavailable");
        foreach (var name in new[] { "payoutRecordingProcedureReference", "beneficiaryVerificationProcedureReference", "operatingOwnerReference", "approvalReference" })
            if (!ReceivingAccountPolicy.Text(policy[name].AsString, 256)) throw B.Broken();
    }
    internal static void Review(ProcurementDisbursementInput input, BsonDocument source, BsonDocument capacity, DateTimeOffset at) {
        var policy = source["policy"].AsBsonDocument; PolicyAt(policy, at); Equal(policy["location"], R.Location(input.Location)); Equal(policy["_id"], input.PolicyId); Equal(policy["revision"], input.PolicyRevision);
        Equal(policy["payingAccountId"], input.PayingAccountId); Equal(policy["payingAccountVersionId"], input.PayingAccountVersionId);
        var decision = source["funding"]["decision"]; var approved = Money.Parse(decision["authorizedAmount"].AsString); Equal(capacity["authorizationId"], input.AuthorizationId); Equal(capacity["authorizedAmount"], approved.ToString());
        var recorded = Money.Parse(capacity["recordedAmount"].AsString); var remaining = Money.Parse(capacity["remainingAmount"].AsString);
        if (Money.Add(recorded, remaining) != approved || input.Amount.Value > remaining.Value || !policy["allowPartialRecordings"].AsBoolean && (recorded.Value != 0 || input.Amount != approved) ||
            input.TransferDate < AccountingCalendar.BusinessDate(new(decision["recordedAt"].ToUniversalTime())) || input.TransferDate > AccountingCalendar.BusinessDate(at)) throw B.Changed();
    }
    internal async Task RequireSourceAsync(IClientSessionHandle tx, ProcurementDisbursementInput input, BsonDocument source, DateTimeOffset at, CancellationToken ct, ProcurementDisbursementProof? proof = null, HistoricalSources? history = null) {
        if (at > clock.UtcNow || source.ElementCount != 5) throw B.Broken();
        if (proof is not null && proof.Matches(source)) { proof.Require(this, tx, input, source, at); ct.ThrowIfCancellationRequested(); return; }
        if (history?.Require(this, tx, input, source, at) == true) { ct.ThrowIfCancellationRequested(); return; }
        var actual = await funding.DisbursementSourceAsync(database, tx, input.FundingRequestId, input.FundingDecisionId, input.AuthorizationId, ct); Equal(source["funding"], actual);
        Equal(actual["request"]["location"], R.Location(input.Location)); Equal(actual["request"]["orderSource"]["proposal"]["input"]["supplierId"], input.SupplierId);
        if (actual["decision"]["recordedAt"].ToUniversalTime() > at.UtcDateTime) throw B.Broken();
        var bankRecords = new SupplierBankRecords(database, catalog); var selected = source["beneficiary"]; var version = await bankRecords.RowAsync(tx, S.Versions, input.BeneficiaryVersionId, ct);
        _ = await bankRecords.HeadAsync(tx, version["beneficiaryId"].AsInt64, ct);
        var proposal = await bankRecords.ProposalAsync(tx, version["proposalId"].AsInt64, ct); var decision = await bankRecords.DecisionAsync(tx, version["decisionId"].AsInt64, proposal, ct);
        var observation = await bankRecords.ObservationAsync(tx, version["observationId"].AsInt64, proposal, ct);
        var expected = new BsonDocument { { "beneficiaryId", version["beneficiaryId"] }, { "versionId", version["_id"] }, { "version", version["version"] }, { "versionHash", version["snapshotHash"] },
            { "owner", version["owner"] }, { "supplierId", version["supplierId"] }, { "supplier", version["supplier"] }, { "details", version["details"] }, { "physicalIdentity", version["physicalIdentity"] }, { "policy", version["policy"] },
            { "proposalId", proposal["_id"] }, { "proposalHash", proposal["snapshotHash"] }, { "observationId", observation["_id"] }, { "observationHash", observation["snapshotHash"] },
            { "decisionId", decision["_id"] }, { "decisionHash", decision["snapshotHash"] }, { "effectiveAt", version["effectiveAt"] } };
        Equal(selected, expected); Equal(version["supplierId"], input.SupplierId); Equal(version["version"], input.BeneficiaryVersion);
        Equal(version["owner"], SupplierBson.Owner(new SupplierOwner(input.Location.OrganizationId, input.Location.ShopId, input.Location.ChannelId)));
        var poSupplier = actual["request"]["orderSource"]["proposal"]["input"]; Equal(version["supplier"]["version"]["_id"], poSupplier["supplierVersionId"]); Equal(version["supplier"]["version"]["version"], poSupplier["supplierVersion"]);
        if (version["effectiveAt"].ToUniversalTime() > at.UtcDateTime) throw B.Broken(); SupplierBankRecords.ObservationAt(proposal, observation, at);
        Equal(source["payingBank"]["accountId"], input.PayingAccountId); Equal(source["payingBank"]["versionId"], input.PayingAccountVersionId);
        _ = await BankReceiptService.ReadPayoutBankHistoryAsync(database, tx, source["payingBank"].AsBsonDocument, at, ct);
        var book = source["payingBank"]["book"]; Equal(book["organizationId"], input.Location.OrganizationId); Equal(book["shopId"], input.Location.ShopId); Equal(book["channelId"], input.Location.ChannelId); Equal(book["currency"], "IDR");
        var accounts = new ReceivingAccountRecords(database); var account = await accounts.RowAsync(tx, ReceivingAccountBson.Versions, input.PayingAccountVersionId, ct); var accountProposal = await accounts.ProposalAsync(tx, account["proposalId"].AsInt64, ct);
        Equal(source["payingAccount"], B.AccountView(account, accountProposal["policy"]["bankName"].AsString)); PolicyAt(source["policy"].AsBsonDocument, at);
        // The graph above proves all permanent source timestamps <= at. Later
        // historical instants still have to satisfy the exact observation/policy window.
        history?.Remember(this, tx, source, at, value => SupplierBankRecords.ObservationAt(proposal, observation, value));
    }
    private static void People(BsonDocument row) {
        var source = row["source"]; var approver = source["funding"]["decision"]["actorId"].AsInt64; var people = row["people"].AsBsonArray.Select(ProcurementFundingBson.Person).ToArray();
        if (people.Length != 2 || !people.Select(p => p.UserId).SequenceEqual(new[] { approver, row["actorId"].AsInt64 }.Distinct().Order()) ||
            people.Any(p => p is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 }) || !PersonIndependencePolicy.AreIndependent(people[0], people[1])) throw B.Broken();
        var original = source["funding"]["decision"]["people"].AsBsonArray.Select(ProcurementFundingBson.Person).Single(p => p.UserId == approver);
        if (people.Single(p => p.UserId == approver).PersonId != original.PersonId) throw B.Broken();
    }
    internal async Task<BsonDocument> DraftAsync(IClientSessionHandle tx, long id, CancellationToken ct, ProcurementDisbursementProof? proof = null, HistoricalSources? history = null) {
        var row = await RowAsync(tx, B.Drafts, id, ct); Equal(row["snapshotHash"], B.DraftHash(row)); Equal(row["draftId"], id);
        var input = B.Input(row["input"]); Equal(row["input"], B.Input(input)); Equal(row["authorizationId"], input.AuthorizationId); Equal(row["location"], R.Location(input.Location));
        await RequireSourceAsync(tx, input, row["source"].AsBsonDocument, new(row["recordedAt"].ToUniversalTime()), ct, proof, history); People(row);
        Review(input, row["source"].AsBsonDocument, row["capacity"].AsBsonDocument, new(row["recordedAt"].ToUniversalTime()));
        Equal(row["binding"], B.Binding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, input, row["source"].AsBsonDocument, row["capacity"].AsBsonDocument));
        await EffectsAsync(tx, row, B.DraftBody(input, row["binding"].AsString), false, ct);
        var record = await Rows(B.Records).Find(tx, new BsonDocument("draftId", id)).SingleOrDefaultAsync(ct);
        if (row["recordedDisbursementId"].IsBsonNull != (record is null)) throw B.Broken();
        if (record is not null) { Equal(record["_id"], row["recordedDisbursementId"]); Equal(record["draftHash"], row["snapshotHash"]); }
        else await NoOrphansAsync(tx, "draftId", id, B.RecordOperation, ct);
        return row;
    }
    internal async Task<BsonDocument> RecordAsync(IClientSessionHandle tx, long id, bool pending, CancellationToken ct, ProcurementDisbursementProof? proof = null, HistoricalSources? history = null) {
        var row = await RowAsync(tx, B.Records, id, ct); Equal(row["snapshotHash"], B.Hash(row)); Equal(row["state"], "recorded_unverified");
        // A pin association is checked before the draft's one-record pointer is changed.
        var draft = pending ? await RowAsync(tx, B.Drafts, row["draftId"].AsInt64, ct) : await DraftAsync(tx, row["draftId"].AsInt64, ct, proof, history);
        Equal(draft["snapshotHash"], B.DraftHash(draft)); Equal(row["draftHash"], draft["snapshotHash"]); Equal(row["actorId"], draft["actorId"]);
        foreach (var field in new[] { "location", "authorizationId", "source", "input" }) Equal(row[field], draft[field]);
        if (!pending) Equal(draft["recordedDisbursementId"], id); if (row["recordedAt"].ToUniversalTime() < draft["recordedAt"].ToUniversalTime()) throw B.Broken();
        var input = B.Input(row["input"]); var source = row["source"].AsBsonDocument; var at = new DateTimeOffset(row["recordedAt"].ToUniversalTime());
        await RequireSourceAsync(tx, input, source, at, ct, proof, history); People(row); Review(input, source, row["capacity"].AsBsonDocument, at);
        var reference = PaymentSubmissionBson.Reference(row["evidence"].AsBsonDocument);
        Equal(row["binding"], B.RecordBinding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, row["draftHash"].AsString, row["reservationId"].AsInt64, reference, row["capacity"].AsBsonDocument));
        await EffectsAsync(tx, row, B.RecordBody(row["draftId"].AsInt64, 1, row["reservationId"].AsInt64, reference, row["binding"].AsString), pending, ct);
        if (!pending) {
            var pin = await EvidencePinRecords.ReadAsync(database, tx, row["pinId"].AsInt64, ct); await new EvidenceService(database, unit, new(database), clock).ValidateHistoricalPinAsync(tx, pin, ct);
            foreach (var (field, value) in new (string, BsonValue)[] { ("associationKind", "ProcurementDisbursement"), ("associationId", id), ("procurementDisbursementDraftId", row["draftId"]),
                ("procurementDisbursementDraftRevision", 1L), ("reference", row["evidence"]), ("reservationId", row["reservationId"]), ("actorId", row["actorId"]), ("actorGrantVersion", row["actorGrantVersion"]),
                ("associationHash", row["snapshotHash"]), ("createdAt", row["recordedAt"]), ("reason", row["reason"]), ("correlationId", row["correlationId"]) }) Equal(pin[field], value);
        }
        return row;
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, object body, bool pending, CancellationToken ct) {
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, row["reason"].AsString)); Equal(await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", row["operation"] }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (receipt.ElementCount != 9 || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || receipt["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime() ||
            PersonApprovalReceipt.Read(receipt, Permissions.DisbursementRecord) != new ApprovalRequest(1, 1, ApprovedBy: row["source"]["funding"]["decision"]["actorId"].AsInt64)) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); if (pending) { if (receipt["result"].AsBsonDocument.ElementCount != 0) throw B.Broken(); } else Equal(receipt["result"], B.Result(row));
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.SourceKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        foreach (var field in new[] { "actorId", "requestKey", "location", "authorizationId" }) Equal(effect[field], row[field]); Equal(effect["kind"], row["operation"]); Equal(effect["payload"], B.Result(row)); Equal(effect["createdAt"], row["recordedAt"]);
    }
    internal async Task NoOrphansAsync(IClientSessionHandle tx, string selection, BsonValue value, string operation, CancellationToken ct) {
        foreach (var (table, field, result) in new[] { ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload"), ("idempotencyRecords", "operation", "result") })
            if (await Rows(table).Find(tx, new BsonDocument { { field, operation }, { result + "." + selection, value } }).AnyAsync(ct)) throw B.Broken();
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string operation, string key, CancellationToken ct, ProcurementDisbursementProof? proof = null) {
        var row = await Rows(operation == B.DraftOperation ? B.Drafts : B.Records).Find(tx, new BsonDocument { { "actorId", actor }, { "operation", operation }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is not null) return operation == B.DraftOperation ? await DraftAsync(tx, row["_id"].AsInt64, ct, proof) : await RecordAsync(tx, row["_id"].AsInt64, false, ct, proof);
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            if (await Rows(table).Find(tx, new BsonDocument { { "actorId", actor }, { field, operation }, { table == "idempotencyRecords" ? "key" : "requestKey", key } }).AnyAsync(ct)) throw B.Broken(); return null;
    }
}
