using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using B = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementSupplierPayableService
{
    public async Task<BsonDocument> PreviewAsync(ActorContext actor, ProcurementSupplierPayableInput input, CancellationToken ct = default)
    {
        input = ProcurementSupplierPayableRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await PrepareAsync(actor, authority, tx, input, Permissions.ProcurementPayablePropose, token);
            RequireCapacity(context.Source);
            var result = new BsonDocument { { "input", B.Input(input) }, { "source", context.Source.Facts },
                { "capacity", PublicCapacity(context.Source.Capacity) }, { "policy", PolicyView(context.Policy) },
                { "accounting", context.Accounting }, { "amount", Amount(input, context.Source).ToString() },
                { "binding", B.Binding(actor.UserId, actor.GrantVersion, B.Input(input), context.Source.Facts,
                    context.Policy, context.Accounting, context.Source.Capacity) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    private static UnitToko.Modules.Accounting.Money Amount(ProcurementSupplierPayableInput input, ProcurementSupplierPayableSource source) =>
        input.Kind == ProcurementSupplierPayableKind.Recognize ? UnitToko.Modules.Accounting.Money.Parse(source.Facts["amount"].AsString) : input.Amount!.Value;
    private static void RequireCapacity(ProcurementSupplierPayableSource source)
    {
        if (source.Facts["kind"] == "recognize") {
            if (source.Capacity["recognized"].AsBoolean) throw B.Changed("procurement_supplier_payable_already_recognized");
        } else {
            var amount = UnitToko.Modules.Accounting.Money.Parse(source.Facts["amount"].AsString);
            if (amount.Value <= 0 || amount.Value > UnitToko.Modules.Accounting.Money.Parse(source.Capacity["payable"]["remainingAmount"].AsString).Value ||
                amount.Value > UnitToko.Modules.Accounting.Money.Parse(source.Capacity["advance"]["remainingAmount"].AsString).Value)
                throw B.Changed("procurement_supplier_payable_capacity_exceeded");
        }
    }
    public async Task<BsonDocument> ProposeAsync(ActorContext actor, ProcurementSupplierPayableInput input, string binding,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        input = ProcurementSupplierPayableRules.Copy(input); Binding(binding); Metadata(metadata);
        if (metadata.Reason != input.Reason) throw B.Invalid(); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Proposals, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.ProposeBody(input, binding); Context? context = null;
        return await authorization.ExecuteCommandAsync(actor, B.ProposeOperation, metadata, body,
            async (authority, tx, token) => {
                if (await Rows(B.Proposals).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).AnyAsync(token))
                    throw B.Changed("procurement_supplier_payable_lookup_required");
                context = await PrepareAsync(actor, authority, tx, input, Permissions.ProcurementPayablePropose, token);
                RequireCapacity(context.Source);
                Equal(binding, B.Binding(actor.UserId, actor.GrantVersion, B.Input(input), context.Source.Facts,
                    context.Policy, context.Accounting, context.Source.Capacity));
                return Command(context);
            }, async (tx, token) => {
                var active = context ?? throw B.Broken(); await FenceAsync(active, token);
                var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "version", 1L },
                    { "location", R.Location(input.Location) }, { "input", B.Input(input) }, { "source", active.Source.Facts.DeepClone() },
                    { "capacity", B.CapacitySnapshot(active.Source.Capacity) }, { "policy", B.PolicySnapshot(active.Policy) },
                    { "accounting", active.Accounting.DeepClone() }, { "amount", Amount(input, active.Source).ToString() },
                    { "binding", binding }, { "people", new BsonArray(active.People!.Evidence.OrderBy(person => person.UserId).Select(R.Person)) } };
                B.Metadata(row, B.ProposeOperation, auditId, actor, metadata, body, Now);
                await Rows(B.Proposals).InsertOneAsync(tx, row, cancellationToken: token);
                await WriteEffectsAsync(tx, row, token); return B.Result(row);
            }, ct, async (_, _, _) => Current(context ?? throw B.Broken()), validateCurrent: () => Current(context ?? throw B.Broken()));
    }
    public async Task<BsonDocument> PreviewDecisionAsync(ActorContext actor, long proposalId,
        ProcurementSupplierPayableDecision decision, CancellationToken ct = default)
    {
        if (proposalId <= 0 || !Enum.IsDefined(decision)) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var verified = await ProposalWithProofAsync(tx, proposalId, token); var proposal = verified.Proposal;
            if (await DecisionAsync(tx, proposal, token, verified.Proof) is not null) throw B.Changed("procurement_supplier_payable_already_decided");
            var input = B.Input(proposal["input"]); var posting = decision == ProcurementSupplierPayableDecision.Approve;
            var context = await PrepareAsync(actor, authority, tx, input, Permissions.ProcurementPayableDecide, token, proposal, posting, verified.Proof);
            if (posting) { RequireCapacity(context.Source); Review(context, proposal); }
            var result = new BsonDocument { { "proposal", ProposalView(proposal) }, { "decision", B.Decision(decision) },
                { "source", context.Source.Facts }, { "capacity", PublicCapacity(context.Source.Capacity) },
                { "policy", PolicyView(context.Policy) }, { "accounting", context.Accounting },
                { "binding", B.DecisionBinding(actor.UserId, actor.GrantVersion, proposal, decision,
                    context.Source.Facts, context.Policy, context.Accounting, context.Source.Capacity) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token, posting); return result;
        }, ct);
    }
    private static void Review(Context context, BsonDocument proposal)
    {
        Equal(proposal["source"], context.Source.Facts); Equal(proposal["policy"], B.PolicySnapshot(context.Policy));
        Equal(proposal["accounting"], context.Accounting);
        Equal(proposal["capacity"], B.CapacitySnapshot(context.Source.Capacity));
    }
    public async Task<BsonDocument> DecideAsync(ActorContext actor, long proposalId, long expectedVersion,
        ProcurementSupplierPayableDecision decision, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        if (proposalId <= 0 || expectedVersion != 1 || !Enum.IsDefined(decision)) throw B.Invalid();
        Binding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Decisions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.DecideBody(proposalId, expectedVersion, decision, binding); Context? context = null;
        var posting = decision == ProcurementSupplierPayableDecision.Approve;
        return await authorization.ExecuteCommandAsync(actor, B.DecideOperation, metadata, body,
            async (authority, tx, token) => {
                if (await Rows(B.Decisions).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).AnyAsync(token))
                    throw B.Changed("procurement_supplier_payable_lookup_required");
                var verified = await ProposalWithProofAsync(tx, proposalId, token); var proposal = verified.Proposal;
                if (await DecisionAsync(tx, proposal, token, verified.Proof) is not null) throw B.Changed("procurement_supplier_payable_already_decided");
                context = await PrepareAsync(actor, authority, tx, B.Input(proposal["input"]), Permissions.ProcurementPayableDecide, token, proposal, posting, verified.Proof);
                if (posting) { RequireCapacity(context.Source); Review(context, proposal); }
                Equal(binding, B.DecisionBinding(actor.UserId, actor.GrantVersion, proposal, decision,
                    context.Source.Facts, context.Policy, context.Accounting, context.Source.Capacity));
                return Command(context, proposal);
            }, async (tx, token) => {
                var active = context ?? throw B.Broken(); var proposal = active.Proposal ?? throw B.Broken();
                if (await DecisionAsync(tx, proposal, token) is not null) throw B.Changed("procurement_supplier_payable_already_decided");
                await FenceAsync(active, token, posting);
                var at = Now; BsonValue payableId = BsonNull.Value, applicationId = BsonNull.Value;
                var payableBefore = active.Source.Capacity.GetValue("payable", BsonNull.Value);
                var advanceBefore = active.Source.Capacity.GetValue("advance", BsonNull.Value);
                BsonValue payableAfter = payableBefore, advanceAfter = advanceBefore;
                if (posting) {
                    if (active.Input.Kind == ProcurementSupplierPayableKind.Recognize) payableId = (await ids.NextAsync(B.Payables, token)).Value;
                    else {
                        payableId = active.Input.PayableId!.Value;
                        applicationId = (await ids.NextAsync(B.Applications, token)).Value;
                        var amount = active.Input.Amount!.Value;
                        payableAfter = B.After(payableBefore.AsBsonDocument, amount, applicationId.AsInt64, proposal["snapshotHash"].AsString);
                        advanceAfter = B.After(advanceBefore.AsBsonDocument, amount, applicationId.AsInt64, proposal["snapshotHash"].AsString);
                    }
                }
                var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "version", 1L }, { "proposalId", proposalId },
                    { "location", proposal["location"] }, { "input", proposal["input"] }, { "source", proposal["source"] },
                    { "capacity", B.CapacitySnapshot(active.Source.Capacity) }, { "policy", proposal["policy"] }, { "accounting", proposal["accounting"] },
                    { "amount", proposal["amount"] }, { "proposalHash", proposal["snapshotHash"] },
                    { "proposalMakerId", proposal["actorId"] }, { "decision", B.Decision(decision) },
                    { "payableBefore", payableBefore }, { "payableAfter", payableAfter }, { "advanceBefore", advanceBefore },
                    { "advanceAfter", advanceAfter }, { "payableId", payableId }, { "applicationId", applicationId },
                    { "journalId", BsonNull.Value }, { "journalFingerprint", BsonNull.Value }, { "postingFence", 0L },
                    { "binding", binding }, { "people", new BsonArray(active.People!.Evidence.OrderBy(person => person.UserId).Select(R.Person)) } };
                B.Metadata(row, B.DecideOperation, auditId, actor, metadata, body, at);
                await Rows(B.Decisions).InsertOneAsync(tx, row, cancellationToken: token);
                if (posting) await PostAndApplyAsync(tx, active, proposal, row, payableAfter, advanceAfter, at, token);
                await WriteEffectsAsync(tx, row, token); return B.Result(row);
            }, ct, async (_, _, _) => Current(context ?? throw B.Broken(), posting), validateCurrent: () => Current(context ?? throw B.Broken(), posting));
    }
}
