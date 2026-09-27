using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Identity;

public sealed partial class IdentityOnboardingService
{
    public async Task<InvitationPage> ListInvitationsAsync(ActorContext actor, int offset, int limit,
        AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        AuthorizationStore.ValidatePage(offset, limit); ValidateIdentityRead(audit);
        var readId = (await ids.NextAsync("identityReadEvents", cancellationToken)).Value;
        return await store.ReadAsync(actor, async (snapshot, session, token) =>
        {
            var authority = DirectoryAuthority(actor, snapshot);
            var filter = authority.All ? F.Empty : AccessAdministrationService.CanonicalHistoryScope("scope", snapshot.Directory) &
                F.Or(authority.Scopes.Select(scope => AccessAdministrationService.ScopeContainmentFilter(scope, "scope")));
            var invitations = Collection("accountInvitations");
            var count = await invitations.CountDocumentsAsync(session, filter, cancellationToken: token);
            var rows = await invitations.Find(session, filter).Sort(new BsonDocument("_id", -1)).Skip(offset).Limit(limit).ToListAsync(token);
            var profile = await Collection("accountProfiles").Find(session, F.Eq("_id", actor.UserId)).FirstOrDefaultAsync(token);
            var items = rows.Select(row =>
            {
                var actions = ImmutableArray.CreateBuilder<string>();
                if (row["status"] == "pending")
                {
                    if (row["providerIssuer"] == policy.TrustedIssuer && row["providerConnectionId"] == policy.TrustedConnectionId &&
                        At(row, "expiresAt") > clock.UtcNow && Approval(actor, snapshot, row).Allowed &&
                        (profile is null || profile["verifiedEmail"] != row["intendedEmail"])) actions.Add("approve");
                    if (ResolutionAuthority(actor, snapshot, row, "rejected").Allowed) actions.Add("reject");
                    if (ResolutionAuthority(actor, snapshot, row, "withdrawn").Allowed) actions.Add("withdraw");
                }
                if (row["status"] == "approved" && ResolutionAuthority(actor, snapshot, row, "revoked").Allowed) actions.Add("revoke");
                return new InvitationSummary(row["_id"].AsInt64, row["userId"].AsInt64, row["intendedEmail"].AsString,
                    AuthorizationBson.Scope(row["scope"].AsBsonDocument), Role(row), row["requestedBy"].AsInt64, OptionalLong(row, "approvedBy"),
                    row["reason"].AsString, At(row, "expiresAt"), OptionalAt(row, "accessExpiresAt"), row["version"].AsInt64, row["status"].AsString, actions.ToImmutable());
            }).ToImmutableArray();
            await AuditReadAsync(readId, actor, snapshot, "identity.invitations.read", audit, offset, limit, null, items.Length, authority.Scopes, session, token);
            return new InvitationPage(count, items);
        }, cancellationToken);
    }

    public async Task<AccountDirectoryPage> ListProfilesAsync(ActorContext actor, int offset, int limit,
        AuthorizationReadMetadata audit, long? userId = null, CancellationToken cancellationToken = default)
    {
        AuthorizationStore.ValidatePage(offset, limit); ValidateIdentityRead(audit);
        if (userId is <= 0) throw new AuthorizationDeniedException("resource_unavailable");
        var readId = (await ids.NextAsync("identityReadEvents", cancellationToken)).Value;
        return await store.ReadAsync(actor, async (snapshot, session, token) =>
        {
            var authority = DirectoryAuthority(actor, snapshot);
            var pipeline = new List<BsonDocument>();
            IMongoCollection<BsonDocument> source;
            if (authority.All)
            {
                source = Collection("accountProfiles");
                if (userId.HasValue) pipeline.Add(new("$match", new BsonDocument("_id", userId.Value)));
                pipeline.Add(new("$replaceRoot", new BsonDocument("newRoot", new BsonDocument { { "_id", "$_id" }, { "profile", "$$ROOT" } })));
            }
            else
            {
                source = Collection("memberships");
                var activeDirectory = new ScopeDirectory(snapshot.Directory.Nodes.Where(node => node.Status == AuthorizationStatus.Active).ToImmutableArray());
                var filter = F.Eq("status", "active") & F.Gt("approvedBy", 0L) & F.Lte("effectiveAt", clock.UtcNow.UtcDateTime) &
                    (F.Eq("expiresAt", BsonNull.Value) | F.Gt("expiresAt", clock.UtcNow.UtcDateTime)) & F.Eq("revokedAt", BsonNull.Value) &
                    AccessAdministrationService.CanonicalHistoryScope("scope", activeDirectory) &
                    F.Or(authority.Scopes.Select(scope => AccessAdministrationService.ScopeContainmentFilter(scope, "scope")));
                if (userId.HasValue) filter &= F.Eq("userId", userId.Value);
                pipeline.Add(new("$match", filter.Render(new(BsonDocumentSerializer.Instance, BsonSerializer.SerializerRegistry))));
                pipeline.Add(new("$match", new BsonDocument("$expr", new BsonDocument("$ne", new BsonArray { "$approvedBy", "$userId" }))));
                pipeline.Add(new("$group", new BsonDocument("_id", "$userId")));
                pipeline.Add(new("$lookup", new BsonDocument { { "from", "accountProfiles" }, { "localField", "_id" }, { "foreignField", "_id" }, { "as", "profile" } }));
                pipeline.Add(new("$unwind", "$profile"));
            }
            pipeline.Add(new("$lookup", new BsonDocument { { "from", "identities" }, { "localField", "_id" }, { "foreignField", "_id" }, { "as", "identity" } }));
            pipeline.Add(new("$unwind", "$identity"));
            pipeline.Add(new("$project", new BsonDocument
            {
                { "_id", 1 }, { "displayName", "$profile.displayName" }, { "verifiedEmail", "$profile.verifiedEmail" }, { "verifiedAt", "$profile.verifiedAt" },
                { "status", "$identity.status" }, { "securityVersion", "$identity.securityVersion" }, { "grantVersion", "$identity.grantVersion" }
            }));
            pipeline.Add(new("$facet", new BsonDocument
            {
                { "total", new BsonArray { new BsonDocument("$count", "count") } },
                { "items", new BsonArray { new BsonDocument("$sort", new BsonDocument { { "verifiedEmail", 1 }, { "_id", 1 } }), new BsonDocument("$skip", offset), new BsonDocument("$limit", limit) } }
            }));
            var page = await source.Aggregate<BsonDocument>(session, pipeline).SingleAsync(token);
            var total = page["total"].AsBsonArray.Count == 0 ? 0 : page["total"][0]["count"].ToInt64();
            var items = page["items"].AsBsonArray.Select(value => Profile(value.AsBsonDocument)).ToImmutableArray();
            await AuditReadAsync(readId, actor, snapshot, "identity.directory.read", audit, offset, limit, userId, items.Length, authority.Scopes, session, token);
            return new AccountDirectoryPage(total, items);
        }, cancellationToken);
    }

    public Task<AccountProfile?> ReadOwnProfileAsync(ActorContext actor, CancellationToken cancellationToken = default) =>
        store.ReadAsync<AccountProfile?>(actor, async (_, session, token) =>
        {
            var profile = await Collection("accountProfiles").Find(session, F.Eq("_id", actor.UserId)).FirstOrDefaultAsync(token);
            if (profile is null) return null;
            var identity = await FindAsync("identities", actor.UserId, session, token);
            profile["status"] = identity["status"]; profile["securityVersion"] = identity["securityVersion"]; profile["grantVersion"] = identity["grantVersion"];
            return Profile(profile);
        }, cancellationToken);

    private (bool All, ImmutableArray<GrantScope> Scopes) DirectoryAuthority(ActorContext actor, AuthorizationSnapshot snapshot)
    {
        if (AdministrativeDecision(actor, snapshot, Permissions.InviteManage).Allowed || AdministrativeDecision(actor, snapshot, Permissions.AccessAuditRead).Allowed)
            return (true, [new(GrantScopeKind.System)]);
        var scopes = new[] { Permissions.AccessApprove, Permissions.MembershipApprove, Permissions.AccessRevoke }
            .Select(permission => ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow)).Where(query => query.IsAllowed)
            .SelectMany(query => query.Clauses.Select(clause => clause.Scope)).Distinct().ToImmutableArray();
        return scopes.IsEmpty || scopes.Length > 256 ? throw new AuthorizationDeniedException("permission_denied") : (false, scopes);
    }
    private static AccountProfile Profile(BsonDocument row) => new(row["_id"].AsInt64, row["displayName"].IsBsonNull ? null : row["displayName"].AsString,
        row["verifiedEmail"].AsString, At(row, "verifiedAt"), row["status"].AsString, SecurityVersion(row), row["grantVersion"].AsInt64);
    private static void ValidateIdentityRead(AuthorizationReadMetadata audit)
    {
        AccessAdministrationService.ValidateReadMetadata(audit);
        if (audit.Purpose == "operation-recovery") throw new AuthorizationDeniedException("invalid_read_purpose");
    }
    private async Task AuditReadAsync(long id, ActorContext actor, AuthorizationSnapshot snapshot, string operation, AuthorizationReadMetadata audit,
        int offset, int limit, long? userId, int count, ImmutableArray<GrantScope> scopes, IClientSessionHandle session, CancellationToken token)
    {
        await store.FenceAsync(actor, scopes.SelectMany(scope => ScopePolicy.RequiredNodes(scope, snapshot.Directory)).Distinct(), session, token);
        var selection = new BsonDocument { { "offset", offset }, { "limit", limit } }; if (userId.HasValue) selection.Add("subjectId", userId.Value);
        await Collection("identityReadEvents").InsertOneAsync(session, new BsonDocument
        {
            { "_id", id }, { "schemaVersion", 1 }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "operation", operation },
            { "purpose", audit.Purpose }, { "correlationId", audit.CorrelationId }, { "createdAt", clock.UtcNow.UtcDateTime },
            { "selection", selection }, { "resultCount", count }, { "scopes", new BsonArray(scopes.Select(AuthorizationBson.Scope)) }
        }, cancellationToken: token);
    }
}
