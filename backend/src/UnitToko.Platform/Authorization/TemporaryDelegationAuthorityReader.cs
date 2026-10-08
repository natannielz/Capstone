using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Identity;
using R = UnitToko.Platform.Authorization.TemporaryDelegationRecords;

namespace UnitToko.Platform.Authorization;

/// <summary>No delegation-chain traversal grants authority. Every eligible temporary grant is
/// proved against its original ordinary source in this transaction; stale sources only remove it.</summary>
internal sealed class TemporaryDelegationAuthorityReader(IMongoDatabase database, IClock clock)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);

    internal async Task<ImmutableArray<PermissionGrant>> ResolveAsync(ImmutableArray<PermissionGrant> grants,
        ScopeDirectory directory, IClientSessionHandle tx, CancellationToken ct) {
        var result = ImmutableArray.CreateBuilder<PermissionGrant>();
        foreach (var grant in grants) {
            if (grant.TemplateId != TemporaryDelegationPolicy.TemplateId) { result.Add(grant); continue; }
            var head = await R.HeadAsync(database, tx, grant.Id, ct);
            if (head["approvedBy"] == 0L || AuthorizationBson.Grant(grant) != AuthorizationBson.Grant(R.Grant(head))) throw R.Invalid();
            TemporaryDelegationAuthority? proof = null;
            if (head["status"] == "active" && await SourceDeadlineAsync(head, directory, tx, ct) is { } deadline)
                proof = new(grant.Id, head["request"]["sourceGrantId"].AsInt64, head["sourceUserId"].AsInt64,
                    head["request"]["sourceVersion"].AsInt64, clock.UtcNow, deadline);
            await TouchAsync(head, tx, ct); result.Add(grant with { TemporaryAuthority = proof });
        }
        return result.ToImmutable();
    }

    internal async Task<bool> SourceAsync(BsonDocument head, ScopeDirectory directory, IClientSessionHandle tx, CancellationToken ct) =>
        await SourceDeadlineAsync(head, directory, tx, ct) is not null;

    internal async Task<DateTimeOffset?> SourceDeadlineAsync(BsonDocument head, ScopeDirectory directory, IClientSessionHandle tx, CancellationToken ct) {
        var request = R.Request(head["request"]); var sourceUser = head["sourceUserId"].AsInt64;
        var source = await Rows("authorizationGrants").Find(tx, F.Eq("_id", request.SourceGrantId)).SingleOrDefaultAsync(ct);
        var sourceMember = await Rows("memberships").Find(tx, F.Eq("_id", head["sourceMembership"]["_id"])).SingleOrDefaultAsync(ct);
        var beneficiary = await Rows("memberships").Find(tx, F.Eq("_id", request.BeneficiaryMembershipId)).SingleOrDefaultAsync(ct);
        var identity = await Rows("identities").Find(tx, F.Eq("_id", sourceUser) & F.Eq("status", "active") &
            F.Eq("grantVersion", head["sourceIdentityVersion"])).SingleOrDefaultAsync(ct);
        var recipient = await Rows("identities").Find(tx, F.Eq("_id", request.BeneficiaryUserId) & F.Eq("status", "active")).SingleOrDefaultAsync(ct);
        if (source is null || sourceMember is null || beneficiary is null || identity is null || recipient is null) return null;
        var grant = AuthorizationBson.Grant(source); var membership = AuthorizationBson.Membership(sourceMember);
        var member = AuthorizationBson.Membership(beneficiary);
        if (AuthorizationBson.Grant(grant) != head["sourceGrant"] || AuthorizationBson.Membership(membership) != head["sourceMembership"] ||
            AuthorizationBson.Membership(member) != head["beneficiaryMembership"] ||
            !TemporaryDelegationPolicy.SourceAllows(request, grant, membership, member, directory, sourceUser, clock.UtcNow)) return null;
        var nodes = ScopePolicy.RequiredNodes(grant.Scope, directory).Concat(ScopePolicy.RequiredNodes(membership.Scope, directory))
            .Concat(ScopePolicy.RequiredNodes(member.Scope, directory)).Distinct().OrderBy(n => n.Id).ToArray();
        var expectedNodes = new BsonArray(nodes.Select(n => { var row = AuthorizationBson.Node(n); row.Remove("fence"); return row; }));
        if (head["sourceNodes"] != expectedNodes) return null;
        var accounts = new[] { sourceUser, request.BeneficiaryUserId, head["approvedBy"].AsInt64 }.Where(id => id > 0).Distinct().Order().ToImmutableArray();
        PersonAuthorityProof people;
        try { people = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, accounts, ct); }
        catch (AuthorizationDeniedException failure) when (failure.Code == "person_authority_unavailable") {
            // Unavailable source-person evidence removes only this temporary authority. The
            // authenticated owner history remains readable so a participant can revoke it.
            return null;
        }
        var giver = people.Evidence.Single(p => p.UserId == sourceUser);
        var recipientPerson = people.Evidence.Single(p => p.UserId == request.BeneficiaryUserId);
        if (!PersonIndependencePolicy.AreIndependent(giver, recipientPerson) || head["approvedBy"].AsInt64 > 0 &&
            !PersonIndependencePolicy.AreIndependent(people.Evidence.Single(p => p.UserId == head["approvedBy"].AsInt64), giver, recipientPerson)) return null;
        await new PersonAuthorizationFence(database, clock).TouchAsync(tx, people, ct);
        await R.ExactAsync(database, tx, "authorizationGrants", source, ct);
        await R.ExactAsync(database, tx, "memberships", sourceMember, ct); await R.ExactAsync(database, tx, "memberships", beneficiary, ct);
        foreach (var row in new[] { identity, recipient }.DistinctBy(row => row["_id"])) {
            var changed = await Rows("identities").UpdateOneAsync(tx, F.Eq("_id", row["_id"]) & F.Eq("status", "active") & F.Eq("grantVersion", row["grantVersion"]),
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct);
            if (changed.ModifiedCount != 1) throw R.Invalid();
        }
        foreach (var node in ScopePolicy.RequiredNodes(grant.Scope, directory).Concat(ScopePolicy.RequiredNodes(membership.Scope, directory))
                     .Concat(ScopePolicy.RequiredNodes(member.Scope, directory))
                     .Concat(ScopePolicy.RequiredNodes(request.Scope, directory)).Distinct().OrderBy(node => node.Id)) {
            var changed = await Rows("authorizationScopes").UpdateOneAsync(tx, F.Eq("_id", node.Id) & F.Eq("version", node.Version) & F.Eq("status", "active"),
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct);
            if (changed.ModifiedCount != 1) throw R.Invalid();
        }
        people.RequireCurrent(tx, clock.UtcNow);
        if (!TemporaryDelegationPolicy.SourceAllows(request, grant, membership, member, directory, sourceUser, clock.UtcNow)) return null;
        return people.ValidUntil is { } until && until < request.ExpiresAt ? until : request.ExpiresAt;
    }

    internal async Task TouchAsync(BsonDocument head, IClientSessionHandle tx, CancellationToken ct) {
        if (head["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(R.Heads).UpdateOneAsync(tx, head,
            Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw R.Invalid();
    }
    internal static void RequireDeadlines(AuthorizationSnapshot snapshot, DateTimeOffset now, IEnumerable<long>? used = null) {
        foreach (var grant in snapshot.Grants.Where(grant => grant.TemporaryAuthority is not null && (used is null || used.Contains(grant.Id))))
            if (!TemporaryDelegationPolicy.HasCurrentProof(grant, now)) throw new AuthorizationDeniedException("delegation_expired");
    }

    /// <summary>Changes to source identity versions permanently invalidate captured delegations.
    /// Bump all reachable beneficiary fences so old sessions cannot continue using cached authority.
    /// This does not activate or restore a grant, change task ownership, or rewrite immutable history.</summary>
    internal static async Task InvalidateDependentsAsync(IMongoDatabase database, IClientSessionHandle tx, IEnumerable<long> changedUsers, CancellationToken ct) {
        var visited = changedUsers.ToHashSet(); var pending = new Queue<long>(visited);
        while (pending.TryDequeue(out var source)) {
            using var cursor = await database.GetCollection<BsonDocument>(R.Heads).Find(tx,
                F.Eq("sourceUserId", source) & F.Eq("status", "active")).Project(new BsonDocument("request.beneficiaryUserId", 1)).ToCursorAsync(ct);
            while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current) {
                var beneficiary = row["request"]["beneficiaryUserId"].AsInt64;
                if (!visited.Add(beneficiary)) continue;
                var result = await database.GetCollection<BsonDocument>("identities").UpdateOneAsync(tx, F.Eq("_id", beneficiary),
                    Builders<BsonDocument>.Update.Inc("grantVersion", 1L).Inc("fence", 1L), cancellationToken: ct);
                if (result.ModifiedCount != 1) throw R.Invalid(); pending.Enqueue(beneficiary);
            }
        }
    }
}
