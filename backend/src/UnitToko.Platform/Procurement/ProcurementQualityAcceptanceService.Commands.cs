using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using B = UnitToko.Platform.Procurement.ProcurementQualityAcceptanceBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementQualityAcceptanceService
{
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
            { "kind", row["operation"] }, { "sourceKey", B.SourceKey(row) }, { "actorId", row["actorId"] },
            { "requestKey", row["requestKey"] }, { "location", row["location"] }, { "payload", B.Result(row) },
            { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
    public async Task<BsonDocument> PreviewAsync(ActorContext actor, ProcurementQualityAcceptanceInput input, CancellationToken ct = default) {
        input = ProcurementQualityAcceptanceRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var context = await PrepareAsync(actor, authority, tx, input, Permissions.ProcurementQualityAcceptancePropose, token);
            await PeopleAsync(context, null, token); var source = context.Source!;
            var result = new BsonDocument { { "input", B.Input(input) }, { "receipt", ProcurementStockReceiptBson.View(source.Receipt) },
                { "heldBalance", HeldView(source.Receipt, source.Balance) }, { "policy", PolicyView(source.Policy) },
                { "capacity", source.Capacity.State },
                { "binding", B.Binding(actor.UserId, actor.GrantVersion, input, source.Receipt, source.Pool,
                    B.SourceConfigurationHash(source.PoolCapture!.Source), source.Policy, source.Capacity.State) },
                { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token, sourceFence: true); return result;
        }, ct);
    }
    public async Task<BsonDocument> ProposeAsync(ActorContext actor, ProcurementQualityAcceptanceInput input, string binding,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = ProcurementQualityAcceptanceRules.Copy(input); Binding(binding); Metadata(metadata);
        if (metadata.Reason != input.Reason) throw ProcurementQualityAcceptanceRules.Invalid(); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Proposals, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.ProposeBody(input, binding); Context? context = null;
        return await authorization.ExecuteCommandAsync(actor, B.ProposeOperation, metadata, body, async (authority, tx, token) => {
            if (await records.ForKeyAsync(tx, actor.UserId, B.ProposeOperation, metadata.IdempotencyKey, token) is not null)
                throw B.Changed("procurement_quality_acceptance_lookup_required");
            context = await PrepareAsync(actor, authority, tx, input, Permissions.ProcurementQualityAcceptancePropose, token);
            await PeopleAsync(context, null, token); var source = context.Source!;
            ProcurementQualityAcceptanceRecords.Equal(binding,
                B.Binding(actor.UserId, actor.GrantVersion, input, source.Receipt, source.Pool,
                    B.SourceConfigurationHash(source.PoolCapture!.Source), source.Policy, source.Capacity.State));
            return Command(context);
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); var source = active.Source ?? throw B.Broken();
            Current(active); await FenceAsync(active, token, sourceFence: true);
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "proposalId", id }, { "version", 1L },
                { "location", R.Location(input.Location) }, { "input", B.Input(input) }, { "receiptHash", source.Receipt["snapshotHash"] },
                { "inspectionHash", source.Receipt["inspection"]["snapshotHash"] }, { "lotId", source.Receipt["lotId"] },
                { "balanceId", source.Receipt["balanceId"] }, { "poolId", source.Receipt["poolId"] },
                { "pool", source.PoolCapture!.Pool.DeepClone() }, { "poolSource", source.PoolCapture.Source.DeepClone() },
                { "sourceConfigurationHash", B.SourceConfigurationHash(source.PoolCapture.Source) },
                { "heldBalance", source.Balance.DeepClone() },
                { "policy", B.PolicySnapshot(source.Policy) }, { "capacity", source.Capacity.State.DeepClone() },
                { "binding", binding }, { "people", new BsonArray { R.Person(active.People!.Evidence.Single(person => person.UserId == actor.UserId)) } } };
            B.Metadata(row, B.ProposeOperation, auditId, actor, metadata, body, Now);
            await Rows(B.Proposals).InsertOneAsync(tx, row, cancellationToken: token); await EffectsAsync(tx, row, token);
            active.Proposal = row; return B.Result(row);
        }, ct, async (_, tx, token) => {
            var saved = await records.ForKeyAsync(tx, actor.UserId, B.ProposeOperation, metadata.IdempotencyKey, token) ?? throw B.Broken();
            ProcurementQualityAcceptanceRecords.Equal(saved["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason));
            Current(context ?? throw B.Broken());
        }, validateCurrent: () => Current(context ?? throw B.Broken()));
    }
    private void ReviewDecision(Context context, BsonDocument proposal, ProcurementQualityAcceptanceDecision decision,
        ProcurementQualityAcceptanceObservation? observation) {
        var source = context.Source ?? throw B.Broken(); var input = B.Input(proposal["input"]);
        if (decision == ProcurementQualityAcceptanceDecision.Approve) {
            if (observation is null) throw ProcurementQualityAcceptanceRules.Invalid();
            var checker = ProcurementQualityAcceptanceRules.Copy(observation);
            if (checker.AcceptedPackages != input.AcceptedPackages || checker.PutawayReference != input.PutawayReference ||
                checker.SegregationReference != input.SegregationReference || checker.AcceptedCondition != input.AcceptedCondition ||
                checker.LotReference != input.LotReference || checker.ExpiresOn != input.ExpiresOn ||
                checker.NonExpiringReason != input.NonExpiringReason) throw B.Changed("procurement_quality_acceptance_independent_observation_mismatch");
            ProcurementQualityAcceptanceRecords.Equal(proposal["receiptHash"], source.Receipt["snapshotHash"]);
            ProcurementQualityAcceptanceRecords.Equal(proposal["heldBalance"], source.Balance);
            var originalPool = proposal["pool"].DeepClone().AsBsonDocument; var currentPool = source.Pool.DeepClone().AsBsonDocument;
            originalPool.Remove("fence"); currentPool.Remove("fence");
            ProcurementQualityAcceptanceRecords.Equal(originalPool, currentPool);
            ProcurementQualityAcceptanceRecords.Equal(proposal["sourceConfigurationHash"], B.SourceConfigurationHash(source.PoolCapture!.Source));
            ProcurementQualityAcceptanceRecords.Equal(proposal["policy"], B.PolicySnapshot(source.Policy));
            ProcurementQualityAcceptanceRecords.Equal(proposal["capacity"], source.Capacity.State);
            ProcurementQualityAcceptanceRecords.ObservationTime(checker.ObservedAt, source.Receipt, source.Policy, Now);
            context.CheckerObservation = checker;
        } else if (observation is not null) throw ProcurementQualityAcceptanceRules.Invalid();
    }
    private async Task<Context> DecisionContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        BsonDocument proposal, ProcurementQualityAcceptanceDecision decision, ProcurementQualityAcceptanceObservation? observation,
        bool fenceMaker, CancellationToken ct) {
        var input = B.Input(proposal["input"]); Context context;
        if (decision == ProcurementQualityAcceptanceDecision.Approve) {
            context = await PrepareAsync(actor, authority, tx, input, Permissions.ProcurementQualityAcceptanceDecide, ct);
        } else {
            context = new Context(actor, authority, tx, input.Location, Permissions.ProcurementQualityAcceptanceDecide, false); _ = Require(context);
            context.Source = await records.SourcesAsync(tx, input, new DateTimeOffset(proposal["recordedAt"].ToUniversalTime()), false, null, null, ct);
        }
        context.Proposal = proposal; await MakerAsync(context, proposal, fenceMaker, ct); await PeopleAsync(context, proposal, ct);
        var approval = new ApprovalRequest(1, 1, RequestedBy: proposal["actorId"].AsInt64);
        var reviewPeople = context.People!.Evidence.Where(person => person.UserId == actor.UserId ||
            person.UserId == proposal["actorId"].AsInt64).ToImmutableArray();
        var people = PersonApprovalPolicy.Evaluate(actor.UserId, Permissions.ProcurementQualityAcceptanceDecide, approval, reviewPeople);
        if (!people.Allowed) throw new AuthorizationDeniedException(people.Code);
        ReviewDecision(context, proposal, decision, observation); Current(context); return context;
    }
    public async Task<BsonDocument> PreviewDecisionAsync(ActorContext actor, long proposalId, ProcurementQualityAcceptanceDecision decision,
        ProcurementQualityAcceptanceObservation? observation, CancellationToken ct = default) {
        if (proposalId <= 0 || !Enum.IsDefined(decision) || (decision == ProcurementQualityAcceptanceDecision.Approve) != (observation is not null))
            throw ProcurementQualityAcceptanceRules.Invalid();
        await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument>(actor, async (authority, tx, token) => {
            var proposal = await records.ProposalAsync(tx, proposalId, token);
            if (await records.DecisionAsync(tx, proposal, token) is not null) throw B.Changed("procurement_quality_acceptance_already_decided");
            var context = await DecisionContextAsync(actor, authority, tx, proposal, decision, observation, false, token);
            var source = context.Source!; var result = new BsonDocument { { "proposal", await ProposalViewAsync(tx, proposal, token) },
                { "decision", B.Decision(decision) }, { "observation", observation is null ? BsonNull.Value : B.Observation(observation) },
                { "heldBalance", HeldView(source.Receipt, source.Balance) }, { "policy", PolicyView(source.Policy) },
                { "capacity", source.Capacity.State },
                { "binding", B.DecisionBinding(actor.UserId, actor.GrantVersion, proposal, decision, observation,
                    source.Pool, decision == ProcurementQualityAcceptanceDecision.Approve ? B.SourceConfigurationHash(source.PoolCapture!.Source)
                        : proposal["sourceConfigurationHash"].AsString, source.Policy, source.Capacity.State) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token, sourceFence: decision == ProcurementQualityAcceptanceDecision.Approve);
            return result;
        }, ct);
    }
    public async Task<BsonDocument> DecideAsync(ActorContext actor, long proposalId, long expectedVersion,
        ProcurementQualityAcceptanceDecision decision, ProcurementQualityAcceptanceObservation? observation,
        string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (proposalId <= 0 || expectedVersion != 1 || !Enum.IsDefined(decision) ||
            (decision == ProcurementQualityAcceptanceDecision.Approve) != (observation is not null)) throw ProcurementQualityAcceptanceRules.Invalid();
        if (observation is not null) observation = ProcurementQualityAcceptanceRules.Copy(observation);
        Binding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Decisions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.DecideBody(proposalId, expectedVersion, decision, observation, binding); Context? context = null;
        ActorContext? maker = null; AuthorizationSnapshot? makerSnapshot = null;
        return await authorization.ExecuteCommandAsync(actor, B.DecideOperation, metadata, body, async (authority, tx, token) => {
            if (await records.ForKeyAsync(tx, actor.UserId, B.DecideOperation, metadata.IdempotencyKey, token) is not null)
                throw B.Changed("procurement_quality_acceptance_lookup_required");
            var proposal = await records.ProposalAsync(tx, proposalId, token);
            if (await records.DecisionAsync(tx, proposal, token) is not null) throw B.Changed("procurement_quality_acceptance_already_decided");
            context = await DecisionContextAsync(actor, authority, tx, proposal, decision, observation, true, token);
            (maker, makerSnapshot) = await MakerAsync(context, proposal, false, token);
            var source = context.Source!;
            ProcurementQualityAcceptanceRecords.Equal(binding, B.DecisionBinding(actor.UserId, actor.GrantVersion, proposal,
                decision, observation, source.Pool,
                decision == ProcurementQualityAcceptanceDecision.Approve ? B.SourceConfigurationHash(source.PoolCapture!.Source)
                    : proposal["sourceConfigurationHash"].AsString, source.Policy, source.Capacity.State));
            return Command(context, proposal);
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); var proposal = active.Proposal ?? throw B.Broken(); var source = active.Source ?? throw B.Broken();
            if (await records.DecisionAsync(tx, proposal, token) is not null) throw B.Changed("procurement_quality_acceptance_already_decided");
            Current(active); await FenceAsync(active, token, sourceFence: decision == ProcurementQualityAcceptanceDecision.Approve);
            var input = B.Input(proposal["input"]); var at = Now; BsonValue allocationId = BsonNull.Value;
            var after = source.Capacity.State.DeepClone().AsBsonDocument;
            if (decision == ProcurementQualityAcceptanceDecision.Approve) {
                allocationId = (await ids.NextAsync(B.Allocations, token)).Value;
                after = ProcurementQualityAcceptanceCapacityReader.After(source.Capacity.State, input.AcceptedPackages,
                    allocationId.AsInt64, proposal["snapshotHash"].AsString);
            }
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "proposalId", proposalId }, { "version", 1L },
                { "location", proposal["location"] }, { "input", proposal["input"] }, { "proposalHash", proposal["snapshotHash"] },
                { "receiptHash", proposal["receiptHash"] }, { "policy", proposal["policy"] }, { "decision", B.Decision(decision) },
                { "observation", observation is null ? BsonNull.Value : B.Observation(observation) },
                { "capacityBefore", source.Capacity.State.DeepClone() }, { "capacityAfter", after },
                { "allocationId", allocationId }, { "binding", binding },
                { "people", new BsonArray(active.People!.Evidence.Where(person => person.UserId == actor.UserId || person.UserId == proposal["actorId"].AsInt64)
                    .OrderBy(person => person.UserId).Select(R.Person)) } };
            B.Metadata(row, B.DecideOperation, auditId, actor, metadata, body, at);
            await Rows(B.Decisions).InsertOneAsync(tx, row, cancellationToken: token);
            if (decision == ProcurementQualityAcceptanceDecision.Approve) {
                var allocation = new BsonDocument { { "_id", allocationId }, { "schemaVersion", 1 }, { "proposalId", proposalId },
                    { "decisionId", id }, { "proposalHash", proposal["snapshotHash"] }, { "receiptId", input.ReceiptId },
                    { "receiptHash", source.Receipt["snapshotHash"] }, { "packages", input.AcceptedPackages },
                    { "sequence", after["version"] }, { "capacityBefore", source.Capacity.State.DeepClone() },
                    { "capacityAfter", after.DeepClone() }, { "recordedAt", at.UtcDateTime } };
                allocation.Add("snapshotHash", B.Hash(allocation)); await Rows(B.Allocations).InsertOneAsync(tx, allocation, cancellationToken: token);
                var head = ProcurementQualityAcceptanceCapacityReader.Head(after, source.Receipt, at, source.Capacity.Head);
                if (source.Capacity.Head is null) await Rows(B.ReceiptHeads).InsertOneAsync(tx, head, cancellationToken: token);
                else if ((await Rows(B.ReceiptHeads).ReplaceOneAsync(tx, source.Capacity.Head, head, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
            }
            await EffectsAsync(tx, row, token); active.Proposal = proposal; return B.Result(row);
        }, ct, async (_, tx, token) => {
            var saved = await records.ForKeyAsync(tx, actor.UserId, B.DecideOperation, metadata.IdempotencyKey, token) ?? throw B.Broken();
            ProcurementQualityAcceptanceRecords.Equal(saved["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason));
            Current(context ?? throw B.Broken());
        }, validateCurrent: () => {
            var active = context ?? throw B.Broken(); Current(active);
            if (active.Live && maker is not null && makerSnapshot is not null) RequireMaker(maker, makerSnapshot, active.Location);
        });
    }
}
