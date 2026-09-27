using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Authorization;

public sealed partial class AccessAdministrationService
{
    private static readonly IReadOnlyDictionary<string, string[]> ReceiptPermissions = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["access.grant.propose"] = [Permissions.AccessPropose], ["access.grant.approve"] = [Permissions.AccessApprove],
        ["access.grant.revoke"] = [Permissions.AccessRevoke], ["access.membership.propose"] = [Permissions.MembershipPropose],
        ["access.membership.approve"] = [Permissions.MembershipApprove], ["access.membership.suspend"] = [Permissions.AccessRevoke],
        ["access.identity.suspend"] = [Permissions.AccessRevoke], ["access.scope.suspend"] = [Permissions.AccessRevoke],
        ["access.proposal.reject"] = [Permissions.AccessApprove, Permissions.MembershipApprove],
        ["access.proposal.withdraw"] = [Permissions.AccessPropose, Permissions.MembershipPropose],
        ["organization.propose"] = [Permissions.TechnicalConfigure], ["organization.withdraw"] = [Permissions.TechnicalConfigure],
        ["organization.approve"] = ["organization.approve"], ["organization.reject"] = ["organization.approve"],
        ["identity.invitation.propose"] = [Permissions.InviteManage, Permissions.MembershipPropose, Permissions.AccessPropose],
        ["identity.invitation.approve"] = [Permissions.MembershipApprove, Permissions.AccessApprove],
        ["identity.invitation.rejected"] = [Permissions.MembershipApprove],
        ["identity.invitation.withdrawn"] = [Permissions.InviteManage],
        ["identity.invitation.revoked"] = [Permissions.InviteManage, Permissions.AccessRevoke],
        ["identity.recovery.request"] = [Permissions.RecoveryInitiate]
    };
    private static readonly string ClosedReceiptHash = Hash("unit-toko/authorization-receipt-closed/v1");

    public Task<OperationReceipt> ReadOperationReceiptAsync(ActorContext actor, string operation, string key,
        AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        ValidateReceiptRequest(actor, operation, key); ValidateReadMetadata(audit);
        if (audit.Purpose != "operation-recovery") throw new DomainValidationException("invalid_read_purpose", "Receipt lookup requires the operation-recovery purpose");
        return AuditedReadAsync(actor, "access.receipt.read", audit, ReceiptSelection(operation, key), async (snapshot, session, token) =>
        {
            var queries = ReceiptAuthority(actor, snapshot, operation);
            var record = await Collection("idempotencyRecords").Find(session, ReceiptFilter(actor.UserId, operation, key)).FirstOrDefaultAsync(token);
            var scopes = await ReceiptScopesAsync(actor, snapshot, operation, record, queries, session, token);
            return new AuditedValue<OperationReceipt>(Receipt(operation, record), record is null ? 0 : 1, scopes);
        }, cancellationToken);
    }

    /// <summary>Serializes closure against the original command's unique actor/operation/key. Never calls committed work canceled.</summary>
    public async Task<OperationReceipt> ResolveOperationReceiptAsync(ActorContext actor, string operation, string key,
        AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        ValidateReceiptRequest(actor, operation, key); AuthorizationStore.ValidateMetadata(metadata);
        if (metadata.IdempotencyKey != key) throw new DomainValidationException("invalid_idempotency", "Resolution must use the original request key");
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        var readId = (await ids.NextAsync("authorizationReadEvents", cancellationToken)).Value;
        return await store.ReadAsync(actor, async (snapshot, session, token) =>
        {
            var queries = ReceiptAuthority(actor, snapshot, operation);
            var records = Collection("idempotencyRecords");
            var record = await records.Find(session, ReceiptFilter(actor.UserId, operation, key)).FirstOrDefaultAsync(token);
            var scopes = await ReceiptScopesAsync(actor, snapshot, operation, record, queries, session, token);
            await WriteReadAuditAsync(readId, actor, snapshot, "access.receipt.read", new("operation-recovery", metadata.CorrelationId),
                ReceiptSelection(operation, key), record is null ? 0 : 1, scopes, session, token);
            if (record is not null) return Receipt(operation, record);
            record = new BsonDocument
            {
                { "schemaVersion", CollectionMigrations.FoundationDocumentSchemaVersion }, { "actorId", actor.UserId },
                { "operation", operation }, { "key", key }, { "bodyHash", ClosedReceiptHash }, { "createdAt", clock.UtcNow.UtcDateTime },
                { "result", new BsonDocument("receiptState", "closed") }
            };
            await records.InsertOneAsync(session, record, cancellationToken: token);
            await AuditAsync(historyId, actor, actor.UserId, "access.receipt.closed", metadata, new(),
                new BsonDocument { { "operation", operation }, { "keyHash", Hash(key) }, { "receiptState", "closed" } }, session, token);
            return Receipt(operation, record);
        }, cancellationToken);
    }

    private ImmutableArray<AuthorizationQuery> ReceiptAuthority(ActorContext actor, AuthorizationSnapshot snapshot, string operation)
    {
        if (IsIdentityReceiptOperation(operation)) return IdentityReceiptAuthority(actor, snapshot, operation);
        var queries = ReceiptPermissions[operation].Select(permission => ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow)).Where(query => query.IsAllowed).ToImmutableArray();
        if (queries.IsEmpty) throw new AuthorizationDeniedException("permission_denied");
        if (operation is "access.identity.suspend" or "access.scope.suspend" || operation.StartsWith("organization.", StringComparison.Ordinal))
            queries = queries.Where(query => query.Matches(Administrative)).ToImmutableArray();
        if (queries.IsEmpty) throw new AuthorizationDeniedException("permission_denied");
        return queries;
    }
    private async Task<ImmutableArray<GrantScope>> ReceiptScopesAsync(ActorContext actor, AuthorizationSnapshot snapshot, string operation,
        BsonDocument? record, ImmutableArray<AuthorizationQuery> queries, IClientSessionHandle session, CancellationToken token)
    {
        if (IsIdentityReceiptOperation(operation))
            return await IdentityReceiptScopesAsync(actor, snapshot, operation, record, queries, session, token);
        var scopes = queries.SelectMany(query => query.Clauses.Select(clause => clause.Scope)).Distinct().ToImmutableArray();
        if (record is null || IsClosedReceipt(record)) return scopes;
        var result = SafeReceiptResult(operation, record["result"].AsBsonDocument);
        if (operation.StartsWith("organization.", StringComparison.Ordinal) || operation is "access.identity.suspend" or "access.scope.suspend")
            return scopes;
        string collection;
        if (operation is "access.grant.propose" or "access.membership.propose" or "access.proposal.reject" or "access.proposal.withdraw") collection = "authorizationProposals";
        else if (operation is "access.grant.approve" or "access.grant.revoke") collection = "authorizationGrants";
        else collection = "memberships";
        var target = await FindAsync(collection, result.Id, session, token);
        var targetScope = AuthorizationBson.Scope(target["scope"].AsBsonDocument);
        if (operation is "access.grant.propose" or "access.membership.propose" or "access.proposal.withdraw")
        {
            var permission = target["kind"].AsString == "grant" ? Permissions.AccessPropose : Permissions.MembershipPropose;
            if (target["requestedBy"].AsInt64 != actor.UserId || !queries.Any(query => query.Permission == permission && query.Matches(Administrative)))
                throw new AuthorizationDeniedException("resource_unavailable");
            return [new(GrantScopeKind.System)];
        }
        var selected = queries.SelectMany(query => query.Clauses).Where(clause => ScopePolicy.ContainsScope(clause.Scope, targetScope)).Select(clause => clause.Scope).Distinct().ToImmutableArray();
        if (selected.IsEmpty) throw new AuthorizationDeniedException("resource_unavailable");
        if (operation == "access.proposal.reject")
        {
            var authority = ProposalResolutionAuthority(actor, snapshot, target, false);
            if (!authority.Decision.Allowed) throw new AuthorizationDeniedException("resource_unavailable");
        }
        return selected;
    }
    private static OperationReceipt Receipt(string operation, BsonDocument? record) => record is null
        ? new(operation, "not_confirmed", null, null)
        : new(operation, IsClosedReceipt(record) ? "closed" : "committed", IsClosedReceipt(record) ? null : SafeReceiptResult(operation, record["result"].AsBsonDocument),
            new(record["createdAt"].ToUniversalTime(), TimeSpan.Zero));
    private static OperationReceiptResult SafeReceiptResult(string operation, BsonDocument result)
    {
        if (IsIdentityReceiptOperation(operation)) return SafeIdentityReceiptResult(operation, result);
        if (!result.TryGetValue("id", out var id) || !id.IsInt64 || id.AsInt64 <= 0 ||
            !result.TryGetValue("subjectId", out var subject) || !subject.IsInt64 || subject.AsInt64 <= 0 ||
            !result.TryGetValue("version", out var version) || !version.IsInt64 || version.AsInt64 <= 0 ||
            !result.TryGetValue("status", out var status) || !status.IsString || status.AsString is not ("pending" or "active" or "approved" or "rejected" or "withdrawn" or "revoked" or "suspended"))
            throw new PersistenceConflictException("receipt_unavailable", "The recorded outcome cannot be confirmed safely");
        return new(id.AsInt64, subject.AsInt64, version.AsInt64, status.AsString,
            result.TryGetValue("kind", out var kind) && kind.IsString && kind.AsString is "grant" or "membership" or "organization" ? kind.AsString : null,
            OptionalLong(result, "grantVersion"));
    }
    private static bool IsClosedReceipt(BsonDocument record) => record["bodyHash"].AsString == ClosedReceiptHash &&
        record["result"].AsBsonDocument.TryGetValue("receiptState", out var state) && state == "closed";
    private static void ValidateReceiptRequest(ActorContext actor, string operation, string key)
    {
        if (operation is null || !ReceiptPermissions.ContainsKey(operation)) throw new DomainValidationException("invalid_receipt_operation", "Unknown recoverable operation");
        IdempotencyScope.ForActor(actor, operation, key, ClosedReceiptHash).Validate();
    }
    private static FilterDefinition<BsonDocument> ReceiptFilter(long actorId, string operation, string key) => F.Eq("actorId", actorId) & F.Eq("operation", operation) & F.Eq("key", key);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static BsonDocument ReceiptSelection(string operation, string key) => new() { { "operation", operation }, { "keyHash", Hash(key) } };
}
