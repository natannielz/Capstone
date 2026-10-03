using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using B = UnitToko.Platform.Procurement.ProcurementStockValuationBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementStockValuationService
{
    private static void Metadata(AuthorizationCommandMetadata metadata) { AuthorizationStore.ValidateMetadata(metadata); Key(metadata.IdempotencyKey);
        ProcurementStockReceiptRules.Text(metadata.CorrelationId, 128); }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
            { "kind", row["operation"] }, { "sourceKey", B.SourceKey(row) }, { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] },
            { "location", row["location"] }, { "payload", B.Result(row) }, { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] },
            { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
    private static BsonDocument PolicyView(BsonDocument row) => new() { { "id", row["_id"] }, { "revision", row["revision"] },
        { "active", row["active"] }, { "location", row["location"] }, { "currency", row["currency"] }, { "approvedAt", row["approvedAt"] },
        { "expiresAt", row["expiresAt"] }, { "sourceRecognition", row["sourceRecognition"] }, { "bookId", row["bookId"] },
        { "chartVersionId", row["chartVersionId"] },
        { "inventory", new BsonDocument { { "mappingKey", row["inventoryMapping"] }, { "accountId", row["inventoryAccountId"] }, { "accountCode", row["inventoryAccountCode"] } } },
        { "receiptAccrual", new BsonDocument { { "mappingKey", row["receiptAccrualMapping"] }, { "accountId", row["receiptAccrualAccountId"] }, { "accountCode", row["receiptAccrualAccountCode"] } } },
        { "capitalizeTax", row["capitalizeTax"] }, { "capitalizeCharge", row["capitalizeCharge"] },
        { "operatingOwnerReference", row["operatingOwnerReference"] }, { "approvalReference", row["approvalReference"] } };
    private static BsonDocument MatchView(ProcurementStockValuationSources source, ProcurementStockValuationInput input, BsonValue? capacity = null) {
        var line = source.MatchLine; return new() { { "invoiceDocumentId", source.Invoice["_id"] }, { "invoiceDocumentRevision", 1L },
            { "invoiceMatchId", source.Match["_id"] }, { "matchLineNumber", input.MatchLineNumber },
            { "documentReference", source.Invoice["input"]["documentReference"] },
            { "line", new BsonDocument { { "documentLineNumber", line["documentLineNumber"] }, { "packages", line["packages"] },
                { "netAmount", line["netAmount"] }, { "taxAmount", line["taxAmount"] }, { "chargeAmount", line["chargeAmount"] },
                { "inspectionId", line["inspectionId"] }, { "inspectionLineNumber", line["inspectionLineNumber"] }, { "state", line["state"] } } },
            { "matchCapacity", capacity ?? source.Capacity.Match } };
    }
    private static BsonDocument ProposalView(BsonDocument row, BsonDocument? decision, ProcurementStockValuationSources source) => new() { { "id", row["_id"] }, { "version", 1L },
        { "input", row["input"] }, { "receiptHash", row["receiptHash"] }, { "invoiceHash", row["invoiceHash"] }, { "matchHash", row["matchHash"] },
        { "receipt", ProcurementStockReceiptBson.View(source.Receipt) },
        { "match", MatchView(source, B.Input(row["input"]), row["matchCapacity"]) },
        { "policy", PolicyView(row["policy"].AsBsonDocument) }, { "accounting", row["accounting"] },
        { "receiptCapacity", row["receiptCapacity"] }, { "matchCapacity", row["matchCapacity"] },
        { "recordedAt", row["recordedAt"] }, { "reason", row["reason"] },
        { "decisionReason", decision is null ? BsonNull.Value : decision["reason"] },
        { "decision", decision is null ? BsonNull.Value : B.Result(decision) } };
    public async Task<BsonDocument> PreviewAsync(ActorContext actor, ProcurementStockValuationInput input, CancellationToken ct = default) {
        input = ProcurementStockValuationRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await PrepareAsync(actor, authority, tx, input, Permissions.ProcurementStockValuationPropose, token);
            var source = context.Source!; var result = new BsonDocument { { "input", B.Input(input) }, { "receipt", ProcurementStockReceiptBson.View(source.Receipt) },
                { "match", MatchView(source, input) }, { "policy", PolicyView(source.Policy) }, { "accounting", source.Accounting },
                { "receiptCapacity", source.Capacity.Receipt }, { "matchCapacity", source.Capacity.Match },
                { "binding", B.Binding(actor.UserId, actor.GrantVersion, input, source.Receipt, source.Invoice, source.Match, source.Policy,
                    source.Accounting, source.Capacity.Receipt, source.Capacity.Match) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> ProposeAsync(ActorContext actor, ProcurementStockValuationInput input, string binding,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = ProcurementStockValuationRules.Copy(input); Binding(binding); Metadata(metadata);
        if (metadata.Reason != input.Reason) throw ProcurementStockValuationRules.Invalid(); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Proposals, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.ProposeBody(input, binding); Context? context = null;
        return await authorization.ExecuteCommandAsync(actor, B.ProposeOperation, metadata, body, async (authority, tx, token) => {
            if (await records.ForKeyAsync(tx, actor.UserId, B.ProposeOperation, metadata.IdempotencyKey, token) is not null)
                throw B.Changed("procurement_stock_valuation_lookup_required");
            context = await PrepareAsync(actor, authority, tx, input, Permissions.ProcurementStockValuationPropose, token);
            var source = context.Source!; ProcurementStockValuationRecords.Equal(binding, B.Binding(actor.UserId, actor.GrantVersion, input,
                source.Receipt, source.Invoice, source.Match, source.Policy, source.Accounting, source.Capacity.Receipt, source.Capacity.Match));
            return Command(context);
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); Current(active); await FenceAsync(active, token); var source = active.Source!;
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "proposalId", id }, { "version", 1L },
                { "location", R.Location(input.Location) }, { "input", B.Input(input) }, { "receiptHash", source.Receipt["snapshotHash"] },
                { "invoiceHash", source.Invoice["snapshotHash"] }, { "matchHash", source.Match["snapshotHash"] },
                { "matchActorId", source.Match["actorId"] }, { "matchLine", source.MatchLine.DeepClone() },
                { "policy", B.PolicySnapshot(source.Policy) }, { "accounting", source.Accounting.DeepClone() },
                { "receiptCapacity", source.Capacity.Receipt.DeepClone() }, { "matchCapacity", source.Capacity.Match.DeepClone() }, { "binding", binding },
                { "people", new BsonArray(active.People!.Evidence.OrderBy(person => person.UserId).Select(R.Person)) } };
            B.Metadata(row, B.ProposeOperation, auditId, actor, metadata, body, Now);
            await Rows(B.Proposals).InsertOneAsync(tx, row, cancellationToken: token); await EffectsAsync(tx, row, token);
            active.Proposal = row; return B.Result(row);
        }, ct, async (_, tx, token) => {
            var active = context ?? throw B.Broken(); var saved = await records.ForKeyAsync(tx, actor.UserId, B.ProposeOperation, metadata.IdempotencyKey, token) ?? throw B.Broken();
            ProcurementStockValuationRecords.Equal(saved["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason)); await FenceAsync(active, token);
        }, validateCurrent: () => Current(context ?? throw B.Broken()));
    }
    public async Task<BsonDocument> PreviewDecisionAsync(ActorContext actor, long proposalId, ProcurementStockValuationDecision decision,
        CancellationToken ct = default) {
        if (proposalId <= 0 || !Enum.IsDefined(decision)) throw ProcurementStockValuationRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var proposal = await records.ProposalAsync(tx, proposalId, token); var input = B.Input(proposal["input"]);
            if (await records.DecisionAsync(tx, proposal, token) is not null) throw B.Changed("procurement_stock_valuation_already_decided");
            var posting = decision == ProcurementStockValuationDecision.Approve;
            var context = await PrepareAsync(actor, authority, tx, input, Permissions.ProcurementStockValuationDecide, token,
                posting ? null : proposal, posting); context.Proposal = proposal;
            await PeopleAsync(context, proposal["actorId"].AsInt64, proposal["matchActorId"].AsInt64, token);
            ReviewDecision(context, proposal, posting);
            var source = context.Source!; var result = new BsonDocument { { "proposal", ProposalView(proposal, null, source) }, { "decision", B.Decision(decision) },
                { "receiptCapacity", source.Capacity.Receipt }, { "matchCapacity", source.Capacity.Match },
                { "policy", PolicyView(source.Policy) }, { "accounting", source.Accounting },
                { "binding", B.DecisionBinding(actor.UserId, actor.GrantVersion, proposal, decision, source.Capacity.Receipt,
                    source.Capacity.Match, source.Policy, source.Accounting) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token, posting); return result;
        }, ct);
    }
    private static void ReviewDecision(Context context, BsonDocument proposal, bool posting) {
        var source = context.Source ?? throw B.Broken();
        ProcurementStockValuationRecords.Equal(proposal["receiptHash"], source.Receipt["snapshotHash"]);
        ProcurementStockValuationRecords.Equal(proposal["invoiceHash"], source.Invoice["snapshotHash"]);
        ProcurementStockValuationRecords.Equal(proposal["matchHash"], source.Match["snapshotHash"]);
        if (posting) {
            ProcurementStockValuationRecords.Equal(proposal["policy"], B.PolicySnapshot(source.Policy));
            ProcurementStockValuationRecords.Equal(proposal["accounting"], source.Accounting);
        }
    }
    public async Task<BsonDocument> DecideAsync(ActorContext actor, long proposalId, long expectedVersion, ProcurementStockValuationDecision decision,
        string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (proposalId <= 0 || expectedVersion != 1 || !Enum.IsDefined(decision)) throw ProcurementStockValuationRules.Invalid();
        Binding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Decisions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.DecideBody(proposalId, expectedVersion, decision, binding); Context? context = null; var posting = decision == ProcurementStockValuationDecision.Approve;
        return await authorization.ExecuteCommandAsync(actor, B.DecideOperation, metadata, body, async (authority, tx, token) => {
            if (await records.ForKeyAsync(tx, actor.UserId, B.DecideOperation, metadata.IdempotencyKey, token) is not null)
                throw B.Changed("procurement_stock_valuation_lookup_required");
            var proposal = await records.ProposalAsync(tx, proposalId, token);
            if (await records.DecisionAsync(tx, proposal, token) is not null) throw B.Changed("procurement_stock_valuation_already_decided");
            var input = B.Input(proposal["input"]);
            context = await PrepareAsync(actor, authority, tx, input, Permissions.ProcurementStockValuationDecide, token,
                posting ? null : proposal, posting); context.Proposal = proposal;
            await PeopleAsync(context, proposal["actorId"].AsInt64, proposal["matchActorId"].AsInt64, token);
            ReviewDecision(context, proposal, posting); var source = context.Source!;
            ProcurementStockValuationRecords.Equal(binding, B.DecisionBinding(actor.UserId, actor.GrantVersion, proposal, decision,
                source.Capacity.Receipt, source.Capacity.Match, source.Policy, source.Accounting));
            return Command(context, proposal, posting);
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); var proposal = active.Proposal ?? throw B.Broken(); var source = active.Source ?? throw B.Broken();
            if (await records.DecisionAsync(tx, proposal, token) is not null) throw B.Changed("procurement_stock_valuation_already_decided");
            Current(active, posting); await FenceAsync(active, token, posting);
            var input = B.Input(proposal["input"]); var at = Now;
            BsonValue allocationId = BsonNull.Value; var receiptAfter = source.Capacity.Receipt.DeepClone().AsBsonDocument;
            var matchAfter = source.Capacity.Match.DeepClone().AsBsonDocument;
            if (posting) {
                allocationId = (await ids.NextAsync(B.Allocations, token)).Value;
                receiptAfter = ProcurementStockValuationCapacityReader.AfterReceipt(source.Capacity.Receipt, input.Packages, allocationId.AsInt64, proposal["snapshotHash"].AsString);
                matchAfter = ProcurementStockValuationCapacityReader.AfterMatch(source.Capacity.Match, input.Packages, input.NetAmount,
                    input.TaxAmount, input.ChargeAmount, allocationId.AsInt64, proposal["snapshotHash"].AsString);
            }
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "proposalId", proposalId }, { "version", 1L },
                { "location", proposal["location"] }, { "input", proposal["input"] }, { "proposalHash", proposal["snapshotHash"] }, { "decision", B.Decision(decision) },
                { "receiptHash", proposal["receiptHash"] }, { "invoiceHash", proposal["invoiceHash"] }, { "matchHash", proposal["matchHash"] },
                { "policy", proposal["policy"] }, { "accounting", proposal["accounting"] }, { "receiptBefore", source.Capacity.Receipt.DeepClone() },
                { "receiptAfter", receiptAfter }, { "matchBefore", source.Capacity.Match.DeepClone() }, { "matchAfter", matchAfter },
                { "allocationId", allocationId }, { "journalId", BsonNull.Value }, { "journalFingerprint", BsonNull.Value }, { "postingFence", 0L },
                { "binding", binding }, { "people", new BsonArray(active.People!.Evidence.OrderBy(person => person.UserId).Select(R.Person)) } };
            B.Metadata(row, B.DecideOperation, auditId, actor, metadata, body, at);
            await Rows(B.Decisions).InsertOneAsync(tx, row, cancellationToken: token);
            if (posting) {
                var journal = await new PostJournal(database, clock, [new ProcurementStockValuationPostingSource(database, row)]).PostAsync(tx, actor,
                    ProcurementStockValuationPostingSource.Source(id), 1, new(metadata.Reason, metadata.CorrelationId), token);
                var actual = await Rows("journalEntries").Find(tx, new BsonDocument("_id", journal.Id)).SingleOrDefaultAsync(token) ?? throw B.Broken();
                ProcurementStockValuationPostingSource.RequireAccounts(row, AccountingBson.ReadDraft(actual));
                ProcurementStockValuationRecords.Equal(actual["bookId"], source.Accounting["bookId"]);
                var pending = row.DeepClone().AsBsonDocument; pending["postingFence"] = 1L;
                row["postingFence"] = 1L; row["journalId"] = journal.Id; row["journalFingerprint"] = journal.Fingerprint;
                row["snapshotHash"] = B.Hash(row);
                if ((await Rows(B.Decisions).ReplaceOneAsync(tx, pending, row, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
                var allocation = new BsonDocument { { "_id", allocationId }, { "schemaVersion", 1 }, { "proposalId", proposalId },
                    { "decisionId", id }, { "proposalHash", proposal["snapshotHash"] }, { "receiptId", input.ReceiptId },
                    { "receiptHash", source.Receipt["snapshotHash"] }, { "invoiceDocumentId", input.InvoiceDocumentId },
                    { "invoiceMatchId", input.InvoiceMatchId }, { "matchLineNumber", input.MatchLineNumber }, { "matchHash", source.Match["snapshotHash"] },
                    { "packages", input.Packages }, { "netAmount", input.NetAmount.ToString() }, { "taxAmount", input.TaxAmount.ToString() },
                    { "chargeAmount", input.ChargeAmount.ToString() }, { "capitalizedAmount", input.CapitalizedAmount.ToString() },
                    { "receiptSequence", receiptAfter["version"] }, { "matchSequence", matchAfter["version"] },
                    { "receiptBefore", source.Capacity.Receipt.DeepClone() }, { "receiptAfter", receiptAfter.DeepClone() },
                    { "matchBefore", source.Capacity.Match.DeepClone() }, { "matchAfter", matchAfter.DeepClone() },
                    { "journalId", journal.Id }, { "recordedAt", at.UtcDateTime } };
                allocation.Add("snapshotHash", B.Hash(allocation)); await Rows(B.Allocations).InsertOneAsync(tx, allocation, cancellationToken: token);
                var receiptHead = ProcurementStockValuationCapacityReader.Head(receiptAfter, input.ReceiptId, source.Receipt, at,
                    source.Capacity.ReceiptHead, receiptId: input.ReceiptId);
                var matchHead = ProcurementStockValuationCapacityReader.Head(matchAfter,
                    ProcurementStockValuationCapacityReader.MatchKey(input.InvoiceMatchId, input.MatchLineNumber), source.Match, at,
                    source.Capacity.MatchHead, matchId: input.InvoiceMatchId, lineNumber: input.MatchLineNumber);
                if (source.Capacity.ReceiptHead is null) await Rows(B.ReceiptHeads).InsertOneAsync(tx, receiptHead, cancellationToken: token);
                else if ((await Rows(B.ReceiptHeads).ReplaceOneAsync(tx, source.Capacity.ReceiptHead, receiptHead, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
                if (source.Capacity.MatchHead is null) await Rows(B.MatchHeads).InsertOneAsync(tx, matchHead, cancellationToken: token);
                else if ((await Rows(B.MatchHeads).ReplaceOneAsync(tx, source.Capacity.MatchHead, matchHead, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
                active.Fences.RemoveAll(fence => fence.Table == B.ReceiptHeads && fence.Row["_id"] == receiptHead["_id"] ||
                    fence.Table == B.MatchHeads && fence.Row["_id"] == matchHead["_id"]);
                active.Fences.Add((B.ReceiptHeads, receiptHead)); active.Fences.Add((B.MatchHeads, matchHead));
            }
            await EffectsAsync(tx, row, token); active.Decision = row; return B.Result(row);
        }, ct, async (_, tx, token) => {
            var saved = await records.ForKeyAsync(tx, actor.UserId, B.DecideOperation, metadata.IdempotencyKey, token) ?? throw B.Broken();
            ProcurementStockValuationRecords.Equal(saved["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason));
            await FenceAsync(context ?? throw B.Broken(), token, posting);
        }, validateCurrent: () => Current(context ?? throw B.Broken(), posting));
    }
}
