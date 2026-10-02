using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Accounting.AccountingBookSetupBson;

namespace UnitToko.Platform.Accounting;

public sealed partial class AccountingBookSetupService
{
    public async Task<BsonDocument> PreviewAsync(ActorContext actor, AccountingBookSetupInput input, CancellationToken ct = default) {
        input = AccountingBookSetupRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, input.Key, Permissions.AccountingBookPropose, token);
            await RequireUnconfiguredAsync(context, token); await PeopleAsync(context, null, null, token);
            var result = new BsonDocument { { "input", B.Input(input) }, { "binding", B.Binding(actor.UserId, actor.GrantVersion, input, context.Sources) }, { "observedAt", Now.UtcDateTime } };
            foreach (var name in B.SourceNames) result.Add(name, B.Label(context.Sources[name]));
            await FenceAsync(context, token); return result;
        }, ct);
    }
    private async Task EffectsAsync(Context context, BsonDocument row, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(context.Tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(context.Tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
            { "sourceKey", B.SourceKey(row) }, { "kind", row["operation"] }, { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "key", row["key"] },
            { "payload", B.Result(row) }, { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
    private Task<BsonDocument> CommandAsync(ActorContext actor, string operation, AuthorizationCommandMetadata metadata, object body,
        Func<AuthorizationSnapshot, IClientSessionHandle, BsonDocument?, CancellationToken, Task<Context>> capture,
        Func<Context, CancellationToken, Task<BsonDocument>> write, CancellationToken ct) {
        Context? selected = null;
        return authorization.ExecuteCommandAsync(actor, operation, metadata, body, async (authority, tx, token) => {
            var permission = B.Permission(operation); _ = Query(actor, authority, permission);
            var saved = await ForKeyAsync(actor, authority, tx, operation, metadata.IdempotencyKey, token);
            selected = await capture(authority, tx, saved, token); var query = Require(selected);
            return new(new(true, "allowed", query.Clauses.Where(clause => clause.Matches(selected.Resource)).Select(clause => clause.GrantId).Distinct().ToImmutableArray()),
                ScopePolicy.RequiredNodes(selected.Resource, authority.Directory), permission,
                new(selected.Resource, permission == Permissions.AccountingBookApprove ? new ApprovalRequest(1, 1, ProposedBy: selected.Proposal!["actorId"].AsInt64) : null));
        }, (_, token) => write(selected ?? throw B.Broken(), token), ct, async (_, tx, token) => {
            var context = selected ?? throw B.Broken(); var row = await records.ForKeyAsync(tx, actor.UserId, operation, metadata.IdempotencyKey, token) ?? throw B.Broken();
            Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason));
            if (row.GetValue("bookId", BsonNull.Value).IsInt64) context.BookId = row["bookId"].AsInt64;
            var proposal = await records.ProposalAsync(tx, row["proposalId"].AsInt64, token);
            BindPeople(context, proposal, operation == B.Prefix + "propose" ? null : row);
            await PeopleAsync(context, operation == B.Prefix + "propose" ? null : actor.UserId, operation == B.Prefix + "propose" ? null : proposal["actorId"].AsInt64, token);
            await FenceAsync(context, token);
        }, validateCurrent: () => Current(selected ?? throw B.Broken()));
    }
    public async Task<BsonDocument> ProposeAsync(ActorContext actor, AccountingBookSetupInput input, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = AccountingBookSetupRules.Copy(input); B.RequireBinding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Proposals, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value; var body = B.ProposeBody(input, binding);
        return await CommandAsync(actor, B.Prefix + "propose", metadata, body, async (authority, tx, saved, token) => {
            var context = await ContextAsync(actor, authority, tx, input.Key, Permissions.AccountingBookPropose, token);
            if (saved is null) { await RequireUnconfiguredAsync(context, token); Equal(binding, B.Binding(actor.UserId, actor.GrantVersion, input, context.Sources)); }
            else BindPeople(context, saved);
            await PeopleAsync(context, null, null, token); return context;
        }, async (context, token) => {
            await RequireUnconfiguredAsync(context, token); Equal(binding, B.Binding(actor.UserId, actor.GrantVersion, input, context.Sources)); Current(context); var now = Now;
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "proposalId", id }, { "version", 1L },
                { "key", B.Key(input.Key) }, { "sources", context.Sources.DeepClone() }, { "input", B.Input(input) }, { "binding", binding },
                { "people", new BsonArray(context.People!.Evidence.OrderBy(p => p.UserId).Select(B.Person)) } };
            B.Metadata(row, B.Prefix + "propose", auditId, actor, metadata, body, now);
            await Rows(B.Proposals).InsertOneAsync(context.Tx, row, cancellationToken: token); await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
    private async Task<Context> ProposalContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long proposalId, string permission, CancellationToken ct) {
        _ = Query(actor, authority, permission);
        var route = await Rows(B.Proposals).Find(tx, new BsonDocument("_id", proposalId)).Project(new BsonDocument("key", 1)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        var context = await ContextAsync(actor, authority, tx, B.Key(route["key"]), permission, ct);
        context.Proposal = await records.ProposalAsync(tx, proposalId, ct); BindPeople(context, context.Proposal); return context;
    }
    private async Task PendingAsync(Context context, AccountingBookSetupDecision decision, CancellationToken ct) {
        if (context.Proposal is null || await records.DecisionForAsync(context.Tx, context.Proposal, ct) is not null) throw B.Changed();
        if (decision == AccountingBookSetupDecision.Approve) {
            Equal(context.Proposal["sources"], context.Sources); await RequireUnconfiguredAsync(context, ct);
        }
    }
    public async Task<BsonDocument> PreviewDecisionAsync(ActorContext actor, long proposalId, AccountingBookSetupDecision decision, CancellationToken ct = default) {
        if (proposalId <= 0 || !Enum.IsDefined(decision)) throw AccountingBookSetupRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, Permissions.AccountingBookApprove, token); var proposal = context.Proposal!;
            await PendingAsync(context, decision, token); await PeopleAsync(context, actor.UserId, proposal["actorId"].AsInt64, token);
            var result = new BsonDocument { { "proposal", await ProposalViewAsync(context, proposal, token) }, { "decision", B.Decision(decision) },
                { "binding", B.DecisionBinding(actor.UserId, actor.GrantVersion, proposal, decision, context.Sources) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> DecideAsync(ActorContext actor, long proposalId, long expectedVersion, AccountingBookSetupDecision decision, string binding,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (proposalId <= 0 || expectedVersion != 1 || !Enum.IsDefined(decision)) throw AccountingBookSetupRules.Invalid();
        B.RequireBinding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Decisions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var operation = B.Prefix + B.Decision(decision); var body = B.DecideBody(proposalId, expectedVersion, decision, binding);
        return await CommandAsync(actor, operation, metadata, body, async (authority, tx, saved, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, Permissions.AccountingBookApprove, token);
            if (saved is null) { await PendingAsync(context, decision, token); Equal(binding, B.DecisionBinding(actor.UserId, actor.GrantVersion, context.Proposal!, decision, context.Sources)); }
            else BindPeople(context, context.Proposal!, saved);
            await PeopleAsync(context, actor.UserId, context.Proposal!["actorId"].AsInt64, token); return context;
        }, async (context, token) => {
            await PendingAsync(context, decision, token); var proposal = context.Proposal!;
            Equal(binding, B.DecisionBinding(actor.UserId, actor.GrantVersion, proposal, decision, context.Sources));
            BsonValue bookId = BsonNull.Value, chartId = BsonNull.Value, periodId = BsonNull.Value; var accounts = new BsonArray();
            if (decision == AccountingBookSetupDecision.Approve) {
                bookId = (await ids.NextAsync("accountingBooks", token)).Value; chartId = (await ids.NextAsync("accountingChartVersions", token)).Value;
                periodId = (await ids.NextAsync("periods", token)).Value; var input = B.Input(proposal["input"]); var accountIds = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var code in input.Mappings.Select(mapping => mapping.AccountCode).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                    accountIds.Add(code, (await ids.NextAsync("accountingAccounts", token)).Value);
                foreach (var mapping in input.Mappings) accounts.Add(new BsonDocument { { "mappingKey", mapping.MappingKey }, { "accountId", accountIds[mapping.AccountCode] }, { "accountCode", mapping.AccountCode }, { "active", true } });
            }
            Current(context); var now = Now; if (now.UtcDateTime < proposal["recordedAt"].ToUniversalTime()) throw B.Changed();
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "proposalId", proposalId }, { "version", 1L },
                { "key", proposal["key"].DeepClone() }, { "sources", proposal["sources"].DeepClone() }, { "proposalHash", proposal["snapshotHash"] },
                { "reviewSources", context.Sources.DeepClone() }, { "decision", B.Decision(decision) }, { "bookId", bookId }, { "chartId", chartId }, { "periodId", periodId },
                { "accounts", accounts }, { "binding", binding }, { "people", new BsonArray(context.People!.Evidence.OrderBy(p => p.UserId).Select(B.Person)) } };
            B.Metadata(row, operation, auditId, actor, metadata, body, now);
            if (decision == AccountingBookSetupDecision.Approve) {
                await Rows("accountingBooks").InsertOneAsync(context.Tx, B.Book(row), cancellationToken: token);
                await Rows("accountingChartVersions").InsertOneAsync(context.Tx, B.Chart(row), cancellationToken: token);
                await Rows("periods").InsertOneAsync(context.Tx, B.Period(row, proposal), cancellationToken: token);
            }
            await Rows(B.Decisions).InsertOneAsync(context.Tx, row, cancellationToken: token); await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
}
