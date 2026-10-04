using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using B = UnitToko.Platform.Procurement.ProcurementAdvanceReturnBson;
using P = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementAdvanceReturnService
{
    public async Task<BsonDocument> PreviewAsync(ActorContext actor, ProcurementAdvanceReturnInput input, CancellationToken ct = default)
    {
        input = ProcurementAdvanceReturnRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await PrepareAsync(actor, authority, tx, input, Permissions.ProcurementAdvanceReturnPropose, false, null, token);
            var result = new BsonDocument { { "input", B.Input(input) }, { "source", context.Source }, { "capacity", context.Capacity },
                { "policy", PolicyView(context.Policy) }, { "accounting", context.Accounting },
                { "amount", ProcurementAdvanceReturnRules.Total(input).ToString() },
                { "binding", B.Binding(actor.UserId, actor.GrantVersion, B.Input(input), context.Source,
                    context.Policy, context.Accounting, context.Capacity) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token, false); return result;
        }, ct);
    }
    public async Task<BsonDocument> ProposeAsync(ActorContext actor, ProcurementAdvanceReturnInput input, string binding,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        input = ProcurementAdvanceReturnRules.Copy(input); Binding(binding); Metadata(metadata);
        if (metadata.Reason != input.Reason) throw B.Invalid(); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Proposals, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.ProposeBody(input, binding); Context? context = null;
        return await authorization.ExecuteCommandAsync(actor, B.ProposeOperation, metadata, body,
            async (authority, tx, token) => {
                if (await Rows(B.Proposals).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).AnyAsync(token))
                    throw B.Changed("procurement_advance_return_lookup_required");
                context = await PrepareAsync(actor, authority, tx, input, Permissions.ProcurementAdvanceReturnPropose, false, null, token);
                Equal(binding, B.Binding(actor.UserId, actor.GrantVersion, B.Input(input), context.Source,
                    context.Policy, context.Accounting, context.Capacity)); return Command(context);
            }, async (tx, token) => {
                var active = context ?? throw B.Broken(); await FenceAsync(active, token, false);
                var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "version", 1L },
                    { "location", R.Location(input.Location) }, { "input", B.Input(input) }, { "source", active.Source.DeepClone() },
                    { "capacity", active.Capacity.DeepClone() }, { "policy", B.PolicySnapshot(active.Policy) },
                    { "accounting", active.Accounting.DeepClone() }, { "amount", ProcurementAdvanceReturnRules.Total(input).ToString() },
                    { "binding", binding }, { "people", new BsonArray(active.People!.Evidence.OrderBy(person => person.UserId).Select(R.Person)) } };
                B.Metadata(row, B.ProposeOperation, auditId, actor, metadata, body, Now);
                await Rows(B.Proposals).InsertOneAsync(tx, row, cancellationToken: token);
                await WriteEffectsAsync(tx, row, token); return B.Result(row);
            }, ct, async (_, _, _) => Current(context ?? throw B.Broken(), false),
            validateCurrent: () => Current(context ?? throw B.Broken(), false));
    }
    private async Task<Context> RejectionContextAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct)
    {
        var input = B.Input(proposal["input"]);
        var context = new Context(actor, authority, tx, input, Permissions.ProcurementAdvanceReturnDecide) {
            Source = proposal["source"].AsBsonDocument, Capacity = proposal["capacity"].AsBsonDocument,
            Accounting = proposal["accounting"].AsBsonDocument, Proposal = proposal
        };
        _ = Queries(context, false); await PeopleAsync(context, proposal, ct); await OriginalMakerAsync(context, proposal, ct);
        Current(context, false); return context;
    }
    private static void Review(Context context, BsonDocument proposal)
    {
        Equal(proposal["source"], context.Source); Equal(proposal["policy"], B.PolicySnapshot(context.Policy));
        Equal(proposal["accounting"], context.Accounting); Equal(proposal["capacity"], context.Capacity);
    }
    public async Task<BsonDocument> PreviewDecisionAsync(ActorContext actor, long proposalId,
        ProcurementAdvanceReturnDecision decision, CancellationToken ct = default)
    {
        if (proposalId <= 0 || !Enum.IsDefined(decision)) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var proposal = await records.ProposalAsync(tx, proposalId, token);
            if (await records.DecisionAsync(tx, proposal, token) is not null) throw B.Changed("procurement_advance_return_already_decided");
            var posting = decision == ProcurementAdvanceReturnDecision.Approve;
            var context = posting ? await PrepareAsync(actor, authority, tx, B.Input(proposal["input"]),
                Permissions.ProcurementAdvanceReturnDecide, true, proposal, token)
                : await RejectionContextAsync(actor, authority, tx, proposal, token);
            if (posting) Review(context, proposal);
            var source = posting ? context.Source : proposal["source"].AsBsonDocument;
            var policy = posting ? context.Policy : proposal["policy"].AsBsonDocument;
            var accounting = posting ? context.Accounting : proposal["accounting"].AsBsonDocument;
            var capacity = posting ? context.Capacity : proposal["capacity"].AsBsonDocument;
            var result = new BsonDocument { { "proposal", ProposalView(proposal) }, { "decision", B.Decision(decision) },
                { "source", source }, { "capacity", capacity }, { "policy", PolicyView(policy) }, { "accounting", accounting },
                { "binding", B.DecisionBinding(actor.UserId, actor.GrantVersion, proposal, decision,
                    source, policy, accounting, capacity) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token, posting); return result;
        }, ct);
    }
    public async Task<BsonDocument> DecideAsync(ActorContext actor, long proposalId, long expectedVersion,
        ProcurementAdvanceReturnDecision decision, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        if (proposalId <= 0 || expectedVersion != 1 || !Enum.IsDefined(decision)) throw B.Invalid();
        Binding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Decisions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.DecideBody(proposalId, expectedVersion, decision, binding); Context? context = null;
        var posting = decision == ProcurementAdvanceReturnDecision.Approve;
        return await authorization.ExecuteCommandAsync(actor, B.DecideOperation, metadata, body,
            async (authority, tx, token) => {
                if (await Rows(B.Decisions).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).AnyAsync(token))
                    throw B.Changed("procurement_advance_return_lookup_required");
                var proposal = await records.ProposalAsync(tx, proposalId, token);
                if (await records.DecisionAsync(tx, proposal, token) is not null) throw B.Changed("procurement_advance_return_already_decided");
                context = posting ? await PrepareAsync(actor, authority, tx, B.Input(proposal["input"]),
                    Permissions.ProcurementAdvanceReturnDecide, true, proposal, token)
                    : await RejectionContextAsync(actor, authority, tx, proposal, token);
                if (posting) Review(context, proposal);
                Equal(binding, B.DecisionBinding(actor.UserId, actor.GrantVersion, proposal, decision,
                    posting ? context.Source : proposal["source"].AsBsonDocument,
                    posting ? context.Policy : proposal["policy"].AsBsonDocument,
                    context.Accounting, context.Capacity));
                return Command(context, proposal);
            }, async (tx, token) => {
                var active = context ?? throw B.Broken(); var proposal = active.Proposal ?? throw B.Broken();
                if (await records.DecisionAsync(tx, proposal, token) is not null) throw B.Changed("procurement_advance_return_already_decided");
                await FenceAsync(active, token, posting);
                var lineIds = new BsonArray();
                if (posting) foreach (var _ in active.Input.Lines) lineIds.Add((await ids.NextAsync(B.Lines, token)).Value);
                var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "version", 1L },
                    { "proposalId", proposal["_id"] }, { "proposalHash", proposal["snapshotHash"] },
                    { "proposalMakerId", proposal["actorId"] }, { "decision", B.Decision(decision) },
                    { "receiptId", proposal["source"]["receiptId"] }, { "receiptHash", proposal["source"]["receiptHash"] },
                    { "supplierId", proposal["source"]["supplierId"] }, { "orderId", proposal["source"]["orderId"] },
                    { "returnLineIds", lineIds }, { "journalId", BsonNull.Value }, { "journalFingerprint", BsonNull.Value },
                    { "postingFence", 0L }, { "location", proposal["location"] }, { "input", proposal["input"] },
                    { "source", proposal["source"] }, { "capacity", proposal["capacity"] }, { "policy", proposal["policy"] },
                    { "accounting", proposal["accounting"] }, { "amount", proposal["amount"] }, { "binding", binding },
                    { "people", new BsonArray(active.People!.Evidence.OrderBy(person => person.UserId).Select(R.Person)) } };
                B.Metadata(row, B.DecideOperation, auditId, actor, metadata, body, Now);
                await Rows(B.Decisions).InsertOneAsync(tx, row, cancellationToken: token);
                if (posting) await PostAndReturnAsync(tx, active, proposal, row, token);
                await WriteEffectsAsync(tx, row, token); return B.Result(row);
            }, ct, async (_, _, _) => Current(context ?? throw B.Broken(), posting),
            validateCurrent: () => Current(context ?? throw B.Broken(), posting));
    }
    private async Task WriteEffectsAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct)
    {
        await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, B.Outbox(row), cancellationToken: ct);
    }
}
