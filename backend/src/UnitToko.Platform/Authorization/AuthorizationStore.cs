using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Identity;

namespace UnitToko.Platform.Authorization;

public sealed class AuthorizationDeniedException(string code) : Exception("Access is not permitted")
{
    public string Code { get; } = code;
}
public sealed record AuthorizedPage(long Total, IReadOnlyList<BsonDocument> Items);
public sealed record AuthorizationTarget(ResourceScope Resource, ApprovalRequest? Approval = null);
internal sealed record CommandAuthorization(AuthorizationDecision Decision, ImmutableArray<ScopeNode> RequiredNodes,
    string? Permission = null, AuthorizationTarget? Target = null);

public sealed class AuthorizationStore(IMongoDatabase database, MongoUnitOfWork unitOfWork, IClock clock)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    public Task<AuthorizationSnapshot> ReadCurrentAsync(ActorContext actor, CancellationToken cancellationToken = default) =>
        ReadAsync(actor, (snapshot, _, _) => Task.FromResult(snapshot), cancellationToken);

    internal Task<T> ReadAsync<T>(ActorContext actor,
        Func<AuthorizationSnapshot, IClientSessionHandle, CancellationToken, Task<T>> read, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(async (session, token) => await read(await LoadAsync(actor, session, token), session, token), cancellationToken);

    internal async Task<AuthorizationSnapshot> LoadAsync(ActorContext actor, IClientSessionHandle session, CancellationToken token)
    {
        try { actor.Validate(); } catch (ArgumentException) { throw new AuthorizationDeniedException("invalid_actor"); }
        var identity = await database.GetCollection<BsonDocument>("identities").Find(session, IdentityFilter(actor)).FirstOrDefaultAsync(token);
        if (identity is null) throw new AuthorizationDeniedException("stale_or_inactive_actor");
        if (actor.Session is { } reference && !await database.GetCollection<BsonDocument>("identitySessions")
                .Find(session, SessionPersistence.LiveFilter(reference, actor.UserId, actor.ProviderIssuer,
                    actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).AnyAsync(token))
            throw new AuthorizationDeniedException("stale_or_inactive_session");
        var grants = await database.GetCollection<BsonDocument>("authorizationGrants").Find(session, F.Eq("userId", actor.UserId)).ToListAsync(token);
        var memberships = await database.GetCollection<BsonDocument>("memberships").Find(session, F.Eq("userId", actor.UserId)).ToListAsync(token);
        var nodes = await database.GetCollection<BsonDocument>("authorizationScopes").Find(session, F.Empty).ToListAsync(token);
        return new(actor.UserId, actor.GrantVersion, AuthorizationStatus.Active, grants.Select(AuthorizationBson.Grant).ToImmutableArray(),
            memberships.Select(AuthorizationBson.Membership).ToImmutableArray(), new(nodes.Select(AuthorizationBson.Node).ToImmutableArray()));
    }

    public Task<AuthorizedPage> QueryAsync(ActorContext actor, string permission, IMongoCollection<BsonDocument> collection,
        int offset, int limit, CancellationToken cancellationToken = default)
    {
        ValidateCollection(collection); ValidatePage(offset, limit);
        return ReadAsync(actor, async (snapshot, session, token) =>
        {
            var query = ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow);
            Require(query);
            var filter = ScopeQuery.Create(query);
            var count = await collection.CountDocumentsAsync(session, filter, cancellationToken: token);
            var items = await collection.Find(session, filter).Sort(new BsonDocument("_id", 1)).Skip(offset).Limit(limit).ToListAsync(token);
            return new AuthorizedPage(count, items);
        }, cancellationToken);
    }
    public Task<BsonDocument?> ReadAsync(ActorContext actor, string permission, IMongoCollection<BsonDocument> collection,
        long id, CancellationToken cancellationToken = default)
    {
        ValidateCollection(collection);
        if (id <= 0) throw new DomainValidationException("invalid_id", "Resource ID must be positive");
        return ReadAsync<BsonDocument?>(actor, async (snapshot, session, token) =>
        {
            var query = ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow); Require(query);
            return await collection.Find(session, F.And(ScopeQuery.Create(query), F.Eq("_id", id))).FirstOrDefaultAsync(token);
        }, cancellationToken);
    }

    /// <summary>The target loader must read server-owned resource scope and immutable maker/checker history in this session.</summary>
    public Task<BsonDocument> ExecuteAuthorizedAsync(ActorContext actor, string operation, string permission,
        AuthorizationCommandMetadata metadata, object requestBody,
        Func<IClientSessionHandle, CancellationToken, Task<AuthorizationTarget>> targetLoader,
        Func<IClientSessionHandle, CancellationToken, Task<BsonDocument>> businessWrite,
        CancellationToken cancellationToken = default) => ExecuteCommandAsync(actor, operation, metadata, requestBody,
            async (snapshot, session, token) =>
            {
                var target = await targetLoader(session, token);
                var query = ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow);
                var decision = query.Matches(target.Resource)
                    ? new AuthorizationDecision(true, "allowed", query.Clauses.Where(clause => clause.Matches(target.Resource)).Select(clause => clause.GrantId).ToImmutableArray())
                    : AuthorizationDecision.Deny(query.DenialCode ?? "scope_denied");
                return new(decision, ScopePolicy.RequiredNodes(target.Resource, snapshot.Directory), permission, target);
            }, async (session, token) =>
            {
                // A trusted same-body result has already passed current access checks. Only new work
                // re-evaluates workflow revision and maker/checker evidence, which the first write may change.
                var snapshot = await LoadAsync(actor, session, token);
                var target = await targetLoader(session, token);
                var decision = ScopePolicy.Authorize(actor, snapshot, permission, target.Resource, clock.UtcNow, target.Approval);
                if (!decision.Allowed) throw new AuthorizationDeniedException(decision.Code);
                return await businessWrite(session, token);
            }, cancellationToken);

    // An owning workflow can fence its full captured authority and time constraints after receipt persistence.
    // This hook also runs on replay; throwing aborts the transaction before a result can be returned.
    internal Task<BsonDocument> ExecuteCommandAsync(ActorContext actor, string operation, AuthorizationCommandMetadata metadata,
        object requestBody, Func<AuthorizationSnapshot, IClientSessionHandle, CancellationToken, Task<CommandAuthorization>> authorize,
        Func<IClientSessionHandle, CancellationToken, Task<BsonDocument>> businessWrite, CancellationToken cancellationToken,
        Func<AuthorizationSnapshot, IClientSessionHandle, CancellationToken, Task>? complete = null)
    {
        ValidateMetadata(metadata);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Request = requestBody, metadata.Reason })))).ToLowerInvariant();
        var scope = IdempotencyScope.ForActor(actor, operation, metadata.IdempotencyKey, hash); scope.Validate();
        return unitOfWork.ExecuteAsync(async (session, token) =>
        {
            var snapshot = await LoadAsync(actor, session, token);
            var authorization = await authorize(snapshot, session, token);
            if (!authorization.Decision.Allowed) throw new AuthorizationDeniedException(authorization.Decision.Code);
            var grantNodes = snapshot.Grants.Where(grant => authorization.Decision.GrantIds.Contains(grant.Id))
                .SelectMany(grant => ScopePolicy.RequiredNodes(grant.Scope, snapshot.Directory)
                    .Concat(snapshot.Memberships.Where(membership => membership.Id == grant.MembershipId)
                        .SelectMany(membership => ScopePolicy.RequiredNodes(membership.Scope, snapshot.Directory))));
            await FenceAsync(actor, authorization.RequiredNodes.Concat(grantNodes).Distinct(), session, token);
            var records = database.GetCollection<BsonDocument>("idempotencyRecords");
            var filter = F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", metadata.IdempotencyKey);
            var existing = await records.Find(session, filter).FirstOrDefaultAsync(token);
            if (existing is not null)
            {
                if (existing["bodyHash"].AsString != hash) throw new PersistenceConflictException("idempotency_key_reused", "Idempotency key was used with a different request");
                // Replay retains the originally approved participants, but their current person
                // authority and the caller's current permission/session must still be valid.
                var replayProof = await RequirePersonAuthorityAsync(actor, authorization, existing, session, token);
                await CompletePersonAuthorizationAsync(actor, snapshot, authorization, replayProof, session, token);
                if (complete is not null) await complete(snapshot, session, token);
                return existing["result"].AsBsonDocument.DeepClone().AsBsonDocument;
            }
            var personProof = await RequirePersonAuthorityAsync(actor, authorization, null, session, token);
            var receipt = new BsonDocument
            {
                { "schemaVersion", CollectionMigrations.FoundationDocumentSchemaVersion }, { "actorId", actor.UserId },
                { "operation", operation }, { "key", metadata.IdempotencyKey }, { "bodyHash", hash },
                { "createdAt", clock.UtcNow.UtcDateTime }, { "result", new BsonDocument() }
            };
            if (personProof is not null)
                receipt.Add(PersonApprovalReceipt.Field, PersonApprovalReceipt.Create(authorization.Permission!, authorization.Target!.Approval!));
            await records.InsertOneAsync(session, receipt, cancellationToken: token);
            var result = await businessWrite(session, token);
            await records.UpdateOneAsync(session, filter, Builders<BsonDocument>.Update.Set("result", result), cancellationToken: token);
            await CompletePersonAuthorizationAsync(actor, snapshot, authorization, personProof, session, token);
            if (complete is not null) await complete(snapshot, session, token);
            return result.DeepClone().AsBsonDocument;
        }, cancellationToken);
    }


    private async Task<PersonAuthorityProof?> RequirePersonAuthorityAsync(ActorContext actor,
        CommandAuthorization authorization, BsonDocument? receipt, IClientSessionHandle session, CancellationToken token)
    {
        var permission = authorization.Permission;
        if (permission is null || !PersonApprovalPolicy.RequiresEvidence(permission))
        {
            if (receipt?.Contains(PersonApprovalReceipt.Field) == true)
                throw new AuthorizationDeniedException("person_receipt_invalid");
            return null;
        }
        var approval = receipt is null ? authorization.Target?.Approval : PersonApprovalReceipt.Read(receipt, permission);
        if (approval is null) throw new AuthorizationDeniedException("approval_evidence_required");
        var accountDecision = ApprovalPolicy.Evaluate(actor.UserId, permission, approval);
        if (!accountDecision.Allowed) throw new AuthorizationDeniedException(accountDecision.Code);
        // Validate the exact private receipt format before allowing a new effect.
        if (receipt is null) PersonApprovalReceipt.Read(new BsonDocument(PersonApprovalReceipt.Field,
            PersonApprovalReceipt.Create(permission, approval)), permission);
        var accounts = PersonApprovalPolicy.RequiredAccounts(actor.UserId, permission, approval);
        if (accounts.IsDefaultOrEmpty) throw new AuthorizationDeniedException("person_evidence_invalid");
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(session, accounts, token);
        var decision = PersonApprovalPolicy.Evaluate(actor.UserId, permission, approval, proof.Evidence);
        if (!decision.Allowed) throw new AuthorizationDeniedException(decision.Code);
        await new PersonAuthorizationFence(database, clock).TouchAsync(session, proof, token);
        return proof;
    }

    private async Task CompletePersonAuthorizationAsync(ActorContext actor, AuthorizationSnapshot snapshot,
        CommandAuthorization authorization, PersonAuthorityProof? proof, IClientSessionHandle session, CancellationToken token)
    {
        if (proof is null) return;
        void RequireCurrent()
        {
            proof.RequireCurrent(session, clock.UtcNow);
            var query = ScopePolicy.Compile(actor, snapshot, authorization.Permission!, clock.UtcNow);
            Require(query);
            if (authorization.Target is null || !query.Matches(authorization.Target.Resource))
                throw new AuthorizationDeniedException("scope_denied");
        }
        RequireCurrent();
        // Refresh the session's time predicate after business and receipt persistence.
        await FenceAsync(actor, [], session, token);
        if (actor.Session is { } reference)
        {
            var live = await database.GetCollection<BsonDocument>("identitySessions").Find(session,
                SessionPersistence.LiveFilter(reference, actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow))
                .Project(new BsonDocument { { "idleExpiresAt", 1 }, { "absoluteExpiresAt", 1 } }).FirstOrDefaultAsync(token);
            var nowMilliseconds = new BsonDateTime(clock.UtcNow.UtcDateTime).MillisecondsSinceEpoch;
            if (live is null || !live.TryGetValue("idleExpiresAt", out var idle) || !idle.IsBsonDateTime ||
                !live.TryGetValue("absoluteExpiresAt", out var absolute) || !absolute.IsBsonDateTime ||
                idle.AsBsonDateTime.MillisecondsSinceEpoch <= nowMilliseconds || absolute.AsBsonDateTime.MillisecondsSinceEpoch <= nowMilliseconds)
                throw new AuthorizationDeniedException("stale_or_inactive_session");
        }
        RequireCurrent();
    }

    internal async Task FenceAsync(ActorContext actor, IEnumerable<ScopeNode> nodes, IClientSessionHandle session, CancellationToken token)
    {
        var updated = await database.GetCollection<BsonDocument>("identities").UpdateOneAsync(session, IdentityFilter(actor),
            Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: token);
        if (updated.ModifiedCount != 1) throw new AuthorizationDeniedException("stale_or_inactive_actor");
        if (actor.Session is { } reference)
        {
            var sessionChanged = await database.GetCollection<BsonDocument>("identitySessions").UpdateOneAsync(session,
                SessionPersistence.LiveFilter(reference, actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow),
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: token);
            if (sessionChanged.ModifiedCount != 1) throw new AuthorizationDeniedException("stale_or_inactive_session");
        }
        foreach (var node in nodes.OrderBy(node => node.Id))
        {
            var filter = F.Eq("_id", node.Id) & F.Eq("kind", AuthorizationBson.Name(node.Kind)) & F.Eq("version", node.Version) & F.Eq("status", "active");
            var changed = await database.GetCollection<BsonDocument>("authorizationScopes").UpdateOneAsync(session, filter,
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: token);
            if (changed.ModifiedCount != 1) throw new AuthorizationDeniedException("scope_unavailable");
        }
    }

    /// <summary>Call after the owning read policy succeeds, in the same session as its read. The caller supplies only safe selection metadata.</summary>
    internal async Task RecordPrivilegedReadAsync(long id, ActorContext actor, AuthorizationSnapshot snapshot, string operation,
        AuthorizationReadMetadata audit, BsonDocument selection, int resultCount, ImmutableArray<GrantScope> scopes,
        IClientSessionHandle session, CancellationToken token)
    {
        AccessAdministrationService.ValidateReadMetadata(audit);
        var distinct = scopes.Distinct().ToArray();
        if (distinct.Length is 0 or > 256 || id <= 0 || resultCount is < 0 or > 100) throw new AuthorizationDeniedException("invalid_audit_scope");
        await FenceAsync(actor, distinct.SelectMany(scope => ScopePolicy.RequiredNodes(scope, snapshot.Directory)).Distinct(), session, token);
        await database.GetCollection<BsonDocument>("authorizationReadEvents").InsertOneAsync(session, new BsonDocument
        {
            { "_id", id }, { "schemaVersion", AuthorizationBson.SchemaVersion }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
            { "operation", operation }, { "purpose", audit.Purpose }, { "correlationId", audit.CorrelationId }, { "createdAt", clock.UtcNow.UtcDateTime },
            { "selection", selection }, { "resultCount", resultCount }, { "scopes", new BsonArray(distinct.Select(AuthorizationBson.Scope)) }
        }, cancellationToken: token);
    }
    internal static void Require(AuthorizationQuery query) { if (!query.IsAllowed) throw new AuthorizationDeniedException(query.DenialCode ?? "permission_denied"); }
    internal static void ValidatePage(int offset, int limit)
    {
        if (offset is < 0 or > 10000 || limit is < 1 or > 100) throw new DomainValidationException("invalid_page", "Page offset or size is outside the supported range");
    }
    internal static void ValidateMetadata(AuthorizationCommandMetadata metadata)
    {
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.IdempotencyKey) || metadata.IdempotencyKey.Length > 200 ||
            string.IsNullOrWhiteSpace(metadata.Reason) || metadata.Reason.Length > 2000 || string.IsNullOrWhiteSpace(metadata.CorrelationId) ||
            metadata.CorrelationId.Length > 128 || metadata.CorrelationId.Any(character => char.IsControl(character)))
            throw new DomainValidationException("invalid_access_request", "Request key, bounded audit reason and correlation ID are required");
    }
    private static FilterDefinition<BsonDocument> IdentityFilter(ActorContext actor) => F.Eq("_id", actor.UserId) &
        F.Eq("issuer", actor.ProviderIssuer) & F.Eq("subject", actor.ProviderSubject) & F.Eq("status", "active") & F.Eq("grantVersion", actor.GrantVersion) &
        (actor.Session is { } session ? F.Eq("securityVersion", session.SecurityVersion) : F.Empty);
    private void ValidateCollection(IMongoCollection<BsonDocument> collection)
    {
        if (collection.Database.DatabaseNamespace != database.DatabaseNamespace) throw new ArgumentException("Resource collection must use the same application database");
    }
}
