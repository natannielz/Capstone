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
    public async Task<PersonReviewPage> ListReviewsAsync(ActorContext actor, string? status, int offset, int limit,
        AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        var request = new PersonReviewSelectionRequest(PersonReviewSelectionKind.ProposalList, Status: status, Offset: offset, Limit: limit).Normalize();
        AccessAdministrationService.ValidateReadMetadata(audit);
        var auditId = (await ids.NextAsync("authorizationReadEvents", cancellationToken)).Value;
        return await unit.ExecuteAsync(async (tx, ct) =>
        {
            var guards = new Guards();
            var selected = await ReviewSelectionAsync(tx, actor, request, guards, ct);
            var filter = ReviewProposalFilter(selected.Permit);
            if (request.Status is not null) filter &= F.Eq("status", request.Status);
            var references = await Collection("personReviewProposals").Find(tx, filter).Project(new BsonDocument("_id", 1))
                .Sort(new BsonDocument("_id", -1)).Limit(257).ToListAsync(ct);
            if (references.Count > 256 || references.Any(row => !ReviewPositive(row, "_id"))) throw Denied();
            var proposals = new List<BsonDocument>();
            foreach (var reference in references)
                proposals.Add(await ScopedReviewProposalAsync(tx, reference["_id"].AsInt64, selected.Permit, ct));
            var users = proposals.SelectMany(ReviewProposalUsers).Distinct().Order().ToImmutableArray();
            var profiles = await ReviewProfilesAsync(tx, users, selected.Permit, ct);
            var items = ImmutableArray.CreateBuilder<PersonReviewSummary>();
            foreach (var proposal in proposals.Skip(request.Offset).Take(request.Limit))
                items.Add(await ReviewSummaryAsync(actor, proposal, profiles, selected.Authorization, selected.Person, tx, guards, ct));
            await ReviewReadAuditAsync(auditId, actor, selected.Authorization, request, audit, items.Count, tx, ct);
            await CompleteAsync(actor, tx, guards, ct);
            return new PersonReviewPage(proposals.Count, items.ToImmutable());
        }, cancellationToken);
    }

    public async Task<PersonReviewTargetPage> SearchTargetsAsync(ActorContext actor, string term, int offset, int limit,
        AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        var request = new PersonReviewSelectionRequest(PersonReviewSelectionKind.TargetSearch, Query: term, Offset: offset, Limit: limit).Normalize();
        AccessAdministrationService.ValidateReadMetadata(audit);
        var auditId = (await ids.NextAsync("authorizationReadEvents", cancellationToken)).Value;
        return await unit.ExecuteAsync(async (tx, ct) =>
        {
            var guards = new Guards();
            var selected = await ReviewSelectionAsync(tx, actor, request, guards, ct);
            var filter = F.In("_id", selected.Permit.GroupIds) & F.In("status", new[] { "active", "disputed" }) &
                ReviewAllDisclosed("memberUserIds", selected.Permit.DisclosureUserIds);
            var references = await Collection("personEquivalenceGroups").Find(tx, filter).Project(new BsonDocument("_id", 1))
                .Sort(new BsonDocument("_id", 1)).Limit(257).ToListAsync(ct);
            if (references.Count > 256 || references.Any(row => !ReviewPositive(row, "_id"))) throw Denied();
            var targets = new List<PersonWithdrawalTarget>();
            foreach (var reference in references)
            {
                var target = await new PersonWithdrawalTargetLoader(database).LoadAsync(tx, reference["_id"].AsInt64, ct);
                if (!target.UserIds.All(selected.Permit.DisclosureUserIds.Contains)) throw Denied();
                await target.TouchAsync(database, tx, ct);
                targets.Add(target);
            }
            var profiles = await ReviewProfilesAsync(tx, targets.SelectMany(target => target.UserIds).Distinct().Order().ToImmutableArray(), selected.Permit, ct);
            var matching = targets.Select(target => ReviewTarget(target, profiles))
                .Where(target => target.Accounts.Any(account =>
                    account.DisplayName?.Contains(request.Query!, StringComparison.OrdinalIgnoreCase) == true ||
                    account.VerifiedEmail.Contains(request.Query!, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(target => target.Accounts[0].DisplayName ?? target.Accounts[0].VerifiedEmail, StringComparer.OrdinalIgnoreCase)
                .ThenBy(target => target.GroupId).ToArray();
            var page = matching.Skip(request.Offset).Take(request.Limit).ToImmutableArray();
            await ReviewReadAuditAsync(auditId, actor, selected.Authorization, request, audit, page.Length, tx, ct);
            await CompleteAsync(actor, tx, guards, ct);
            return new PersonReviewTargetPage(matching.LongLength, page);
        }, cancellationToken);
    }

    public async Task<PersonReviewDetail> ReadReviewAsync(ActorContext actor, long proposalId,
        AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        var request = new PersonReviewSelectionRequest(PersonReviewSelectionKind.ProposalDetail, ReferenceId: proposalId).Normalize();
        AccessAdministrationService.ValidateReadMetadata(audit);
        var auditId = (await ids.NextAsync("authorizationReadEvents", cancellationToken)).Value;
        return await unit.ExecuteAsync(async (tx, ct) =>
        {
            var guards = new Guards();
            var selected = await ReviewSelectionAsync(tx, actor, request, guards, ct);
            var proposal = await ScopedReviewProposalAsync(tx, proposalId, selected.Permit, ct);
            var profiles = await ReviewProfilesAsync(tx, ReviewProposalUsers(proposal).Distinct().Order().ToImmutableArray(), selected.Permit, ct);
            var summary = await ReviewSummaryAsync(actor, proposal, profiles, selected.Authorization, selected.Person, tx, guards, ct);
            var targetUsers = proposal["target"]["userIds"].AsBsonArray.Select(value => value.AsInt64).ToHashSet();
            var accounts = profiles.Accounts.Where(account => targetUsers.Contains(account.UserId)).ToImmutableArray();
            PersonReviewDecision? decision = null;
            if (proposal.TryGetValue("resolution", out var raw))
            {
                var resolution = raw.AsBsonDocument;
                decision = new(profiles.Accounts.Single(account => account.UserId == resolution["actorId"].AsInt64),
                    resolution["reason"].AsString, PersonAuthorityBson.At(resolution, "decidedAt"));
            }
            await ReviewReadAuditAsync(auditId, actor, selected.Authorization, request, audit, 1, tx, ct);
            await CompleteAsync(actor, tx, guards, ct);
            return new PersonReviewDetail(summary, accounts, decision);
        }, cancellationToken);
    }

    // Current acting-person proof is separate from the structural targets being inspected.
    private async Task<(AuthorizationSnapshot Authorization, PersonAuthorityProof Person, PersonReviewSelectionPermit Permit)>
        ReviewSelectionAsync(IClientSessionHandle tx, ActorContext actor, PersonReviewSelectionRequest request, Guards guards, CancellationToken ct)
    {
        request = request.Normalize();
        var authorization = await AuthorizeAsync(actor, "read", tx, guards, ct);
        var permit = await selectionTrust.RequireAsync(tx, actor, request, ct) ?? throw Denied("person_selection_unavailable");
        await permit.TouchAsync(database, tx, actor, request, clock, ct);
        guards.Checks.Add(now => permit.RequireCurrent(database, tx, actor, request, now));
        var person = await PersonAsync(tx, actor.UserId, guards, ct);
        return (authorization, person, permit);
    }

    private static FilterDefinition<BsonDocument> ReviewAllDisclosed(string field, ImmutableArray<long> users) =>
        new BsonDocument(field, new BsonDocument
        {
            { "$type", "array" }, { "$not", new BsonDocument("$elemMatch", new BsonDocument("$nin", new BsonArray(users))) }
        });

    private static FilterDefinition<BsonDocument> ReviewProposalFilter(PersonReviewSelectionPermit permit) =>
        F.In("_id", permit.ProposalIds) & F.In("target.groupId", permit.GroupIds) &
        F.In("requestedBy", permit.DisclosureUserIds) & ReviewAllDisclosed("target.userIds", permit.DisclosureUserIds) &
        (F.Eq("status", "pending") | F.In("resolution.actorId", permit.DisclosureUserIds));

    private async Task<BsonDocument> ScopedReviewProposalAsync(IClientSessionHandle tx, long proposalId,
        PersonReviewSelectionPermit permit, CancellationToken ct)
    {
        if (!permit.ProposalIds.Contains(proposalId)) throw Denied();
        // The full conjunction precedes even the private proposal-body read.
        if (!await Collection("personReviewProposals").Find(tx, ReviewProposalFilter(permit) & F.Eq("_id", proposalId)).AnyAsync(ct))
            throw Denied();
        var proposal = await ProposalAsync(tx, proposalId, ct);
        if (!permit.GroupIds.Contains(proposal["target"]["groupId"].AsInt64) ||
            !ReviewProposalUsers(proposal).All(permit.DisclosureUserIds.Contains)) throw Denied();
        return proposal;
    }

    private static IEnumerable<long> ReviewProposalUsers(BsonDocument proposal)
    {
        foreach (var user in proposal["target"]["userIds"].AsBsonArray) yield return user.AsInt64;
        yield return proposal["requestedBy"].AsInt64;
        if (proposal.TryGetValue("resolution", out var resolution)) yield return resolution["actorId"].AsInt64;
    }

    private sealed record ReviewProfiles(ImmutableArray<PersonReviewAccount> Accounts, BsonArray Snapshots);

    private async Task<ReviewProfiles> ReviewProfilesAsync(IClientSessionHandle tx, ImmutableArray<long> userIds,
        PersonReviewSelectionPermit permit, CancellationToken ct)
    {
        if (userIds.IsDefault || userIds.Length > 258 || userIds.Any(id => id <= 0) ||
            !userIds.SequenceEqual(userIds.Distinct().Order()) || !userIds.All(permit.DisclosureUserIds.Contains)) throw Denied();
        if (userIds.IsEmpty) return new([], []);
        // No profile access is permitted until every required alias/participant is visible.
        var profiles = await Collection("accountProfiles").Find(tx, F.In("_id", userIds)).Limit(259).ToListAsync(ct);
        var identities = await Collection("identities").Find(tx, F.In("_id", userIds)).Limit(259).ToListAsync(ct);
        if (profiles.Count != userIds.Length || identities.Count != userIds.Length) throw Denied("person_profile_unavailable");
        var validatedProfiles = profiles.Select(ReviewProfileSnapshot).OrderBy(row => row["_id"].AsInt64).ToArray();
        var validatedIdentities = identities.Select(ReviewIdentitySnapshot).OrderBy(row => row["_id"].AsInt64).ToArray();
        if (!validatedProfiles.Select(row => row["_id"].AsInt64).SequenceEqual(userIds) ||
            !validatedIdentities.Select(row => row["_id"].AsInt64).SequenceEqual(userIds)) throw Denied("person_profile_unavailable");
        var snapshots = new BsonArray(); var accounts = ImmutableArray.CreateBuilder<PersonReviewAccount>(userIds.Length);
        // Complete preflight above; supported profile writers fence the same identity in their transaction.
        for (var index = 0; index < userIds.Length; index++)
        {
            var profile = validatedProfiles[index]; var identity = validatedIdentities[index];
            var changed = await Collection("identities").UpdateOneAsync(tx, PersonAuthorityBson.ExactFilter(identity, true),
                U.Inc("fence", 1L), cancellationToken: ct);
            if (changed.ModifiedCount != 1) throw Denied("person_profile_unavailable");
            var name = profile["displayName"].IsBsonNull ? null : profile["displayName"].AsString;
            accounts.Add(new(userIds[index], name, profile["verifiedEmail"].AsString, identity["status"].AsString));
            snapshots.Add(new BsonDocument
            {
                { "userId", userIds[index] }, { "version", profile["version"] }, { "displayName", profile["displayName"] },
                { "verifiedEmail", profile["verifiedEmail"] }, { "status", identity["status"] }
            });
        }
        return new(accounts.MoveToImmutable(), snapshots);
    }

    private BsonDocument ReviewProfileSnapshot(BsonDocument row)
    {
        string[] fields = ["_id", "schemaVersion", "version", "displayName", "verifiedEmail", "verifiedAt", "updatedAt"];
        if (!ReviewFields(row, fields) || !ReviewPositive(row, "_id") || !ReviewPositive(row, "version") ||
            !row["schemaVersion"].IsInt32 || row["schemaVersion"].AsInt32 != 1 ||
            !(row["displayName"].IsBsonNull || ReviewText(row["displayName"], 160)) || !ReviewText(row["verifiedEmail"], 254) ||
            !ReviewDate(row["verifiedAt"]) || !ReviewDate(row["updatedAt"]) || row["verifiedAt"].CompareTo(row["updatedAt"]) > 0 ||
            row["updatedAt"].AsBsonDateTime.MillisecondsSinceEpoch > clock.UtcNow.ToUnixTimeMilliseconds())
            throw Denied("person_profile_unavailable");
        try
        {
            if (OnboardingPolicy.NormalizeEmail(row["verifiedEmail"].AsString) != row["verifiedEmail"].AsString)
                throw Denied("person_profile_unavailable");
        }
        catch (UnitToko.Modules.Contracts.DomainValidationException) { throw Denied("person_profile_unavailable"); }
        return row.DeepClone().AsBsonDocument;
    }

    private static BsonDocument ReviewIdentitySnapshot(BsonDocument row)
    {
        string[] fields = ["_id", "schemaVersion", "issuer", "subject", "status", "grantVersion", "securityVersion", "fence"];
        if (!ReviewFields(row, fields, "securityVersion") || !ReviewPositive(row, "_id") || !ReviewPositive(row, "grantVersion") ||
            row.Contains("securityVersion") && !ReviewPositive(row, "securityVersion") ||
            !row["schemaVersion"].IsInt32 || row["schemaVersion"].AsInt32 != 1 ||
            !ReviewText(row["issuer"], 2048) || !ReviewText(row["subject"], 512) ||
            !row["status"].IsString || row["status"].AsString is not ("pending" or "active" or "suspended" or "revoked") ||
            !row["fence"].IsInt64 || row["fence"].AsInt64 is < 0 or long.MaxValue) throw Denied("person_profile_unavailable");
        return row.DeepClone().AsBsonDocument;
    }

    private static bool ReviewFields(BsonDocument row, string[] fields, string? optional = null) =>
        row.ElementCount == row.Names.Distinct(StringComparer.Ordinal).Count() && row.Names.All(fields.Contains) &&
        fields.Where(field => field != optional).All(row.Contains);
    private static bool ReviewPositive(BsonDocument row, string field) =>
        row.TryGetValue(field, out var value) && value.IsInt64 && value.AsInt64 > 0;
    private static bool ReviewText(BsonValue value, int maximum) => value.IsString &&
        !string.IsNullOrWhiteSpace(value.AsString) && value.AsString.Length <= maximum && !value.AsString.Any(char.IsControl);
    private static bool ReviewDate(BsonValue value) => value.IsBsonDateTime &&
        value.AsBsonDateTime.MillisecondsSinceEpoch >= new BsonDateTime(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)).MillisecondsSinceEpoch &&
        value.AsBsonDateTime.MillisecondsSinceEpoch <= new BsonDateTime(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)).MillisecondsSinceEpoch;

    private static PersonReviewTarget ReviewTarget(PersonWithdrawalTarget target, ReviewProfiles profiles) =>
        new(target.GroupId, target.GroupVersion, target.GroupStatus,
            target.UserIds.Select(id => profiles.Accounts.Single(account => account.UserId == id)).ToImmutableArray());

    private async Task<PersonReviewSummary> ReviewSummaryAsync(ActorContext actor, BsonDocument proposal, ReviewProfiles profiles,
        AuthorizationSnapshot authorization, PersonAuthorityProof person, IClientSessionHandle tx, Guards guards, CancellationToken ct)
    {
        var actions = ImmutableArray.CreateBuilder<string>();
        if (proposal["status"] == "pending")
        {
            foreach (var action in new[] { "approve", "reject", "withdraw" })
            {
                if (!ScopePolicy.Authorize(actor, authorization, Permission(action), Administrative, clock.UtcNow).Allowed) continue;
                var requester = proposal["requestedBy"].AsInt64;
                if (action == "withdraw" ? actor.UserId != requester :
                    actor.UserId == requester || person.Evidence.Single().PersonId == proposal["requesterPerson"]["canonicalPersonId"].AsInt64) continue;
                var actionGuards = new Guards();
                try
                {
                    await AuthorizeAsync(actor, action, tx, actionGuards, ct);
                    var permit = await PermitAsync(tx, actor.UserId, action, Intent(proposal), actionGuards, ct);
                    if (!Equal(permit.Pin, proposal["policy"].AsBsonDocument)) continue;
                    if (action == "approve")
                    {
                        await RequireRequesterAsync(actor, proposal, tx, actionGuards, ct);
                        var target = await new PersonWithdrawalTargetLoader(database).LoadAsync(tx, proposal["target"]["groupId"].AsInt64, ct);
                        if (!Equal(target.Snapshot, proposal["target"].AsBsonDocument)) continue;
                        var people = person.CopyRecords().Where(row => row.Collection == "personRecords").Select(row => row.Document["_id"].AsInt64);
                        var users = person.CopyRecords().Where(row => row.Collection == "personAccountBindings").Select(row => row.Document["_id"].AsInt64);
                        if (people.Intersect(target.PersonIds).Any() || users.Intersect(target.UserIds).Any()) continue;
                        await target.TouchAsync(database, tx, ct);
                    }
                    actionGuards.Check(clock.UtcNow);
                    guards.Checks.AddRange(actionGuards.Checks); actions.Add(action);
                }
                catch (AuthorizationDeniedException) { /* A visible historical request need not remain actionable. */ }
                catch (PersistenceConflictException) { /* Changed current closure removes the approval affordance. */ }
            }
        }
        return new(Result(proposal), profiles.Accounts.Single(account => account.UserId == proposal["requestedBy"].AsInt64),
            proposal["reason"].AsString, proposal["target"]["userIds"].AsBsonArray.Count, actions.ToImmutable());
    }

    private Task ReviewReadAuditAsync(long auditId, ActorContext actor, AuthorizationSnapshot authorization,
        PersonReviewSelectionRequest request, AuthorizationReadMetadata audit, int resultCount, IClientSessionHandle tx, CancellationToken ct) =>
        this.authorization.RecordPrivilegedReadAsync(auditId, actor, authorization, "access.history.read", audit,
            new BsonDocument { { "operation", "person.review." + request.Kind.ToString().ToLowerInvariant() },
                { "offset", request.Offset }, { "limit", request.Limit }, { "keyHash", request.Digest } },
            resultCount, [new(GrantScopeKind.System)], tx, ct);

}
