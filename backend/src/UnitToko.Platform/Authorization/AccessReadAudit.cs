using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Platform.Authorization;

public sealed partial class AccessAdministrationService
{
    private sealed record AuditedValue<T>(T Value, int Count, ImmutableArray<GrantScope> Scopes);
    private async Task<T> AuditedReadAsync<T>(ActorContext actor, string operation, AuthorizationReadMetadata? audit,
        BsonDocument selection, Func<AuthorizationSnapshot, IClientSessionHandle, CancellationToken, Task<AuditedValue<T>>> read,
        CancellationToken cancellationToken)
    {
        var metadata = audit ?? new AuthorizationReadMetadata("access-review", "internal-service");
        ValidateReadMetadata(metadata);
        var id = (await ids.NextAsync("authorizationReadEvents", cancellationToken)).Value;
        return await store.ReadAsync(actor, async (snapshot, session, token) =>
        {
            var result = await read(snapshot, session, token);
            await WriteReadAuditAsync(id, actor, snapshot, operation, metadata, selection, result.Count, result.Scopes, session, token);
            return result.Value;
        }, cancellationToken);
    }
    private Task WriteReadAuditAsync(long id, ActorContext actor, AuthorizationSnapshot snapshot, string operation,
        AuthorizationReadMetadata audit, BsonDocument selection, int resultCount, ImmutableArray<GrantScope> scopes,
        IClientSessionHandle session, CancellationToken token) =>
        store.RecordPrivilegedReadAsync(id, actor, snapshot, operation, audit, selection, resultCount, scopes, session, token);
    internal static void ValidateReadMetadata(AuthorizationReadMetadata audit)
    {
        if (audit.Purpose is not ("access-review" or "account-support" or "security-investigation" or "operation-recovery") ||
            string.IsNullOrWhiteSpace(audit.CorrelationId) || audit.CorrelationId.Length > 128 || audit.CorrelationId.Any(char.IsControl))
            throw new DomainValidationException("invalid_read_purpose", "An approved purpose and correlation ID are required");
    }
    private ImmutableArray<GrantScope> ReadAuthorityScopes(ActorContext actor, AuthorizationSnapshot snapshot) =>
        AdministrativeRead(actor, snapshot) ? [new(GrantScopeKind.System)] : AdministrationScopes(actor, snapshot);

    public Task<AccessHistoryPage> ListHistoryAsync(ActorContext actor, AuthorizationReadMetadata audit, int offset, int limit,
        long? subjectId = null, CancellationToken cancellationToken = default)
    {
        AuthorizationStore.ValidatePage(offset, limit); ValidateReadMetadata(audit);
        if (subjectId is <= 0 || audit.Purpose == "operation-recovery") throw new DomainValidationException("invalid_history_query", "History requires an access review, support or security purpose");
        var selection = new BsonDocument { { "offset", offset }, { "limit", limit } };
        if (subjectId.HasValue) selection.Add("subjectId", subjectId.Value);
        return AuditedReadAsync(actor, "access.history.read", audit, selection, async (snapshot, session, token) =>
        {
            var global = ScopePolicy.Compile(actor, snapshot, Permissions.AccessAuditRead, clock.UtcNow).Matches(Administrative);
            var organizationRead = ScopePolicy.Compile(actor, snapshot, Permissions.TechnicalConfigure, clock.UtcNow).Matches(Administrative) ||
                ScopePolicy.Compile(actor, snapshot, "organization.approve", clock.UtcNow).Matches(Administrative);
            var administrativeScopes = AdministrationScopes(actor, snapshot);
            var scopes = global ? ImmutableArray.Create(new GrantScope(GrantScopeKind.System)) : administrativeScopes;
            if (!global && organizationRead) scopes = scopes.Add(new(GrantScopeKind.System)).Distinct().ToImmutableArray();
            if (scopes.IsEmpty || !global && audit.Purpose != "access-review") throw new AuthorizationDeniedException("permission_denied");
            var filters = administrativeScopes.Select(scope => HistoryScopeFilter(scope, snapshot.Directory)).ToList();
            if (organizationRead) filters.Add(F.In("operation", OrganizationHistoryOperations));
            var filter = global ? F.Empty : F.Or(filters);
            if (subjectId.HasValue) filter &= F.Eq("subjectId", subjectId.Value);
            var collection = Collection("authorizationHistory");
            var count = await collection.CountDocumentsAsync(session, filter, cancellationToken: token);
            var projection = Builders<BsonDocument>.Projection.Include("_id").Include("actorId").Include("subjectId").Include("operation")
                .Include("createdAt").Include("reason").Include("scope").Include("before._id").Include("before.version").Include("before.scope")
                .Include("before.proposal._id").Include("before.proposal.version")
                .Include("after._id").Include("after.version").Include("after.status").Include("after.resolutionKind").Include("after.scope")
                .Include("after.proposal._id").Include("after.proposal.version").Include("after.proposal.status");
            var documents = await collection.Find(session, filter).Sort(new BsonDocument { { "createdAt", -1 }, { "_id", -1 } })
                .Skip(offset).Limit(limit).Project<BsonDocument>(projection).ToListAsync(token);
            var structuralDirectory = new ScopeDirectory(snapshot.Directory.Nodes.Select(node => node with { Status = AuthorizationStatus.Active }).ToImmutableArray());
            var entries = documents.Select(document =>
            {
                var metadata = HistoryResourceMetadata(document);
                return new AccessHistoryEntry(document["_id"].AsInt64, document["operation"].AsString, document["actorId"].AsInt64,
                    document["subjectId"].AsInt64, SafeHistoryScope(document, structuralDirectory), new(document["createdAt"].ToUniversalTime(), TimeSpan.Zero),
                    document["reason"].AsString, metadata.Id, metadata.BeforeVersion, metadata.AfterVersion, metadata.Status);
            }).ToImmutableArray();
            return new AuditedValue<AccessHistoryPage>(new(count, entries), entries.Length, scopes);
        }, cancellationToken);
    }
    private static FilterDefinition<BsonDocument> HistoryScopeFilter(GrantScope scope, ScopeDirectory directory)
    {
        var legacy = F.In("operation", AccessScopeHistoryOperations);
        return (CanonicalHistoryScope("scope", directory) & ScopeContainmentFilter(scope, "scope")) |
            (legacy & F.Exists("scope", false) & CanonicalHistoryScope("after.scope", directory) & ScopeContainmentFilter(scope, "after.scope")) |
            (legacy & F.Exists("scope", false) & F.Exists("after.scope", false) & CanonicalHistoryScope("before.scope", directory) & ScopeContainmentFilter(scope, "before.scope"));
    }
    private static GrantScope HistoryScope(BsonDocument document)
    {
        if (document.TryGetValue("scope", out var scope)) return AuthorizationBson.Scope(scope.AsBsonDocument);
        foreach (var name in new[] { "after", "before" })
            if (document.TryGetValue(name, out var state) && state.IsBsonDocument && state.AsBsonDocument.TryGetValue("scope", out scope))
                return AuthorizationBson.Scope(scope.AsBsonDocument);
        return new(GrantScopeKind.System);
    }
    private static long? OptionalLong(BsonDocument document, string field) => document.TryGetValue(field, out var value) && value.IsInt64 ? value.AsInt64 : null;
}
