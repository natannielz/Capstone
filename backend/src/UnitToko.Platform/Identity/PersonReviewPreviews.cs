using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Identity;

public sealed partial class PersonWithdrawalService
{
    public Task<PersonReviewPreview> PreviewProposeAsync(ActorContext actor, long groupId, long expectedVersion,
        AuthorizationReadMetadata audit, CancellationToken cancellationToken = default) =>
        CreateReviewPreviewAsync(actor, "propose", groupId, expectedVersion, audit, cancellationToken);
    public Task<PersonReviewPreview> PreviewApproveAsync(ActorContext actor, long proposalId, long expectedVersion,
        AuthorizationReadMetadata audit, CancellationToken cancellationToken = default) =>
        CreateReviewPreviewAsync(actor, "approve", proposalId, expectedVersion, audit, cancellationToken);
    public Task<PersonWithdrawalResult> SubmitProposalAsync(ActorContext actor, string previewReference,
        AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default) =>
        ProposeCoreAsync(actor, 0, 0, metadata, cancellationToken, PreviewReference(previewReference));
    public Task<PersonWithdrawalResult> ApproveReviewedAsync(ActorContext actor, long proposalId, long expectedVersion,
        string previewReference, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default) =>
        DecideAsync(actor, proposalId, expectedVersion, "approve", metadata, cancellationToken, PreviewReference(previewReference));

    private static string PreviewReference(string reference) => reference is { Length: 36 } &&
        Guid.TryParseExact(reference, "D", out var parsed) && parsed.ToString("D") == reference &&
        reference[14] == '4' && "89ab".Contains(reference[19]) ? reference : throw Denied("person_preview_invalid");

    private async Task<PersonReviewPreview> CreateReviewPreviewAsync(ActorContext actor, string kind, long id,
        long version, AuthorizationReadMetadata audit, CancellationToken cancellationToken)
    {
        Version(id, version); AccessAdministrationService.ValidateReadMetadata(audit);
        var previewId = (await ids.NextAsync("personReviewPreviews", cancellationToken)).Value;
        var auditId = (await ids.NextAsync("authorizationReadEvents", cancellationToken)).Value;
        var reference = Guid.NewGuid().ToString("D");
        return await unit.ExecuteAsync(async (tx, ct) =>
        {
            var guards = new Guards();
            var request = new PersonReviewSelectionRequest(kind == "propose" ? PersonReviewSelectionKind.TargetPreview :
                PersonReviewSelectionKind.ProposalDetail, ReferenceId: id, ExpectedVersion: version).Normalize();
            var selection = await ReviewSelectionAsync(tx, actor, request, guards, ct);
            var actionAuthorization = await AuthorizeAsync(actor, kind, tx, guards, ct);
            BsonDocument? proposal = null;
            if (kind == "approve")
            {
                proposal = await ScopedReviewProposalAsync(tx, id, selection.Permit, ct);
                if (proposal["version"] != version || proposal["status"] != "pending") throw Conflict();
            }
            var groupId = proposal is null ? id : proposal["target"]["groupId"].AsInt64;
            if (!selection.Permit.GroupIds.Contains(groupId)) throw Denied();
            var target = await new PersonWithdrawalTargetLoader(database).LoadAsync(tx, groupId, ct);
            if (proposal is null ? target.GroupVersion != version : !Equal(target.Snapshot, proposal["target"].AsBsonDocument)) throw Conflict();
            var userIds = proposal is null ? target.UserIds : target.UserIds.Append(proposal["requestedBy"].AsInt64).Distinct().Order().ToImmutableArray();
            var profiles = await ReviewProfilesAsync(tx, userIds, selection.Permit, ct);
            var intent = proposal is null ? new PersonWithdrawalIntent(0, request.Digest, target.Snapshot) : Intent(proposal);
            var appointed = await PermitAsync(tx, actor.UserId, proposal is null ? "previewPropose" : "approve", intent, guards, ct);
            if (!Equal(appointed.Pin, selection.Permit.PolicyPin) || proposal is not null && !Equal(appointed.Pin, proposal["policy"].AsBsonDocument))
                throw Denied("person_policy_changed");
            if (proposal is not null)
            {
                await RequireRequesterAsync(actor, proposal, tx, guards, ct);
                var person = selection.Person;
                if (actor.UserId == proposal["requestedBy"].AsInt64 || person.Evidence.Single().PersonId == proposal["requesterPerson"]["canonicalPersonId"].AsInt64 ||
                    person.CopyRecords().Any(row => row.Collection == "personRecords" && target.PersonIds.Contains(row.Document["_id"].AsInt64) ||
                        row.Collection == "personAccountBindings" && target.UserIds.Contains(row.Document["_id"].AsInt64)))
                    throw Denied("independent_person_required");
            }
            await target.TouchAsync(database, tx, ct);
            var expiresAt = await ReviewPreviewExpiryAsync(actor, kind, selection.Permit, selection.Person,
                selection.Authorization, actionAuthorization, tx, ct);
            guards.Checks.Add(now => { if (now >= expiresAt) throw Denied("person_preview_expired"); });
            var row = new BsonDocument
            {
                { "_id", previewId }, { "schemaVersion", 1 }, { "reference", reference }, { "kind", kind },
                { "status", "ready" }, { "version", 1L }, { "actorId", actor.UserId }, { "grantVersion", actor.GrantVersion },
                { "securityVersion", await ActorSecurityAsync(tx, actor, ct) },
                { "session", new BsonDocument { { "id", actor.Session!.Id }, { "version", actor.Session.Version }, { "securityVersion", actor.Session.SecurityVersion } } },
                { "createdAt", clock.UtcNow.UtcDateTime }, { "expiresAt", expiresAt.UtcDateTime },
                { "selectionDigest", request.Digest }, { "policy", selection.Permit.PolicyPin }, { "target", target.Snapshot }, { "profiles", profiles.Snapshots }
            };
            if (proposal is not null)
            {
                row.Add("proposalId", proposal["_id"]); row.Add("proposalVersion", proposal["version"]);
                row.Add("proposalDigest", proposal["requestDigest"]); row.Add("approvalRequestedBy", proposal["requestedBy"]);
            }
            if (!PersonReviewPreviewSchemas.IsValid("personReviewPreviews", row)) throw Denied("person_preview_invalid");
            var summary = proposal is null ? null : await ReviewSummaryAsync(actor, proposal, profiles,
                selection.Authorization, selection.Person, tx, guards, ct);
            await Collection("personReviewPreviews").InsertOneAsync(tx, row, cancellationToken: ct);
            await authorization.RecordPrivilegedReadAsync(auditId, actor, selection.Authorization, "access.history.read", audit,
                new BsonDocument("operation", "person.review.preview/" + kind), 1, [new(GrantScopeKind.System)], tx, ct);
            await CompleteAsync(actor, tx, guards, ct);
            return new PersonReviewPreview(reference, kind, expiresAt,
                new(target.GroupId, target.GroupVersion, target.GroupStatus, [.. profiles.Accounts.Where(account => target.UserIds.Contains(account.UserId))]), summary);
        }, cancellationToken);
    }

    private async Task<DateTimeOffset> ReviewPreviewExpiryAsync(ActorContext actor, string action,
        PersonReviewSelectionPermit selection, PersonAuthorityProof person, AuthorizationSnapshot read,
        AuthorizationSnapshot write, IClientSessionHandle tx, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var end = now.AddMinutes(5);
        void Cap(DateTimeOffset? value) { if (value is { } limit && limit < end) end = limit; }
        Cap(selection.ValidUntil); Cap(person.ValidUntil);
        Cap(actor.FactorCompletedAt + Permissions.Find(Permission(action))!.Assurance.MaximumAge!.Value);
        foreach (var (snapshot, permission) in new[] { (read, Permissions.PersonReviewRead), (write, Permission(action)) })
        {
            var decision = ScopePolicy.Authorize(actor, snapshot, permission, Administrative, now);
            if (!decision.Allowed) throw Denied(decision.Code);
            foreach (var grant in snapshot.Grants.Where(grant => decision.GrantIds.Contains(grant.Id)))
            {
                Cap(grant.ExpiresAt);
                foreach (var member in snapshot.Memberships.Where(member => member.Id == grant.MembershipId)) Cap(member.ExpiresAt);
            }
        }
        if (actor.Session is not { } reference) throw Denied("stale_or_inactive_session");
        var session = await Collection("identitySessions").Find(tx,
            SessionPersistence.LiveFilter(reference, actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, now))
            .FirstOrDefaultAsync(ct) ?? throw Denied("stale_or_inactive_session");
        Cap(PersonAuthorityBson.At(session, "idleExpiresAt")); Cap(PersonAuthorityBson.At(session, "absoluteExpiresAt"));
        // Mongo date precision is milliseconds; the returned deadline is the exact persisted deadline.
        end = DateTimeOffset.FromUnixTimeMilliseconds(end.ToUnixTimeMilliseconds());
        if (end <= clock.UtcNow) throw Denied("person_preview_expired");
        return end;
    }

    private async Task<BsonDocument> ReviewedPreviewAsync(ActorContext actor, string kind, string reference,
        long? proposalId, long? version, IClientSessionHandle tx, Guards guards, CancellationToken ct)
    {
        var row = await Collection("personReviewPreviews").Find(tx,
            F.Eq("reference", PreviewReference(reference)) & F.Eq("actorId", actor.UserId)).FirstOrDefaultAsync(ct);
        if (row is null || !PersonReviewPreviewSchemas.IsValid("personReviewPreviews", row) || row["kind"] != kind ||
            row["actorId"] != actor.UserId || row["grantVersion"] != actor.GrantVersion ||
            row["securityVersion"].AsInt64 != await ActorSecurityAsync(tx, actor, ct) || actor.Session is not { } session ||
            row["session"]["id"] != session.Id || row["session"]["version"] != session.Version ||
            row["session"]["securityVersion"] != session.SecurityVersion) throw Denied("person_preview_invalid");
        if (row["status"] != "ready") throw Conflict();
        var expiresAt = PersonAuthorityBson.At(row, "expiresAt");
        var createdAt = PersonAuthorityBson.At(row, "createdAt");
        void Current(DateTimeOffset now)
        {
            if (now < createdAt || now >= expiresAt) throw Denied("person_preview_expired");
        }
        Current(clock.UtcNow); guards.Checks.Add(Current);
        var targetSnapshot = row["target"].AsBsonDocument;
        if (kind == "approve" && (row["proposalId"].AsInt64 != proposalId || row["proposalVersion"].AsInt64 != version)) throw Conflict();
        var request = kind == "propose"
            ? new PersonReviewSelectionRequest(PersonReviewSelectionKind.TargetPreview,
                ReferenceId: targetSnapshot["groupId"].AsInt64, ExpectedVersion: targetSnapshot["groupVersion"].AsInt64)
            : new PersonReviewSelectionRequest(PersonReviewSelectionKind.ProposalDetail, ReferenceId: proposalId, ExpectedVersion: version);
        var selection = await ReviewSelectionAsync(tx, actor, request, guards, ct);
        if (request.Digest != row["selectionDigest"].AsString || !Equal(selection.Permit.PolicyPin, row["policy"].AsBsonDocument))
            throw Denied("person_policy_changed");
        if (!selection.Permit.GroupIds.Contains(targetSnapshot["groupId"].AsInt64)) throw Denied();
        var target = await new PersonWithdrawalTargetLoader(database).LoadAsync(tx, targetSnapshot["groupId"].AsInt64, ct);
        if (!Equal(target.Snapshot, targetSnapshot)) throw Conflict();
        var userIds = target.UserIds;
        if (kind == "approve")
        {
            var proposal = await ScopedReviewProposalAsync(tx, proposalId!.Value, selection.Permit, ct);
            if (proposal["status"] != "pending" || proposal["version"].AsInt64 != version || proposal["requestDigest"] != row["proposalDigest"] ||
                proposal["requestedBy"] != row["approvalRequestedBy"] || !Equal(proposal["target"].AsBsonDocument, targetSnapshot)) throw Conflict();
            userIds = userIds.Append(proposal["requestedBy"].AsInt64).Distinct().Order().ToImmutableArray();
        }
        var profiles = await ReviewProfilesAsync(tx, userIds, selection.Permit, ct);
        if (!Equal(new BsonDocument("profiles", profiles.Snapshots), new BsonDocument("profiles", row["profiles"]))) throw Conflict();
        await target.TouchAsync(database, tx, ct);
        return row;
    }

    private async Task ConsumeReviewPreviewAsync(BsonDocument row, BsonDocument proposal, long historyId,
        AuthorizationCommandMetadata metadata, BsonDocument actionPolicy, IClientSessionHandle tx, CancellationToken ct)
    {
        if (!Equal(row["policy"].AsBsonDocument, actionPolicy)) throw Denied("person_policy_changed");
        var next = row.DeepClone().AsBsonDocument;
        next["version"] = 2L; next["status"] = "consumed";
        next.Add("consumption", new BsonDocument
        {
            { "operation", "person.review." + row["kind"].AsString },
            { "keyHash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(metadata.IdempotencyKey))).ToLowerInvariant() },
            { "resultProposalId", proposal["_id"] }, { "resultHistoryId", historyId }, { "consumedAt", clock.UtcNow.UtcDateTime }
        });
        if (!PersonReviewPreviewSchemas.IsValid("personReviewPreviews", next)) throw Denied("person_preview_expired");
        var result = await Collection("personReviewPreviews").ReplaceOneAsync(tx,
            F.And(new FilterDefinition<BsonDocument>[] { PersonReviewPreviewSchemas.Validator("personReviewPreviews") }
                .Concat(row.Select(field => F.Eq(field.Name, field.Value)))), next, cancellationToken: ct);
        if (result.ModifiedCount != 1) throw Conflict();
    }
}
