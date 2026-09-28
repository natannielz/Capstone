using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Identity;

public sealed partial class PersonWithdrawalService
{
    public async Task<PersonWithdrawalResult> ProposeAsync(ActorContext actor, long groupId, long expectedGroupVersion,
        AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        Version(groupId, expectedGroupVersion); AuthorizationStore.ValidateMetadata(metadata);
        var proposalId = (await ids.NextAsync("personReviewProposals", cancellationToken)).Value;
        var historyId = (await ids.NextAsync("personReviewHistory", cancellationToken)).Value;
        return await ExecuteAsync(actor, "propose", metadata, new { groupId, expectedGroupVersion },
            async (tx, guards, ct) =>
            {
                var target = await new PersonWithdrawalTargetLoader(database).LoadAsync(tx, groupId, ct);
                if (target.GroupVersion != expectedGroupVersion) throw Conflict();
                var person = await PersonAsync(tx, actor.UserId, guards, ct);
                var proposal = new BsonDocument
                {
                    { "_id", proposalId }, { "schemaVersion", 1 }, { "kind", "equivalence-group-suspension" },
                    { "version", 1L }, { "status", "pending" }, { "requestedBy", actor.UserId },
                    { "requesterGrantVersion", actor.GrantVersion }, { "requesterSecurityVersion", await ActorSecurityAsync(tx, actor, ct) },
                    { "createdAt", clock.UtcNow.UtcDateTime }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId },
                    { "target", target.Snapshot }, { "requesterPerson", PersonSnapshot(person) }
                };
                proposal.Add("requestDigest", RequestDigest(proposal));
                var permit = await PermitAsync(tx, actor.UserId, "propose", Intent(proposal), guards, ct);
                proposal.Add("policy", permit.Pin);
                await target.TouchAsync(database, tx, ct);
                Require("personReviewProposals", proposal);
                await Collection("personReviewProposals").InsertOneAsync(tx, proposal, cancellationToken: ct);
                await WriteHistoryAsync(historyId, proposal, ct, tx);
                return new(proposal, historyId);
            }, cancellationToken);
    }

    public Task<PersonWithdrawalResult> ApproveAsync(ActorContext actor, long proposalId, long expectedVersion,
        AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default) =>
        DecideAsync(actor, proposalId, expectedVersion, "approve", metadata, cancellationToken);
    public Task<PersonWithdrawalResult> RejectAsync(ActorContext actor, long proposalId, long expectedVersion,
        AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default) =>
        DecideAsync(actor, proposalId, expectedVersion, "reject", metadata, cancellationToken);
    public Task<PersonWithdrawalResult> WithdrawAsync(ActorContext actor, long proposalId, long expectedVersion,
        AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default) =>
        DecideAsync(actor, proposalId, expectedVersion, "withdraw", metadata, cancellationToken);

    private async Task<PersonWithdrawalResult> DecideAsync(ActorContext actor, long proposalId, long expectedVersion,
        string action, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken)
    {
        Version(proposalId, expectedVersion); AuthorizationStore.ValidateMetadata(metadata);
        var historyId = (await ids.NextAsync("personReviewHistory", cancellationToken)).Value;
        return await ExecuteAsync(actor, action, metadata, new { proposalId, expectedVersion },
            async (tx, guards, ct) =>
            {
                var proposal = await ProposalAsync(tx, proposalId, ct);
                if (proposal["status"] != "pending" || proposal["version"] != expectedVersion) throw Conflict();
                var requesterId = proposal["requestedBy"].AsInt64;
                var intent = Intent(proposal);
                var reviewerPermit = await PermitAsync(tx, actor.UserId, action, intent, guards, ct);
                var reviewerPerson = await PersonAsync(tx, actor.UserId, guards, ct);
                if (!Equal(reviewerPermit.Pin, proposal["policy"].AsBsonDocument)) throw Denied("person_policy_changed");
                var target = proposal["target"].AsBsonDocument;
                ImmutableArray<PersonAccountAuthorityRevision> affected = [];
                var groupVersion = target["groupVersion"].AsInt64;
                if (action == "withdraw")
                {
                    if (requesterId != actor.UserId) throw Denied();
                }
                else
                {
                    if (requesterId == actor.UserId ||
                        reviewerPerson.Evidence.Single().PersonId == proposal["requesterPerson"]["canonicalPersonId"].AsInt64) throw Denied("independent_person_required");
                }
                if (action == "approve")
                {
                    await RequireRequesterAsync(actor, proposal, tx, guards, ct);
                    var current = await new PersonWithdrawalTargetLoader(database).LoadAsync(tx, target["groupId"].AsInt64, ct);
                    if (!Equal(current.Snapshot, target)) throw Conflict();
                    var reviewerPeople = reviewerPerson.CopyRecords().Where(row => row.Collection == "personRecords").Select(row => row.Document["_id"].AsInt64);
                    var reviewerAccounts = reviewerPerson.CopyRecords().Where(row => row.Collection == "personAccountBindings").Select(row => row.Document["_id"].AsInt64);
                    if (reviewerPeople.Intersect(current.PersonIds).Any() || reviewerAccounts.Intersect(current.UserIds).Any())
                        throw Denied("independent_person_required");
                    await current.TouchAsync(database, tx, ct);
                    var changed = await Collection("personEquivalenceGroups").UpdateOneAsync(tx,
                        F.Eq("_id", current.GroupId) & F.Eq("version", current.GroupVersion) & F.Eq("status", current.GroupStatus),
                        U.Set("status", "suspended").Inc("version", 1L), cancellationToken: ct);
                    if (changed.ModifiedCount != 1) throw Conflict();
                    affected = await invalidation.InvalidateAsync(tx, current.UserIds, ct);
                    groupVersion = checked(current.GroupVersion + 1);
                }
                var next = proposal.DeepClone().AsBsonDocument;
                next["version"] = 2L;
                next["status"] = action switch { "approve" => "approved", "reject" => "rejected", _ => "withdrawn" };
                next.Add("resolution", new BsonDocument
                {
                    { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
                    { "actorSecurityVersion", await ActorSecurityAsync(tx, actor, ct) },
                    { "decidedAt", clock.UtcNow.UtcDateTime }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId },
                    { "person", PersonSnapshot(reviewerPerson) }, { "policy", reviewerPermit.Pin }, { "resultGroupVersion", groupVersion },
                    { "affectedRevisions", new BsonArray(affected.Select(row => new BsonDocument
                        { { "userId", row.UserId }, { "previousGrantVersion", row.PreviousGrantVersion }, { "grantVersion", row.GrantVersion },
                          { "previousSecurityVersion", row.PreviousSecurityVersion }, { "securityVersion", row.SecurityVersion } })) }
                });
                Require("personReviewProposals", next);
                var replaced = await Collection("personReviewProposals").ReplaceOneAsync(tx,
                    F.Eq("_id", proposalId) & F.Eq("version", expectedVersion) & F.Eq("status", "pending") &
                    F.Eq("requestDigest", proposal["requestDigest"]), next, cancellationToken: ct);
                if (replaced.ModifiedCount != 1) throw Conflict();
                await WriteHistoryAsync(historyId, next, ct, tx);
                return new(next, historyId);
            }, cancellationToken);
    }

    private async Task RequireRequesterAsync(ActorContext reviewer, BsonDocument proposal, IClientSessionHandle tx,
        Guards guards, CancellationToken ct)
    {
        var requesterId = proposal["requestedBy"].AsInt64;
        var identity = await Collection("identities").Find(tx, F.Eq("_id", requesterId)).FirstOrDefaultAsync(ct);
        bool Text(string name, int maximum) =>
            identity is not null && identity.TryGetValue(name, out var value) && value.IsString &&
            !string.IsNullOrWhiteSpace(value.AsString) && value.AsString.Length <= maximum &&
            !value.AsString.Any(char.IsControl);
        if (identity is null || !identity.TryGetValue("_id", out var id) || !id.IsInt64 || id.AsInt64 != requesterId ||
            !identity.TryGetValue("schemaVersion", out var schema) || !schema.IsInt32 || schema.AsInt32 != 1 ||
            !identity.TryGetValue("status", out var status) || !status.IsString || status.AsString != "active" ||
            !identity.TryGetValue("grantVersion", out var grantVersion) || !grantVersion.IsInt64 ||
            grantVersion != proposal["requesterGrantVersion"] ||
            !identity.TryGetValue("fence", out var fence) || !fence.IsInt64 || fence.AsInt64 is < 0 or long.MaxValue ||
            !Text("issuer", 2048) || !Text("subject", 512) ||
            Security(identity) != proposal["requesterSecurityVersion"].AsInt64) throw Denied("requester_authority_changed");
        // Fence the validated provider identity as well as its epochs without impersonating the requester.
        var identityFilter = F.Eq("_id", requesterId) & F.Eq("schemaVersion", 1) & F.Eq("status", "active") &
            F.Eq("issuer", identity["issuer"]) & F.Eq("subject", identity["subject"]) &
            F.Eq("grantVersion", proposal["requesterGrantVersion"]) &
            (identity.Contains("securityVersion") ? F.Eq("securityVersion", identity["securityVersion"]) : F.Exists("securityVersion", false)) &
            new BsonDocument("$jsonSchema", new BsonDocument
            {
                { "required", new BsonArray { "_id", "schemaVersion", "issuer", "subject", "status", "grantVersion", "fence" } },
                { "properties", new BsonDocument
                    {
                        { "_id", new BsonDocument("bsonType", "long") }, { "schemaVersion", new BsonDocument("bsonType", "int") },
                        { "issuer", new BsonDocument("bsonType", "string") }, { "subject", new BsonDocument("bsonType", "string") },
                        { "status", new BsonDocument("bsonType", "string") }, { "grantVersion", new BsonDocument("bsonType", "long") },
                        { "fence", new BsonDocument { { "bsonType", "long" }, { "minimum", 0L }, { "maximum", long.MaxValue - 1 } } },
                        { "securityVersion", new BsonDocument("bsonType", "long") }
                    } }
            });
        var changed = await Collection("identities").UpdateOneAsync(tx, identityFilter, U.Inc("fence", 1L), cancellationToken: ct);
        if (changed.ModifiedCount != 1) throw Denied("requester_authority_changed");
        var permit = await PermitAsync(tx, requesterId, "propose", Intent(proposal), guards, ct);
        if (!Equal(permit.Pin, proposal["policy"].AsBsonDocument)) throw Denied("person_policy_changed");
        var person = await PersonAsync(tx, requesterId, guards, ct);
        if (!Equal(PersonSnapshot(person), proposal["requesterPerson"].AsBsonDocument)) throw Denied("requester_authority_changed");

        var grants = (await Collection("authorizationGrants").Find(tx, F.Eq("userId", requesterId)).Limit(1025).ToListAsync(ct))
            .Select(AuthorizationBson.Grant).ToArray();
        var memberships = (await Collection("memberships").Find(tx, F.Eq("userId", requesterId)).Limit(1025).ToListAsync(ct))
            .Select(AuthorizationBson.Membership).ToArray();
        if (grants.Length > 1024 || memberships.Length > 1024 || grants.Select(row => row.Id).Distinct().Count() != grants.Length ||
            memberships.Select(row => row.Id).Distinct().Count() != memberships.Length) throw Denied();
        var directory = new ScopeDirectory([]);
        bool Valid(PermissionGrant grant, DateTimeOffset now) =>
            ScopePolicy.IsGrantActive(grant, requesterId, now) && grant.Scope.Kind == GrantScopeKind.System &&
            ScopePolicy.IsScopeActive(grant.Scope, directory) && grant.Permissions.Contains(Permissions.PersonReviewPropose) &&
            memberships.Any(member => member.Id == grant.MembershipId && member.Id > 0 && member.Version > 0 &&
                member.UserId == requesterId && member.ApprovedBy > 0 && member.ApprovedBy != requesterId &&
                ScopePolicy.IsEffective(member.Status, member.EffectiveAt, member.ExpiresAt, member.RevokedAt, now) &&
                member.Scope.Kind == GrantScopeKind.System && ScopePolicy.IsScopeActive(member.Scope, directory) &&
                ScopePolicy.ContainsScope(member.Scope, grant.Scope));
        var selected = grants.Where(grant => Valid(grant, clock.UtcNow)).ToArray();
        if (selected.Length == 0) throw Denied("requester_authority_changed");
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(reviewer, [], [], selected,
            memberships.Where(member => selected.Any(grant => grant.MembershipId == member.Id)), tx, ct);
        guards.Checks.Add(now => { if (!selected.Any(grant => Valid(grant, now))) throw Denied("requester_authority_changed"); });
    }
    private static string RequestDigest(BsonDocument proposal) => PersonWithdrawalTarget.Digest(new BsonDocument
    {
        { "_id", proposal["_id"] }, { "kind", proposal["kind"] }, { "requestedBy", proposal["requestedBy"] }, { "reason", proposal["reason"] },
        { "requesterGrantVersion", proposal["requesterGrantVersion"] }, { "requesterSecurityVersion", proposal["requesterSecurityVersion"] },
        { "createdAt", proposal["createdAt"] }, { "correlationId", proposal["correlationId"] },
        { "target", proposal["target"] }, { "requesterPerson", proposal["requesterPerson"] }
    }, false);
    private static PersistenceConflictException Conflict() => new("version_conflict", "Review changed; refresh the proposal");
}
