using MongoDB.Bson;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Inventory.PackConversionRuleBson;

namespace UnitToko.Platform.Inventory;

public sealed partial class PackConversionRuleService
{
    public async Task<BsonDocument> PreviewRuleAsync(ActorContext actor, PackConversionRuleInput input, CancellationToken ct = default) {
        input = PackConversionRulePolicy.Copy(input); await PackConversionRuleStorage.RequireAsync(database, ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await PrepareAsync(actor, authority, tx, input, token);
            var quantity = PackConversionRuleCatalogue.Quantities(input, context.Catalogue ?? throw B.Broken(), 1, 1);
            var pair = context.Catalogue ?? throw B.Broken();
            var result = new BsonDocument { { "input", B.Input(input) }, { "catalog", pair }, { "binding", B.Binding(input, pair) },
                { "sourcePackages", quantity.SourcePackages }, { "destinationPackages", quantity.DestinationPackages }, { "baseUnits", quantity.BaseUnits },
                { "physicalConversionAvailable", false }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, true, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> ProposeRuleAsync(ActorContext actor, PackConversionRuleInput input, string binding,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = PackConversionRulePolicy.Copy(input); Binding(binding); Metadata(metadata); await PackConversionRuleStorage.RequireAsync(database, ct);
        var id = (await ids.NextAsync(B.Proposals, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.ProposeBody(input, binding); Context? context = null;
        return await authorization.ExecuteCommandAsync(actor, B.Propose, metadata, body, async (authority, tx, token) => {
            context = new(actor, authority, tx, input.Location); _ = Current(context);
            var existing = await records.ForKeyAsync(tx, actor.UserId, B.Propose, metadata.IdempotencyKey, token);
            if (existing is null) {
                context = await PrepareAsync(actor, authority, tx, input, token);
                if (binding != B.Binding(input, context.Catalogue ?? throw B.Broken())) throw B.Changed();
            } else await PeopleAsync(context, null, token);
            return Command(context);
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); await FenceAsync(active, true, token);
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "kind", "pack.conversion.rule.proposal" }, { "revision", 1L },
                { "input", B.Input(input) }, { "catalog", active.Catalogue ?? throw B.Broken() }, { "binding", binding },
                { "people", new BsonArray((active.People ?? throw B.Broken()).Evidence.Select(PackConversionRuleRecords.Person)) } };
            B.Metadata(row, B.Propose, audit, actor, metadata, body, Now);
            await Rows(B.Proposals).InsertOneAsync(tx, row, cancellationToken: token); await EffectsAsync(tx, row, token); return B.Result(row);
        }, ct, async (_, tx, token) => {
            var row = await records.ForKeyAsync(tx, actor.UserId, B.Propose, metadata.IdempotencyKey, token) ?? throw B.Broken();
            if (row["bodyHash"] != UnitToko.Platform.Authorization.AuthorizationStore.CommandHash(body, metadata.Reason)) throw B.Broken();
            await FenceAsync(context ?? throw B.Broken(), false, token);
        }, validateCurrent: () => _ = Current(context ?? throw B.Broken()));
    }
    public async Task<BsonDocument> PreviewDecisionAsync(ActorContext actor, long proposalId, PackConversionRuleDecision decision, CancellationToken ct = default) {
        if (proposalId <= 0 || !Enum.IsDefined(decision)) throw PackConversionRulePolicy.Invalid(); await PackConversionRuleStorage.RequireAsync(database, ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var proposal = await AuthorizedProposalAsync(actor, authority, tx, proposalId, token);
            if (await records.DecisionAsync(tx, proposal, token) is not null) throw B.Changed();
            var originalInput = B.Input(proposal["input"].AsBsonDocument);
            var context = decision == PackConversionRuleDecision.Approve ? await PrepareAsync(actor, authority, tx, originalInput, token)
                : new Context(actor, authority, tx, originalInput.Location);
            await PeopleAsync(context, proposal, token);
            if (decision == PackConversionRuleDecision.Approve && context.Catalogue != proposal["catalog"]) throw B.Changed();
            var result = new BsonDocument { { "proposal", proposal }, { "decision", decision == PackConversionRuleDecision.Approve ? "approve" : "reject" },
                { "binding", B.DecisionBinding(proposal, decision) }, { "canReview", true }, { "physicalConversionAvailable", false } };
            await FenceAsync(context, decision == PackConversionRuleDecision.Approve, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> DecideRuleAsync(ActorContext actor, long proposalId, long expectedRevision, PackConversionRuleDecision decision,
        string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (proposalId <= 0 || expectedRevision != 1 || !Enum.IsDefined(decision)) throw PackConversionRulePolicy.Invalid(); Binding(binding); Metadata(metadata);
        await PackConversionRuleStorage.RequireAsync(database, ct); var id = (await ids.NextAsync(B.Decisions, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.DecideBody(proposalId, expectedRevision, decision, binding); Context? context = null; BsonDocument? proposal = null;
        return await authorization.ExecuteCommandAsync(actor, B.Decide, metadata, body, async (authority, tx, token) => {
            proposal = await AuthorizedProposalAsync(actor, authority, tx, proposalId, token);
            var prior = await records.ForKeyAsync(tx, actor.UserId, B.Decide, metadata.IdempotencyKey, token);
            if (prior is null) {
                if (await records.DecisionAsync(tx, proposal, token) is not null) throw B.Changed();
                var originalInput = B.Input(proposal["input"].AsBsonDocument);
                context = decision == PackConversionRuleDecision.Approve ? await PrepareAsync(actor, authority, tx, originalInput, token)
                    : new Context(actor, authority, tx, originalInput.Location);
                if (decision == PackConversionRuleDecision.Approve && context.Catalogue != proposal["catalog"] || binding != B.DecisionBinding(proposal, decision)) throw B.Changed();
            } else context = new(actor, authority, tx, B.Input(proposal["input"].AsBsonDocument).Location);
            await PeopleAsync(context, proposal, token); return Command(context);
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); var original = proposal ?? throw B.Broken(); await FenceAsync(active, decision == PackConversionRuleDecision.Approve, token);
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "kind", "pack.conversion.rule.decision" }, { "proposalId", proposalId },
                { "proposalHash", original["snapshotHash"] }, { "decision", decision == PackConversionRuleDecision.Approve ? "approve" : "reject" },
                { "ruleId", decision == PackConversionRuleDecision.Approve ? new BsonInt64(proposalId) : BsonNull.Value }, { "binding", binding },
                { "people", new BsonArray((active.People ?? throw B.Broken()).Evidence.OrderBy(person => person.UserId).Select(PackConversionRuleRecords.Person)) } };
            B.Metadata(row, B.Decide, audit, actor, metadata, body, Now); await Rows(B.Decisions).InsertOneAsync(tx, row, cancellationToken: token);
            if (decision == PackConversionRuleDecision.Approve) await Rows(B.Rules).InsertOneAsync(tx, B.Rule(original, row), cancellationToken: token);
            await EffectsAsync(tx, row, token); return B.Result(row);
        }, ct, async (_, tx, token) => {
            var row = await records.ForKeyAsync(tx, actor.UserId, B.Decide, metadata.IdempotencyKey, token) ?? throw B.Broken();
            if (row["bodyHash"] != UnitToko.Platform.Authorization.AuthorizationStore.CommandHash(body, metadata.Reason)) throw B.Broken();
            await FenceAsync(context ?? throw B.Broken(), false, token);
        }, validateCurrent: () => _ = Current(context ?? throw B.Broken()));
    }
}
