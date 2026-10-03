using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementSupplierPayableService
{
    private async Task WriteEffectsAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct)
    {
        await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, B.Outbox(row), cancellationToken: ct);
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct)
    {
        Equal(await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", row["actorId"]) & F.Eq("operation", row["operation"]) &
            F.Eq("key", row["requestKey"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (receipt.ElementCount < 8 || receipt["schemaVersion"] != 1 || !receipt["_id"].IsObjectId ||
            receipt["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row));
        if (row["operation"] == B.DecideOperation)
            Equal(receipt[PersonApprovalReceipt.Field], PersonApprovalReceipt.Create(Permissions.ProcurementPayableDecide,
                new ApprovalRequest(1, 1, RequestedBy: row["proposalMakerId"].AsInt64)));
        var effect = await Rows("outboxEvents").Find(tx, F.Eq("sourceKey", B.SourceKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        foreach (var field in new[] { "kind", "actorId", "requestKey", "location", "payload", "createdAt" })
            Equal(effect[field], B.Outbox(row)[field]);
        if (effect["schemaVersion"] != 1 || effect["_id"].IsBsonNull || effect["dueAt"].ToUniversalTime() < effect["createdAt"].ToUniversalTime() ||
            effect["attempts"].AsInt32 < 0 || effect["status"].AsString is not ("pending" or "processing" or "delivered" or "failed")) throw B.Broken();
    }
    private async Task PostedAsync(IClientSessionHandle tx, BsonDocument decision, CancellationToken ct)
    {
        if (decision["postingFence"] != 1L || decision["journalId"].IsBsonNull || decision["journalFingerprint"].IsBsonNull) throw B.Broken();
        var journal = await Rows("journalEntries").Find(tx, F.Eq("_id", decision["journalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var draft = AccountingBson.ReadDraft(journal); var snapshot = ProcurementSupplierPayablePostingSource.Snapshot(decision);
        ProcurementSupplierPayablePostingSource.RequireAccounts(decision, draft);
        if (draft.Fingerprint != decision["journalFingerprint"].AsString || draft.Source != snapshot.Source || draft.SourceRevision != 1 ||
            draft.Book != snapshot.Book || draft.Dimensions != snapshot.Dimensions || draft.Date != snapshot.Date ||
            !draft.Lines.Select(line => new PostingLine(line.MappingKey, line.Debit, line.Credit)).SequenceEqual(snapshot.Lines) ||
            journal["bookId"] != decision["accounting"]["bookId"] ||
            await Rows("journalEntries").CountDocumentsAsync(tx, F.Eq("sourceEvent", draft.Source.EventKey), cancellationToken: ct) != 1)
            throw B.Broken();
        foreach (var field in new[] { "actorId", "actorGrantVersion", "reason", "correlationId" }) Equal(journal[field], decision[field]);
        var eventKey = "accounting:journal:" + decision["journalId"].AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var audit = await Rows("accountingAuditEvents").Find(tx, F.Eq("eventKey", eventKey)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        foreach (var (field, expected) in new (string, BsonValue)[] { ("kind", "journal.posted"), ("bookId", journal["bookId"]),
            ("journalId", decision["journalId"]), ("fingerprint", decision["journalFingerprint"]), ("actorId", decision["actorId"]),
            ("actorGrantVersion", decision["actorGrantVersion"]), ("reason", decision["reason"]), ("correlationId", decision["correlationId"]) })
            Equal(audit[field], expected);
        var outbox = await Rows("outboxEvents").Find(tx, F.Eq("sourceKey", eventKey)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(outbox["kind"], "journal.posted"); Equal(outbox["payload"]["journalId"], decision["journalId"]);
    }
    private async Task<BsonDocument> ProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct) =>
        (await ProposalWithProofAsync(tx, id, ct)).Proposal;
    private async Task<(BsonDocument Proposal, HistoricalProof Proof)> ProposalWithProofAsync(IClientSessionHandle tx, long id, CancellationToken ct)
    {
        var row = await Rows(B.Proposals).Find(tx, F.Eq("_id", id) & new BsonDocument("$and", new BsonArray { ProcurementSupplierPayableSchemas.Validator(B.Proposals) }))
            .SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(row["snapshotHash"], B.Hash(row));
        var input = B.Input(row["input"]); Equal(row["input"], B.Input(input)); Equal(row["location"], R.Location(input.Location));
        Equal(row["policy"]["_id"], input.PolicyId); Equal(row["policy"]["revision"], input.PolicyRevision);
        Equal(row["binding"], B.Binding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64,
            row["input"].AsBsonDocument, row["source"].AsBsonDocument, row["policy"].AsBsonDocument,
            row["accounting"].AsBsonDocument, row["capacity"].AsBsonDocument));
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(B.ProposeBody(input, row["binding"].AsString), row["reason"].AsString));
        HistoryPeople(row, null);
        var proof = await HistoricalSourceAsync(tx, input, ct);
        Equal(row["source"], proof.Facts);
        await EffectsAsync(tx, row, ct); return (row, proof);
    }
    private async Task<HistoricalProof> HistoricalSourceAsync(IClientSessionHandle tx, ProcurementSupplierPayableInput input, CancellationToken ct)
    {
        if (input.Kind == ProcurementSupplierPayableKind.Recognize)
        {
            var source = await valuations.ReadPayableSourceAsync(database, tx, input.ValuationAllocationId!.Value, ct);
            return Proof(tx, B.Input(input), RecognizeFacts(source), valuation: source);
        }
        var payable = await PayableRootAsync(tx, input.PayableId!.Value, ct);
        var advance = await settlements.ReadPayableSourceAsync(database, tx, input.SettlementId!.Value, ct);
        return Proof(tx, B.Input(input), ApplyFacts(payable, advance, input.Amount!.Value, input.PostingDate), payable: payable, advance: advance);
    }
    private async Task<BsonDocument?> DecisionAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct,
        HistoricalProof? proof = null)
    {
        proof?.Require(tx, B.Input(proposal["input"]));
        if (proof is not null) Equal(proposal["source"], proof.Facts);
        var rows = await Rows(B.Decisions).Find(tx, F.Eq("proposalId", proposal["_id"])).Limit(2).ToListAsync(ct);
        if (rows.Count > 1) throw B.Broken(); if (rows.Count == 0) return null;
        var row = rows[0]; Equal(row["snapshotHash"], B.Hash(row)); Equal(row["proposalHash"], proposal["snapshotHash"]);
        Equal(row["proposalMakerId"], proposal["actorId"]); HistoryPeople(proposal, row);
        foreach (var field in new[] { "location", "input", "source", "policy", "accounting", "amount" }) Equal(row[field], proposal[field]);
        var decision = row["decision"] == "approve" ? ProcurementSupplierPayableDecision.Approve : ProcurementSupplierPayableDecision.Reject;
        Equal(row["binding"], B.DecisionBinding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64,
            proposal, decision, row["source"].AsBsonDocument, row["policy"].AsBsonDocument,
            row["accounting"].AsBsonDocument, row["capacity"].AsBsonDocument));
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(B.DecideBody(proposal["_id"].AsInt64, 1, decision, row["binding"].AsString), row["reason"].AsString));
        if (row["recordedAt"].ToUniversalTime() < proposal["recordedAt"].ToUniversalTime()) throw B.Broken();
        if (decision == ProcurementSupplierPayableDecision.Approve)
        {
            await PostedAsync(tx, row, ct);
            if (row["input"]["kind"] == "recognize") {
                if (row["payableId"].IsBsonNull || !row["applicationId"].IsBsonNull || !row["payableBefore"].IsBsonNull ||
                    !row["payableAfter"].IsBsonNull || !row["advanceBefore"].IsBsonNull || !row["advanceAfter"].IsBsonNull) throw B.Broken();
                var payable = await PayableRootAsync(tx, row["payableId"].AsInt64, ct, proof);
                Equal(payable["decisionId"], row["_id"]);
            } else {
                if (row["payableId"].IsBsonNull || row["payableId"] != row["input"]["payableId"] || row["applicationId"].IsBsonNull ||
                    row["payableBefore"].IsBsonNull || row["payableAfter"].IsBsonNull ||
                    row["advanceBefore"].IsBsonNull || row["advanceAfter"].IsBsonNull) throw B.Broken();
                var application = await Rows(B.Applications).Find(tx, F.Eq("_id", row["applicationId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
                Equal(application["snapshotHash"], B.Hash(application)); Equal(application["decisionId"], row["_id"]);
                Equal(application["payableId"], row["payableId"]);
                Equal(application["payableBefore"], row["payableBefore"]); Equal(application["payableAfter"], row["payableAfter"]);
                Equal(application["advanceBefore"], row["advanceBefore"]); Equal(application["advanceAfter"], row["advanceAfter"]);
                var payable = proof?.Payable is { } existingPayable && existingPayable["_id"] == application["payableId"]
                    ? existingPayable : await PayableRootAsync(tx, application["payableId"].AsInt64, ct);
                var advance = proof?.Advance is { } existingAdvance && existingAdvance.Settlement["_id"] == application["settlementId"]
                    ? existingAdvance : await settlements.ReadPayableSourceAsync(database, tx, application["settlementId"].AsInt64, ct);
                _ = await CapacityAsync(tx, payable, advance, ct);
            }
        } else if (!row["payableId"].IsBsonNull || !row["applicationId"].IsBsonNull || !row["journalId"].IsBsonNull ||
            !row["journalFingerprint"].IsBsonNull || row["postingFence"] != 0L || row["payableBefore"] != row["payableAfter"] ||
            row["advanceBefore"] != row["advanceAfter"]) throw B.Broken();
        await EffectsAsync(tx, row, ct); return row;
    }
    private static void HistoryPeople(BsonDocument proposal, BsonDocument? decision)
    {
        var maker = proposal["actorId"].AsInt64;
        var original = proposal["people"].AsBsonArray.Select(R.Person).ToArray();
        if (original.Length is < 1 or > 4 || original.Select(person => person.UserId).Distinct().Count() != original.Length ||
            original.Any(person => person.Verification != PersonVerification.Verified || person.PersonId <= 0 || person.Version <= 0) ||
            !original.Any(person => person.UserId == maker)) throw B.Broken();
        var policy = proposal["policy"]["people"].AsBsonArray.Select(R.Person).ToArray();
        foreach (var person in policy) if (original.SingleOrDefault(value => value.UserId == person.UserId)?.PersonId != person.PersonId) throw B.Broken();
        if (decision is null) return;
        var checker = decision["actorId"].AsInt64; var people = decision["people"].AsBsonArray.Select(R.Person).ToArray();
        if (people.Length is < 2 or > 4 || people.Select(person => person.UserId).Distinct().Count() != people.Length ||
            people.Any(person => person.Verification != PersonVerification.Verified || person.PersonId <= 0 || person.Version <= 0) ||
            !people.Any(person => person.UserId == checker) || !people.Any(person => person.UserId == maker) ||
            !PersonIndependencePolicy.AreIndependent(people.Single(person => person.UserId == checker), people.Single(person => person.UserId == maker)))
            throw B.Broken();
        foreach (var person in original) if (people.SingleOrDefault(value => value.UserId == person.UserId)?.PersonId != person.PersonId) throw B.Broken();
    }
    internal async Task PreflightAsync(IClientSessionHandle tx, CancellationToken ct)
    {
        var proposalIds = await Rows(B.Proposals).Find(tx, F.Empty).Project(new BsonDocument("_id", 1)).ToListAsync(ct);
        foreach (var raw in proposalIds) { var proposal = await ProposalAsync(tx, raw["_id"].AsInt64, ct); _ = await DecisionAsync(tx, proposal, ct); }
        foreach (var raw in await Rows(B.Payables).Find(tx, F.Empty).Project(new BsonDocument("_id", 1)).ToListAsync(ct)) {
            var payable = await PayableRootAsync(tx, raw["_id"].AsInt64, ct); _ = await PayableOnlyCapacityAsync(tx, payable, ct);
        }
        foreach (var raw in await Rows(B.AdvanceHeads).Find(tx, F.Empty).Project(new BsonDocument("_id", 1)).ToListAsync(ct)) {
            var advance = await settlements.ReadPayableSourceAsync(database, tx, raw["_id"].AsInt64, ct);
            _ = await AdvanceOnlyCapacityAsync(tx, advance, ct);
        }
        foreach (var (table, field, local) in new[] {
            ("auditEvents", "kind", "result.proposalId"),
            ("outboxEvents", "kind", "payload.proposalId"),
            ("idempotencyRecords", "operation", "result.proposalId"),
            ("journalEntries", "source.namespace", "source.documentId") }) {
            foreach (var operation in new[] { B.ProposeOperation, B.DecideOperation }) {
                if (table == "journalEntries" && operation == B.ProposeOperation) continue;
                var destination = operation == B.ProposeOperation ? B.Proposals : B.Decisions;
                var lookupField = operation == B.ProposeOperation ? local : table == "journalEntries" ? local : "result.decisionId";
                if (table == "outboxEvents" && operation == B.DecideOperation) lookupField = "payload.decisionId";
                var pipeline = new BsonDocument[] { new("$match", new BsonDocument(field, operation)),
                    new("$lookup", new BsonDocument { { "from", destination }, { "localField", lookupField }, { "foreignField", "_id" }, { "as", "owner" } }),
                    new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1) };
                if (await Rows(table).Aggregate<BsonDocument>(tx, pipeline).AnyAsync(ct)) throw B.Broken();
            }
        }
        foreach (var raw in await Rows(B.Applications).Find(tx, F.Empty).Project(new BsonDocument("_id", 1)).ToListAsync(ct)) {
            var decision = await Rows(B.Decisions).Find(tx, F.Eq("applicationId", raw["_id"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            if (decision["decision"] != "approve") throw B.Broken();
        }
        foreach (var raw in await Rows(B.PayableHeads).Find(tx, F.Empty).Project(new BsonDocument("_id", 1)).ToListAsync(ct))
            if (!await Rows(B.Payables).Find(tx, F.Eq("_id", raw["_id"])).AnyAsync(ct)) throw B.Broken();
    }
}
