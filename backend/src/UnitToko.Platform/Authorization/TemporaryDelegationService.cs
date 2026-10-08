using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using R = UnitToko.Platform.Authorization.TemporaryDelegationRecords;

namespace UnitToko.Platform.Authorization;

public sealed record TemporaryDelegationSummary(long Id, long Revision, long SourceUserId, long ApprovedBy,
    TemporaryDelegationRequest Request, string Status, string SourceState, string Reason, DateTimeOffset UpdatedAt,
    ImmutableArray<string> AllowedActions, string TaskReassignmentState = "unavailable");
public sealed record TemporaryDelegationPage(ImmutableArray<TemporaryDelegationSummary> Items, long? NextId);
public sealed record TemporaryDelegationEvent(long Id, long Revision, long ActorId, string Action, string Reason, DateTimeOffset RecordedAt);
public sealed record TemporaryDelegationSource(long Id, long Version, GrantScope Scope, ImmutableArray<string> Permissions,
    DateTimeOffset EffectiveAt, DateTimeOffset? ExpiresAt);

public sealed class TemporaryDelegationService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private readonly AuthorizationStore authorization = new(database, unit, clock);
    private readonly TemporaryDelegationAuthorityReader dependencies = new(database, clock);
    private static readonly ResourceScope Administrative = new(IsAdministrative: true);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static void Metadata(AuthorizationCommandMetadata value) {
        AuthorizationStore.ValidateMetadata(value);
        if (value.IdempotencyKey.Length > 128 || value.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')) ||
            value.Reason.Trim() != value.Reason || value.Reason.Length > 1000 || value.Reason.Any(char.IsControl)) throw R.Invalid();
    }
    private async Task StorageAsync(CancellationToken ct) {
        if (!await new CollectionMigrations(database, clock).IsCurrentAsync(ct)) throw R.Invalid();
        await TemporaryDelegationStorage.RequireAsync(database, ct);
    }
    private AuthorizationDecision ProposeAuthority(ActorContext actor, AuthorizationSnapshot snapshot) {
        var result = ScopePolicy.Authorize(actor, Independent(snapshot), Permissions.AccessPropose, Administrative, clock.UtcNow);
        if (!result.Allowed) throw new AuthorizationDeniedException(result.Code); return result;
    }
    private AuthorizationDecision ScopedAuthority(ActorContext actor, AuthorizationSnapshot snapshot, string permission, GrantScope scope) {
        var query = ScopePolicy.Compile(actor, Independent(snapshot), permission, clock.UtcNow); AuthorizationStore.Require(query);
        var grants = query.Clauses.Where(c => ScopePolicy.ContainsScope(c.Scope, scope)).Select(c => c.GrantId).ToImmutableArray();
        if (grants.IsEmpty) throw new AuthorizationDeniedException("scope_denied"); return new(true, "allowed", grants);
    }
    private AuthorizationDecision ApproveAuthority(ActorContext actor, AuthorizationSnapshot snapshot, BsonDocument state) {
        var request = R.Request(state["request"]);
        var proposed = new PermissionGrant(state["_id"].AsInt64, request.BeneficiaryUserId, request.BeneficiaryMembershipId,
            TemporaryDelegationPolicy.TemplateId, 1, request.Permissions, request.Scope, AuthorizationStatus.Pending,
            state["sourceUserId"].AsInt64, 0, state["proposalReason"].AsString, request.EffectiveAt, request.ExpiresAt, DelegablePermissions: []);
        var decision = AccessGrantPolicy.CanApprove(actor, Independent(snapshot), proposed, clock.UtcNow);
        if (!decision.Allowed) throw new AuthorizationDeniedException(decision.Code); return decision;
    }
    private static AuthorizationSnapshot Independent(AuthorizationSnapshot snapshot) => snapshot with {
        Grants = snapshot.Grants.Where(grant => grant.TemplateId != TemporaryDelegationPolicy.TemplateId).ToImmutableArray() };
    // Match the existing personal-account read policy, then narrow it to the delegation's
    // participant membership. A stored recipient ID or expired derived grant is never a read grant.
    private ImmutableArray<ScopeClause> ParticipantClauses(ActorContext actor, AuthorizationSnapshot snapshot) {
        if (!AssurancePolicy.Satisfies(actor, new(true), clock.UtcNow)) throw new AuthorizationDeniedException("mfa_required");
        var current = Independent(snapshot);
        var clauses = current.Grants.SelectMany(grant => grant.Permissions.IsDefault ? [] : grant.Permissions).Distinct(StringComparer.Ordinal)
            .SelectMany(permission => ScopePolicy.CompileParticipantGrants(actor, current, permission, clock.UtcNow).Clauses).Distinct().ToImmutableArray();
        if (clauses.IsEmpty) throw new AuthorizationDeniedException("delegation_caller_scope_unavailable"); return clauses;
    }
    private ImmutableArray<ScopeClause> ReadClauses(ActorContext actor, AuthorizationSnapshot snapshot, BsonDocument head) {
        var current = Independent(snapshot); var own = ParticipantClauses(actor, snapshot); var request = R.Request(head["request"]);
        var result = ImmutableArray.CreateBuilder<ScopeClause>();
        if (head["sourceUserId"] == actor.UserId)
            result.AddRange(ScopePolicy.Compile(actor, current, Permissions.AccessPropose, clock.UtcNow).Clauses.Where(c => c.Matches(Administrative)));
        if (request.BeneficiaryUserId == actor.UserId)
            result.AddRange(own.Where(c => c.MembershipId == request.BeneficiaryMembershipId && ScopePolicy.ContainsScope(c.Scope, request.Scope)));
        result.AddRange(ScopePolicy.Compile(actor, current, Permissions.AccessAuditRead, clock.UtcNow).Clauses.Where(c => c.Matches(Administrative)));
        foreach (var permission in new[] { Permissions.AccessApprove, Permissions.AccessRevoke })
            result.AddRange(ScopePolicy.Compile(actor, current, permission, clock.UtcNow).Clauses.Where(c => ScopePolicy.ContainsScope(c.Scope, request.Scope)));
        return result.Distinct().ToImmutableArray();
    }
    private ImmutableArray<ScopeClause> RequireRead(ActorContext actor, AuthorizationSnapshot snapshot, BsonDocument head) {
        var clauses = ReadClauses(actor, snapshot, head); if (clauses.IsEmpty) throw new AuthorizationDeniedException("delegation_caller_scope_unavailable"); return clauses;
    }
    private async Task RequireReadAsync(ActorContext actor, AuthorizationSnapshot snapshot, BsonDocument head, IClientSessionHandle tx, CancellationToken ct) {
        var clauses = RequireRead(actor, snapshot, head);
        await authorization.FenceAsync(actor, clauses.SelectMany(c => c.RequiredNodes).Distinct(), tx, ct);
        _ = RequireRead(actor, snapshot, head);
    }
    private static ResourceScope Resource(GrantScope scope) => scope.Kind switch {
        GrantScopeKind.System => Administrative,
        GrantScopeKind.BuyingOrganization or GrantScopeKind.BuyingBranch or GrantScopeKind.BuyingDivision =>
            new(BuyingOrganizationId: scope.OrganizationId, BranchId: scope.BranchId, DivisionId: scope.DivisionId),
        _ => new(FulfillmentOrganizationId: scope.OrganizationId, ShopId: scope.ShopId, WarehouseId: scope.WarehouseId, ChannelId: scope.ChannelId) };
    private async Task<PersonAuthorityProof> IndependentAsync(long source, long beneficiary, IClientSessionHandle tx, CancellationToken ct) {
        if (source == beneficiary) throw new AuthorizationDeniedException("independent_person_required");
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, [source, beneficiary], ct);
        if (!PersonIndependencePolicy.AreIndependent(proof.Evidence.Single(p => p.UserId == source), proof.Evidence.Single(p => p.UserId == beneficiary)))
            throw new AuthorizationDeniedException("independent_person_required");
        await new PersonAuthorizationFence(database, clock).TouchAsync(tx, proof, ct); return proof;
    }
    private async Task<BsonDocument> ProposalStateAsync(ActorContext actor, AuthorizationSnapshot snapshot, TemporaryDelegationRequest request,
        long id, long eventId, AuthorizationCommandMetadata metadata, IClientSessionHandle tx, CancellationToken ct) {
        TemporaryDelegationPolicy.Validate(request); ProposeAuthority(actor, snapshot);
        var source = snapshot.Grants.SingleOrDefault(grant => grant.Id == request.SourceGrantId) ?? throw R.Invalid();
        var membership = snapshot.Memberships.SingleOrDefault(member => member.Id == source.MembershipId) ?? throw R.Invalid();
        var beneficiaryRow = await Rows("memberships").Find(tx, F.Eq("_id", request.BeneficiaryMembershipId)).SingleOrDefaultAsync(ct) ?? throw R.Invalid();
        var beneficiary = AuthorizationBson.Membership(beneficiaryRow);
        if (!TemporaryDelegationPolicy.SourceAllows(request, source, membership, beneficiary, snapshot.Directory, actor.UserId, clock.UtcNow)) throw R.Invalid();
        var now = new BsonDateTime(clock.UtcNow.UtcDateTime);
        return new() { { "_id", id }, { "schemaVersion", 1 }, { "eventId", eventId }, { "revision", 1L }, { "sourceUserId", actor.UserId },
            { "sourceIdentityVersion", actor.GrantVersion }, { "request", R.Request(request) }, { "sourceGrant", AuthorizationBson.Grant(source) },
            { "sourceMembership", AuthorizationBson.Membership(membership) }, { "beneficiaryMembership", AuthorizationBson.Membership(beneficiary) },
            { "sourceNodes", new BsonArray(ScopePolicy.RequiredNodes(source.Scope, snapshot.Directory).Concat(ScopePolicy.RequiredNodes(membership.Scope, snapshot.Directory)).Concat(ScopePolicy.RequiredNodes(beneficiary.Scope, snapshot.Directory)).Distinct().OrderBy(n => n.Id).Select(n => { var row = AuthorizationBson.Node(n); row.Remove("fence"); return row; })) },
            { "proposalReason", metadata.Reason }, { "status", "pending" }, { "approvedBy", 0L }, { "grantVersion", 0L },
            { "revokedAt", BsonNull.Value }, { "createdAt", now }, { "updatedAt", now }, { "fence", 0L } };
    }

    public async Task<BsonDocument> ChangeAsync(ActorContext actor, string action, long? id, long revision,
        TemporaryDelegationRequest? request, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        Metadata(metadata);
        if (action is not ("propose" or "approve" or "revoke" or "handover") || (action == "propose" ? id is not null || revision != 0 || request is null :
            id is null or <= 0 || revision is <= 0 or long.MaxValue || request is not null)) throw R.Invalid();
        if (request is not null) TemporaryDelegationPolicy.Validate(request); await StorageAsync(ct);
        // Reserve from the ordinary grant sequence so approval can never collide with a grant ID.
        var selected = id ?? (await ids.NextAsync("authorizationGrants", ct)).Value;
        var eventId = (await ids.NextAsync(R.Events, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = R.Body(action, id, revision, request); var hash = AuthorizationStore.CommandHash(body, metadata.Reason);
        BsonDocument? before = null; BsonDocument? proposal = null; BsonDocument? callerHead = null; AuthorizationSnapshot? callerSnapshot = null;
        PersonAuthorityProof? people = null; DateTimeOffset? deadline = null;
        return await authorization.ExecuteCommandAsync(actor, R.Operation, metadata, body, async (snapshot, tx, token) => {
            before = null; proposal = null; callerHead = null; callerSnapshot = null; people = null; deadline = null;
            var originals = await Rows(R.Events).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).Limit(2).ToListAsync(token);
            if (originals.Count > 1) throw R.Invalid();
            if (originals.Count == 1) await R.EventAsync(database, tx, originals[0]["_id"].AsInt64, token);
            else if (await Rows("auditEvents").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", R.Operation) & F.Eq("requestKey", metadata.IdempotencyKey)).AnyAsync(token)) throw R.Invalid();
            var original = originals.SingleOrDefault();
            if (action == "propose") {
                proposal = original is null ? await ProposalStateAsync(actor, snapshot, request!, selected, eventId, metadata, tx, token) : original["after"].AsBsonDocument;
                ProposeAuthority(actor, snapshot);
            } else {
                before = await R.HeadAsync(database, tx, id!.Value, token);
                await RequireReadAsync(actor, snapshot, before, tx, token);
            }
            var state = proposal ?? before!; var actual = R.Request(state["request"]); var scope = actual.Scope;
            if (action is "propose" or "approve") {
                deadline = await dependencies.SourceDeadlineAsync(state, snapshot.Directory, tx, token) ?? throw new AuthorizationDeniedException("delegation_source_changed");
                people = await IndependentAsync(state["sourceUserId"].AsInt64, actual.BeneficiaryUserId, tx, token);
            }
            if (action == "propose") return new(ProposeAuthority(actor, snapshot), ScopePolicy.RequiredNodes(scope, snapshot.Directory));
            if (action == "approve") {
                var evidenceRevision = original is null ? revision : original["revision"].AsInt64 - 1;
                var decision = ApproveAuthority(actor, snapshot, state);
                var approval = new ApprovalRequest(evidenceRevision, evidenceRevision, ProposedBy: state["sourceUserId"].AsInt64, BeneficiaryId: actual.BeneficiaryUserId);
                return new(decision, ScopePolicy.RequiredNodes(scope, snapshot.Directory), Permissions.AccessApprove, new(Resource(scope), approval));
            }
            if (action == "revoke") {
                var decision = state["sourceUserId"] == actor.UserId ? ProposeAuthority(actor, snapshot) : ScopedAuthority(actor, snapshot, Permissions.AccessRevoke, scope);
                return new(decision, []);
            }
            if (actor.UserId != state["sourceUserId"] && actor.UserId != actual.BeneficiaryUserId) throw R.Invalid();
            if (!AssurancePolicy.Satisfies(actor, Permissions.Find(Permissions.AccessPropose)!.Assurance, clock.UtcNow)) throw new AuthorizationDeniedException("step_up_required");
            // A participant may acknowledge handover history. This grants no permission or task ownership.
            return new(new(true, "allowed", []), []);
        }, async (tx, token) => {
            if (before is not null && before["revision"] != revision) throw new PersistenceConflictException("delegation_changed", "Reload the delegation before reviewing again");
            var head = (proposal ?? before!).DeepClone().AsBsonDocument; var now = new BsonDateTime(clock.UtcNow.UtcDateTime);
            if (before is not null && now.CompareTo(before["updatedAt"]) < 0) throw R.Invalid();
            if (action == "propose") head["createdAt"] = now;
            if (action == "approve") {
                if (head["status"] != "pending") throw new PersistenceConflictException("delegation_changed", "Only an unchanged pending delegation can be approved");
                head["status"] = "active"; head["approvedBy"] = actor.UserId; head["grantVersion"] = 1L;
                await Rows("authorizationGrants").InsertOneAsync(tx, AuthorizationBson.Grant(R.Grant(head)), cancellationToken: token);
                await BumpAsync(R.Request(head["request"]).BeneficiaryUserId, tx, token);
            } else if (action == "revoke") {
                if (head["status"] == "revoked") throw new PersistenceConflictException("delegation_changed", "Delegation is already revoked");
                var active = head["status"] == "active"; head["status"] = "revoked"; head["revokedAt"] = now;
                if (active) {
                    var expected = AuthorizationBson.Grant(R.Grant(before!)); head["grantVersion"] = head["grantVersion"].AsInt64 + 1;
                    if ((await Rows("authorizationGrants").ReplaceOneAsync(tx, expected, AuthorizationBson.Grant(R.Grant(head)), cancellationToken: token)).ModifiedCount != 1) throw R.Invalid();
                    await BumpAsync(R.Request(head["request"]).BeneficiaryUserId, tx, token);
                }
            }
            head["eventId"] = eventId; head["revision"] = revision + 1; head["updatedAt"] = now; head["fence"] = 0L;
            var row = new BsonDocument { { "_id", eventId }, { "schemaVersion", 1 }, { "delegationId", head["_id"] }, { "revision", revision + 1 },
                { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "action", action }, { "requestKey", metadata.IdempotencyKey },
                { "bodyHash", hash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "recordedAt", now }, { "auditId", auditId },
                { "before", before is null ? BsonNull.Value : R.State(before) }, { "after", R.State(head) } };
            await Rows(R.Events).InsertOneAsync(tx, row, cancellationToken: token);
            if (before is null) await Rows(R.Heads).InsertOneAsync(tx, head, cancellationToken: token);
            else if ((await Rows(R.Heads).ReplaceOneAsync(tx, before, head, cancellationToken: token)).ModifiedCount != 1) throw R.Invalid();
            await Rows("auditEvents").InsertOneAsync(tx, R.Audit(row), cancellationToken: token); return R.Result(row);
        }, ct, async (snapshot, tx, token) => {
            var original = await OriginalAsync(actor, metadata.IdempotencyKey, tx, token) ?? throw R.Invalid();
            var head = await R.HeadAsync(database, tx, original["delegationId"].AsInt64, token);
            callerHead = head; callerSnapshot = snapshot;
            await RequireReadAsync(actor, snapshot, head, tx, token);
            if (action is "propose" or "approve") {
                deadline = await dependencies.SourceDeadlineAsync(head, snapshot.Directory, tx, token) ?? throw new AuthorizationDeniedException("delegation_source_changed");
                await new PersonAuthorizationFence(database, clock).TouchAsync(tx, people!, token);
            }
            await dependencies.TouchAsync(head, tx, token); await authorization.FenceAsync(actor, [], tx, token);
            if (action == "propose" || action == "revoke" && head["sourceUserId"] == actor.UserId) ProposeAuthority(actor, snapshot);
            else if (action == "approve") ApproveAuthority(actor, snapshot, head);
            else if (action == "revoke") ScopedAuthority(actor, snapshot, Permissions.AccessRevoke, R.Request(head["request"]).Scope);
            if (action == "handover" && !AssurancePolicy.Satisfies(actor, Permissions.Find(Permissions.AccessPropose)!.Assurance, clock.UtcNow)) throw new AuthorizationDeniedException("step_up_required");
            _ = RequireRead(actor, snapshot, head);
        }, validateCurrent: () => {
            if (callerHead is null || callerSnapshot is null) throw R.Invalid();
            _ = RequireRead(actor, callerSnapshot, callerHead);
            if (action == "propose" || action == "revoke" && callerHead["sourceUserId"] == actor.UserId) ProposeAuthority(actor, callerSnapshot);
            else if (action == "approve") ApproveAuthority(actor, callerSnapshot, callerHead);
            else if (action == "revoke") ScopedAuthority(actor, callerSnapshot, Permissions.AccessRevoke, R.Request(callerHead["request"]).Scope);
            if (deadline is { } end && clock.UtcNow >= end) throw new AuthorizationDeniedException("delegation_expired");
            if (people is not null && (clock.UtcNow < people.ValidFrom || people.ValidUntil is { } until && clock.UtcNow >= until)) throw R.Invalid();
        });
    }
    private async Task BumpAsync(long userId, IClientSessionHandle tx, CancellationToken ct) {
        if ((await Rows("identities").UpdateOneAsync(tx, F.Eq("_id", userId), Builders<BsonDocument>.Update.Inc("grantVersion", 1L).Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw R.Invalid();
        await TemporaryDelegationAuthorityReader.InvalidateDependentsAsync(database, tx, [userId], ct);
    }
    private async Task<BsonDocument?> OriginalAsync(ActorContext actor, string key, IClientSessionHandle tx, CancellationToken ct) {
        var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", R.Operation) & F.Eq("key", key)).SingleOrDefaultAsync(ct);
        var versions = await Rows(R.Events).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", key)).Limit(2).ToListAsync(ct);
        if (receipt is null) {
            if (versions.Count != 0 || await Rows("auditEvents").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", R.Operation) & F.Eq("requestKey", key)).AnyAsync(ct)) throw R.Invalid();
            return null;
        }
        if (versions.Count != 1) throw R.Invalid(); var row = await R.EventAsync(database, tx, versions[0]["_id"].AsInt64, ct);
        if (receipt["result"] != R.Result(row) || receipt["bodyHash"] != row["bodyHash"]) throw R.Invalid(); return row;
    }
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor, string key, CancellationToken ct = default) {
        Metadata(new(key, "Recover original delegation operation", "delegation-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var clauses = ParticipantClauses(actor, snapshot);
            await authorization.FenceAsync(actor, clauses.SelectMany(c => c.RequiredNodes).Distinct(), tx, token);
            var row = await OriginalAsync(actor, key, tx, token);
            BsonDocument? head = null;
            if (row is not null) {
                head = await R.HeadAsync(database, tx, row["delegationId"].AsInt64, token);
                await RequireReadAsync(actor, snapshot, head, tx, token); await dependencies.TouchAsync(head, tx, token);
            }
            await authorization.FenceAsync(actor, clauses.SelectMany(c => c.RequiredNodes).Distinct(), tx, token);
            _ = ParticipantClauses(actor, snapshot); if (head is not null) _ = RequireRead(actor, snapshot, head);
            return row is null ? null : R.Result(row);
        }, ct);
    }

    public async Task<ImmutableArray<TemporaryDelegationSource>> SourcesAsync(ActorContext actor, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            ProposeAuthority(actor, snapshot); var result = ImmutableArray.CreateBuilder<TemporaryDelegationSource>();
            foreach (var grant in snapshot.Grants.Where(g => g.TemplateId != TemporaryDelegationPolicy.TemplateId && ScopePolicy.IsGrantActive(g, actor.UserId, clock.UtcNow) &&
                g.Scope.Kind is not (GrantScopeKind.BuyerOwned or GrantScopeKind.CourierAssignment) && !g.DelegablePermissions.IsDefaultOrEmpty)) {
                var membership = snapshot.Memberships.SingleOrDefault(m => m.Id == grant.MembershipId);
                if (membership is null || !ScopePolicy.IsEffective(membership.Status, membership.EffectiveAt, membership.ExpiresAt, membership.RevokedAt, clock.UtcNow) ||
                    !ScopePolicy.IsScopeActive(grant.Scope, snapshot.Directory) || !ScopePolicy.IsScopeActive(membership.Scope, snapshot.Directory)) continue;
                var permissions = grant.Permissions.Intersect(grant.DelegablePermissions).Order(StringComparer.Ordinal).ToImmutableArray(); if (permissions.IsEmpty) continue;
                var expiry = new[] { grant.ExpiresAt, membership.ExpiresAt }.Where(d => d is not null).Min();
                result.Add(new(grant.Id, grant.Version, grant.Scope, permissions, grant.EffectiveAt > membership.EffectiveAt ? grant.EffectiveAt : membership.EffectiveAt, expiry));
            }
            await authorization.FenceAsync(actor, [], tx, token); ProposeAuthority(actor, snapshot); return result.ToImmutable();
        }, ct);
    }
    public async Task<TemporaryDelegationPage> ListAsync(ActorContext actor, long? beforeId = null, CancellationToken ct = default) {
        if (beforeId is <= 0) throw R.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var current = Independent(snapshot); var participant = ParticipantClauses(actor, snapshot);
            var allowed = participant.Select(c => F.Eq("request.beneficiaryUserId", actor.UserId) & F.Eq("request.beneficiaryMembershipId", c.MembershipId) &
                AccessAdministrationService.ScopeContainmentFilter(c.Scope, "request.scope")).ToList();
            if (ScopePolicy.Compile(actor, current, Permissions.AccessPropose, clock.UtcNow).Matches(Administrative)) allowed.Add(F.Eq("sourceUserId", actor.UserId));
            foreach (var permission in new[] { Permissions.AccessApprove, Permissions.AccessRevoke })
                allowed.AddRange(ScopePolicy.Compile(actor, current, permission, clock.UtcNow).Clauses.Select(c => AccessAdministrationService.ScopeContainmentFilter(c.Scope, "request.scope")));
            var all = ScopePolicy.Compile(actor, current, Permissions.AccessAuditRead, clock.UtcNow).Matches(Administrative);
            var filter = all ? F.Empty : F.Or(allowed); if (beforeId is { } cursor) filter &= F.Lt("_id", cursor);
            await authorization.FenceAsync(actor, participant.SelectMany(c => c.RequiredNodes).Distinct(), tx, token);
            var rows = await Rows(R.Heads).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var result = ImmutableArray.CreateBuilder<TemporaryDelegationSummary>(); var captured = new List<BsonDocument>();
            foreach (var row in rows.Take(25)) {
                var head = await R.HeadAsync(database, tx, row["_id"].AsInt64, token);
                await RequireReadAsync(actor, snapshot, head, tx, token); captured.Add(head);
                result.Add(await SummaryAsync(actor, snapshot, head, tx, token)); await dependencies.TouchAsync(head, tx, token);
            }
            await authorization.FenceAsync(actor, participant.SelectMany(c => c.RequiredNodes).Distinct(), tx, token);
            _ = ParticipantClauses(actor, snapshot); foreach (var head in captured) _ = RequireRead(actor, snapshot, head);
            return new TemporaryDelegationPage(result.ToImmutable(), rows.Count > 25 ? rows[24]["_id"].AsInt64 : null);
        }, ct);
    }
    public async Task<(TemporaryDelegationSummary Item, ImmutableArray<TemporaryDelegationEvent> History)> ReadAsync(ActorContext actor, long id, CancellationToken ct = default) {
        if (id <= 0) throw R.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var head = await R.HeadAsync(database, tx, id, token); await RequireReadAsync(actor, snapshot, head, tx, token);
            var rows = await Rows(R.Events).Find(tx, F.Eq("delegationId", id)).Sort(new BsonDocument("revision", 1)).ToListAsync(token);
            var result = (await SummaryAsync(actor, snapshot, head, tx, token), rows.Select(row => new TemporaryDelegationEvent(row["_id"].AsInt64,
                row["revision"].AsInt64, row["actorId"].AsInt64, row["action"].AsString, row["reason"].AsString, R.Time(row["recordedAt"]))).ToImmutableArray());
            await dependencies.TouchAsync(head, tx, token); await RequireReadAsync(actor, snapshot, head, tx, token); return result;
        }, ct);
    }
    private async Task<TemporaryDelegationSummary> SummaryAsync(ActorContext actor, AuthorizationSnapshot snapshot, BsonDocument head, IClientSessionHandle tx, CancellationToken ct) {
        _ = RequireRead(actor, snapshot, head); var request = R.Request(head["request"]);
        var current = await dependencies.SourceAsync(head, snapshot.Directory, tx, ct); var actions = ImmutableArray.CreateBuilder<string>();
        var independent = Independent(snapshot);
        if (head["status"] == "pending" && current) {
            var candidate = new PermissionGrant(head["_id"].AsInt64, request.BeneficiaryUserId, request.BeneficiaryMembershipId, TemporaryDelegationPolicy.TemplateId,
                1, request.Permissions, request.Scope, AuthorizationStatus.Pending, head["sourceUserId"].AsInt64, 0, head["proposalReason"].AsString,
                request.EffectiveAt, request.ExpiresAt, DelegablePermissions: []);
            if (AccessGrantPolicy.CanApprove(actor, independent, candidate, clock.UtcNow).Allowed) actions.Add("approve");
        }
        if (head["status"] != "revoked" && (head["sourceUserId"] == actor.UserId && ScopePolicy.Compile(actor, independent, Permissions.AccessPropose, clock.UtcNow).Matches(Administrative) ||
            ScopePolicy.Compile(actor, independent, Permissions.AccessRevoke, clock.UtcNow).Clauses.Any(c => ScopePolicy.ContainsScope(c.Scope, request.Scope)))) actions.Add("revoke");
        if (head["sourceUserId"] == actor.UserId || request.BeneficiaryUserId == actor.UserId) actions.Add("handover");
        return new(head["_id"].AsInt64, head["revision"].AsInt64, head["sourceUserId"].AsInt64, head["approvedBy"].AsInt64, request,
            head["status"].AsString, current ? "current" : "unavailable", head["proposalReason"].AsString, R.Time(head["updatedAt"]), actions.ToImmutable(), request.Permissions.Contains(Permissions.PaymentReviewManage) ? "payment_review_only" : "unavailable");
    }
}
