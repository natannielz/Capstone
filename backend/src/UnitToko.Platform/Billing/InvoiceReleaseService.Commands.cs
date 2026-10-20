using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using B = UnitToko.Platform.Billing.InvoiceReleaseBson;

namespace UnitToko.Platform.Billing;

public sealed partial class InvoiceReleaseService
{
    private static void Expected(Context context, InvoiceReleaseInput input) {
        if ((context.History?.Head["revision"].AsInt64 ?? 0) != input.ExpectedReleaseRevision || context.History?.Latest["action"] == "propose") throw B.Changed();
    }
    private static BsonDocument Pending(Context context, long proposalId, long revision) {
        var proposal = context.History?.Latest ?? throw B.Changed();
        if (proposal["action"] != "propose" || proposal["_id"] != proposalId || proposal["revision"] != revision) throw B.Changed();
        return proposal;
    }
    private async Task CaptureAsync(Context context, InvoiceReleaseInput input, CancellationToken ct) {
        context.Capture = await sources.CaptureAsync(context.Actor, context.Authority, context.Tx, input, Permissions.InvoiceRead, ct);
        InvoiceReleaseRecords.Equal(InvoiceBson.Sources(context.Receipts), InvoiceBson.Sources(context.Capture.Receipts));
    }
    public async Task<InvoiceReleasePreview> PreviewAsync(ActorContext actor, InvoiceReleaseInput input, CancellationToken ct = default) {
        input = InvoiceReleaseRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, input.Receipts, B.ProposePermission, token); Expected(context, input);
            await CaptureAsync(context, input, token); await PeopleAsync(context, null, null, token);
            var binding = B.ProposalBinding(actor, input, context.Capture!.Snapshot); await FenceAsync(context, token);
            return Preview(binding, input, context.Capture.Snapshot, context.History?.Head["revision"].AsInt64 ?? 0);
        }, ct);
    }
    public async Task<InvoiceReleasePreview> PreviewDecisionAsync(ActorContext actor, long proposalId, long revision,
        InvoiceReleaseDecision decision, CancellationToken ct = default) {
        if (proposalId <= 0 || revision <= 0 || !Enum.IsDefined(decision)) throw InvoiceReleaseRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await RecordContextAsync(actor, authority, tx, proposalId, B.ApprovePermission, token); var proposal = Pending(context, proposalId, revision);
            var input = B.Input(proposal["input"]);
            if (decision == InvoiceReleaseDecision.Approve) {
                await CaptureAsync(context, input, token); InvoiceReleaseRecords.Equal(context.Capture!.Snapshot, proposal["source"]);
            }
            await PeopleAsync(context, proposal, null, token); var source = context.Capture?.Snapshot ?? proposal["source"].AsBsonDocument;
            var binding = B.DecisionBinding(actor, proposal, decision, source); await FenceAsync(context, token);
            return Preview(binding, input, source, revision);
        }, ct);
    }
    private async Task EffectsAsync(Context context, BsonDocument row, CancellationToken ct) {
        await records.Rows("auditEvents").InsertOneAsync(context.Tx, B.Audit(row), cancellationToken: ct);
        await records.Rows("outboxEvents").InsertOneAsync(context.Tx, new BsonDocument {
            { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", row["operation"] }, { "sourceKey", B.SourceKey(row) },
            { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "payload", B.Result(row) },
            { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 }
        }, cancellationToken: ct);
    }
    private async Task AppendAsync(Context context, BsonDocument row, CancellationToken ct) {
        var old = context.History?.Head; var now = row["recordedAt"];
        if (old is not null && old["updatedAt"].CompareTo(now) > 0) throw B.Changed();
        if (old is null) await records.Rows(B.Heads).InsertOneAsync(context.Tx, new BsonDocument {
            { "_id", row["releaseId"] }, { "schemaVersion", 1 }, { "selectionKey", row["selectionKey"] }, { "receipts", row["input"]["receipts"].DeepClone() },
            { "scopes", row["scopes"].DeepClone() }, { "revision", row["revision"] }, { "eventId", row["_id"] }, { "eventHash", row["recordHash"] },
            { "state", row["action"] }, { "fence", 0L }, { "createdAt", now }, { "updatedAt", now }
        }, cancellationToken: ct);
        else if ((await records.Rows(B.Heads).UpdateOneAsync(context.Tx, old, Builders<BsonDocument>.Update
            .Set("revision", row["revision"]).Set("eventId", row["_id"]).Set("eventHash", row["recordHash"]).Set("state", row["action"])
            .Set("updatedAt", now).Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        await records.Rows(B.Events).InsertOneAsync(context.Tx, row, cancellationToken: ct); await EffectsAsync(context, row, ct);
    }
    private BsonDocument Event(Context context, long id, long releaseId, long proposalId, string action, InvoiceReleaseInput input,
        BsonDocument source, string binding, long auditId, AuthorizationCommandMetadata metadata, object body) {
        var previous = context.History?.Latest; var priorRevision = previous?["revision"].AsInt64 ?? 0;
        if (priorRevision >= long.MaxValue - 1) throw B.Changed();
        var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "releaseId", releaseId }, { "proposalId", proposalId },
            { "selectionKey", B.SelectionKey(input.Receipts) }, { "scopes", new BsonArray(context.Receipts.Select(source => ShipmentPreparationBson.Scope(source.Resource))) },
            { "revision", checked(priorRevision + 1) }, { "previousRevision", priorRevision }, { "previousHash", previous?["recordHash"] ?? BsonNull.Value },
            { "action", action }, { "input", B.Input(input) }, { "source", source.DeepClone() }, { "binding", binding },
            { "people", new BsonArray(context.People!.Evidence.OrderBy(person => person.UserId).Select(B.Person)) } };
        B.Metadata(row, context.Actor, metadata, auditId, Now, body); return row;
    }
    private async Task<InvoiceReleaseCommandResult> CommandAsync(ActorContext actor, string action, AuthorizationCommandMetadata metadata, object body,
        Func<AuthorizationSnapshot, IClientSessionHandle, CancellationToken, Task<Context>> capture,
        Func<Context, CancellationToken, Task<BsonDocument>> write, CancellationToken ct) {
        var permission = B.Permission(action); var operation = B.Prefix + action; Context? context = null; BsonDocument? saved = null;
        var result = await authorization.ExecuteCommandAsync(actor, operation, metadata, body, async (authority, tx, token) => {
            AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, permission, clock.UtcNow));
            // Scope is checked from a minimal route before history or original result is disclosed.
            var route = await records.Rows(B.Events).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "operation", operation }, { "requestKey", metadata.IdempotencyKey } })
                .Project(new BsonDocument("_id", 1)).SingleOrDefaultAsync(token);
            if (route is null) {
                _ = await records.ForKeyAsync(tx, actor.UserId, operation, metadata.IdempotencyKey, token);
                context = await capture(authority, tx, token); saved = null;
            } else {
                context = await RecordContextAsync(actor, authority, tx, route["_id"].AsInt64, permission, token);
                saved = await records.ForKeyAsync(tx, actor.UserId, operation, metadata.IdempotencyKey, token) ?? throw B.Broken();
                InvoiceReleaseRecords.Equal(saved["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason));
                var proposal = context.History!.Events.Single(row => row["_id"] == saved["proposalId"]);
                await PeopleAsync(context, proposal, action == "propose" ? null : saved, token);
            }
            var query = Require(context); var proposalForApproval = action == "propose" ? null : saved is null ? context.History!.Latest :
                context.History!.Events.Single(row => row["_id"] == saved["proposalId"]);
            return new(new(true, "allowed", [.. query.Clauses.Where(clause => context.Receipts.Any(source => clause.Matches(source.Resource))).Select(clause => clause.GrantId).Distinct()]),
                [.. context.Receipts.SelectMany(source => ScopePolicy.RequiredNodes(source.Resource, authority.Directory)).Distinct()], permission,
                new(context.Receipts[0].Resource, proposalForApproval is null ? null : new ApprovalRequest(proposalForApproval["revision"].AsInt64,
                    proposalForApproval["revision"].AsInt64, ProposedBy: proposalForApproval["actorId"].AsInt64)));
        }, (_, token) => write(context ?? throw B.Broken(), token), ct, async (_, tx, token) => {
            var current = context ?? throw B.Broken(); var row = await records.ForKeyAsync(tx, actor.UserId, operation, metadata.IdempotencyKey, token) ?? throw B.Broken();
            InvoiceReleaseRecords.Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason));
            current.History = await records.ReadAsync(tx, row["releaseId"].AsInt64, token);
            await FenceAsync(current, token);
        }, validateCurrent: () => Current(context ?? throw B.Broken()));
        return Result(result);
    }
    public async Task<InvoiceReleaseCommandResult> ProposeAsync(ActorContext actor, InvoiceReleaseInput input, string binding,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = InvoiceReleaseRules.Copy(input); B.RequireHash(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Events, ct)).Value; var headId = (await ids.NextAsync(B.Heads, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.ProposeBody(input, binding);
        return await CommandAsync(actor, "propose", metadata, body, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, input.Receipts, B.ProposePermission, token); Expected(context, input);
            await CaptureAsync(context, input, token); InvoiceReleaseRecords.Equal(binding, B.ProposalBinding(actor, input, context.Capture!.Snapshot));
            await PeopleAsync(context, null, null, token); return context;
        }, async (context, token) => {
            Expected(context, input); Current(context);
            var row = Event(context, id, context.History?.Head["_id"].AsInt64 ?? headId, id, "propose", input, context.Capture!.Snapshot, binding, audit, metadata, body);
            await AppendAsync(context, row, token); return B.Result(row);
        }, ct);
    }
    public async Task<InvoiceReleaseCommandResult> DecideAsync(ActorContext actor, long proposalId, long revision, InvoiceReleaseDecision decision,
        string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (proposalId <= 0 || revision <= 0 || !Enum.IsDefined(decision)) throw InvoiceReleaseRules.Invalid(); B.RequireHash(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Events, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value;
        var action = B.Decision(decision); var body = B.DecideBody(proposalId, revision, decision, binding);
        return await CommandAsync(actor, action, metadata, body, async (authority, tx, token) => {
            var context = await RecordContextAsync(actor, authority, tx, proposalId, B.ApprovePermission, token); var proposal = Pending(context, proposalId, revision);
            if (decision == InvoiceReleaseDecision.Approve) {
                await CaptureAsync(context, B.Input(proposal["input"]), token); InvoiceReleaseRecords.Equal(context.Capture!.Snapshot, proposal["source"]);
            }
            InvoiceReleaseRecords.Equal(binding, B.DecisionBinding(actor, proposal, decision, context.Capture?.Snapshot ?? proposal["source"].AsBsonDocument));
            await PeopleAsync(context, proposal, null, token); return context;
        }, async (context, token) => {
            var proposal = Pending(context, proposalId, revision); Current(context);
            var row = Event(context, id, context.History!.Head["_id"].AsInt64, proposalId, action, B.Input(proposal["input"]),
                context.Capture?.Snapshot ?? proposal["source"].AsBsonDocument, binding, audit, metadata, body);
            await AppendAsync(context, row, token); return B.Result(row);
        }, ct);
    }
}
