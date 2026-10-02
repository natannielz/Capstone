using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Organizations.OrganizationAddressBson;

namespace UnitToko.Platform.Organizations;

public sealed partial class OrganizationAddressService
{
    private AuthorizationQuery DemandRead(ActorContext actor, AuthorizationSnapshot authority) {
        foreach (var permission in new[] { Permissions.TechnicalConfigure, Permissions.OrganizationApprove }) {
            var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow);
            if (query.Matches(new(IsAdministrative: true))) return query;
        }
        var read = ScopePolicy.Compile(actor, authority, Permissions.OrganizationAddressRead, clock.UtcNow); AuthorizationStore.Require(read); return read;
    }
    private async Task FenceReadQueryAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        AuthorizationQuery query, ResourceScope? resource, CancellationToken ct) {
        var clauses = query.Clauses.Where(clause => resource is null || clause.Matches(resource)).ToArray();
        if (clauses.Length == 0) throw new AuthorizationDeniedException("scope_denied");
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        await LiveSessionAsync(actor, tx, ct);
        var current = ScopePolicy.Compile(actor, authority, query.Permission, clock.UtcNow); AuthorizationStore.Require(current);
        if (resource is not null && !current.Matches(resource)) throw new AuthorizationDeniedException("scope_denied");
        ct.ThrowIfCancellationRequested();
    }
    private async Task ReadAuditAsync(Context context, string action, BsonDocument selection, int count, CancellationToken ct) {
        var query = Require(context); var scopes = query.Clauses.Where(clause => clause.Matches(context.Resource)).Select(clause => clause.Scope).Distinct().ToImmutableArray();
        var auditSelection = new BsonDocument {
            { "subjectId", selection.GetValue("proposalId", context.Source["target"]["id"]) },
            { "operation", selection.GetValue("operation", B.Prefix + action + ":" + B.Purpose(context.AddressPurpose)) } };
        if (selection.TryGetValue("key", out var key))
            auditSelection.Add("keyHash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.AsString))).ToLowerInvariant());
        var id = (await ids.NextAsync("authorizationReadEvents", ct)).Value;
        await authorization.RecordPrivilegedReadAsync(id, context.Actor, context.Authority, "organization.workbench.read",
            new(action == "operation.query" ? "operation-recovery" : context.Resource.IsAdministrative ? "access-review" : "account-support",
                "postal-address-" + Guid.NewGuid().ToString("N")), auditSelection, count, scopes, context.Tx, ct);
    }
    private async Task<BsonDocument> CurrentViewAsync(Context context, CancellationToken ct) {
        var target = OrganizationProfileRecords.Node(context.Source["target"]); var head = context.Head;
        BsonDocument? version = head is null || head["versionId"].IsBsonNull ? null : await records.RowAsync(context.Tx, B.Versions, head["versionId"].AsInt64, ct);
        var profile = version?["source"]["profile"] ?? context.Source["profile"];
        return new() { { "addressId", version is null ? BsonNull.Value : head!["_id"] }, { "target", B.Target(new OrganizationAddressTarget(target.Kind, target.Id)) }, { "purpose", B.Purpose(context.AddressPurpose) },
            { "version", version is null ? 0L : version["version"] }, { "versionId", version is null ? BsonNull.Value : version["_id"] },
            { "code", profile["code"] }, { "name", profile["name"] }, { "address", version is null ? BsonNull.Value : version["details"] },
            { "approvedAt", version is null ? BsonNull.Value : version["effectiveAt"] }, { "current", version is not null && version["source"].Equals(context.Source) } };
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, OrganizationAddressTarget target, OrganizationAddressPurpose purpose, CancellationToken ct = default) {
        OrganizationAddressPolicy.RequireTarget(target, purpose); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, target, purpose, null, token); await ApprovedPeopleAsync(context, token);
            var view = await CurrentViewAsync(context, token); await ReadAuditAsync(context, "query", new() { { "target", B.Target(target) }, { "purpose", B.Purpose(purpose) } }, 1, token);
            await FenceAsync(context, token); return view;
        }, ct);
    }
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, OrganizationAddressPurpose purpose, CancellationToken ct = default) {
        if (!Enum.IsDefined(purpose)) throw OrganizationAddressPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var query = DemandRead(actor, authority); var items = new BsonArray();
            foreach (var node in authority.Directory.Nodes.Where(node => node.Status == AuthorizationStatus.Active).OrderBy(node => node.Id)) {
                var target = new OrganizationAddressTarget(node.Kind, node.Id);
                try { OrganizationAddressPolicy.RequireTarget(target, purpose); } catch (UnitToko.Modules.Contracts.DomainValidationException) { continue; }
                try { _ = ReadResource(actor, authority, target, DirectoryTarget(authority, target)); }
                catch (AuthorizationDeniedException error) when (error.Code is "scope_denied" or "resource_unavailable") { continue; }
                if (!await Rows("organizationProfiles").Find(tx, new BsonDocument("_id", target.Id)).AnyAsync(token)) continue;
                Context context;
                try { context = await ContextAsync(actor, authority, tx, target, purpose, null, token); }
                catch (AuthorizationDeniedException error) when (error.Code == "scope_denied") { continue; }
                await ApprovedPeopleAsync(context, token); var view = await CurrentViewAsync(context, token);
                items.Add(new BsonDocument { { "target", B.Target(target) }, { "purpose", B.Purpose(purpose) }, { "code", context.Source["profile"]["code"] }, { "name", context.Source["profile"]["name"] },
                    { "scopeVersion", context.Source["target"]["version"] }, { "profileVersion", context.Source["profile"]["version"] }, { "addressVersion", view["version"] },
                    { "addressId", view["addressId"] }, { "versionId", view["versionId"] }, { "currentAddress", view["current"].AsBoolean ? view["address"] : BsonNull.Value } });
                if (items.Count > 100) throw new AuthorizationDeniedException("organization_address_target_limit");
                await ReadAuditAsync(context, "targets.query", new() { { "target", B.Target(target) }, { "purpose", B.Purpose(purpose) } }, 1, token);
                await FenceAsync(context, token);
            }
            if (items.Count == 0) await FenceReadQueryAsync(actor, authority, tx, query, null, token);
            return new BsonDocument { { "items", items }, { "hasMore", false } };
        }, ct);
    }
    private async Task<BsonDocument> ProposalViewAsync(Context context, BsonDocument proposal, CancellationToken ct) {
        var decision = await records.DecisionForAsync(context.Tx, proposal, ct);
        return new() { { "proposalId", proposal["_id"] }, { "addressId", proposal["addressId"] }, { "revision", 1L }, { "target", proposal["target"] }, { "purpose", proposal["purpose"] },
            { "code", proposal["source"]["profile"]["code"] }, { "name", proposal["source"]["profile"]["name"] }, { "address", proposal["details"] }, { "baseVersion", proposal["baseVersion"] },
            { "proposedAt", proposal["recordedAt"] }, { "state", decision is null ? "pending" : decision["decision"] == "approve" ? "approved" : "rejected" },
            { "decision", decision is null ? BsonNull.Value : decision["decision"] }, { "addressVersion", decision is null ? BsonNull.Value : decision["addressVersion"] },
            { "versionId", decision is null ? BsonNull.Value : decision["versionId"] }, { "bindingCurrent", proposal["source"].Equals(context.Source) && proposal["baseVersion"] == context.Head!["version"] } };
    }
    public async Task<BsonDocument> ReadProposalAsync(ActorContext actor, long proposalId, CancellationToken ct = default) {
        if (proposalId <= 0) throw OrganizationAddressPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            DemandRead(actor, authority);
            // Route by target metadata only. Foreign and missing identifiers are denied before any
            // private address, profile approval or proposal-effect integrity joins are attempted.
            var raw = await Rows(B.Proposals).Find(tx, new BsonDocument("_id", proposalId)).Project(new BsonDocument { { "target", 1 }, { "purpose", 1 } }).SingleOrDefaultAsync(token);
            if (raw is null) throw new AuthorizationDeniedException("resource_unavailable");
            OrganizationAddressTarget target; OrganizationAddressPurpose purpose;
            try { target = B.Target(raw["target"]); purpose = B.Purpose(raw["purpose"]); OrganizationAddressPolicy.RequireTarget(target, purpose); }
            catch (Exception error) when (error is ArgumentException or InvalidCastException or KeyNotFoundException or UnitToko.Modules.Contracts.DomainValidationException or UnitToko.Platform.Mongo.PersistenceConflictException)
                { throw new AuthorizationDeniedException("resource_unavailable"); }
            try { _ = ReadResource(actor, authority, target, DirectoryTarget(authority, target)); }
            catch (AuthorizationDeniedException) { throw new AuthorizationDeniedException("resource_unavailable"); }
            var context = await ContextAsync(actor, authority, tx, target, purpose, null, token);
            var proposal = await records.ProposalAsync(tx, proposalId, token); await ApprovedPeopleAsync(context, token);
            var view = await ProposalViewAsync(context, proposal, token); await ReadAuditAsync(context, "proposal.query", new("proposalId", proposalId), 1, token);
            await FenceAsync(context, token); return view;
        }, ct);
    }
    public async Task<BsonDocument> ListProposalsAsync(ActorContext actor, OrganizationAddressTarget target, OrganizationAddressPurpose purpose, long? beforeId = null, CancellationToken ct = default) {
        OrganizationAddressPolicy.RequireTarget(target, purpose); if (beforeId is <= 0) throw OrganizationAddressPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, target, purpose, null, token); var filter = new BsonDocument { { "target", B.Target(target) }, { "purpose", B.Purpose(purpose) } };
            if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(B.Proposals).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray();
            foreach (var row in rows.Take(25)) items.Add(await ProposalViewAsync(context, await records.ProposalAsync(tx, row["_id"].AsInt64, token), token));
            await ApprovedPeopleAsync(context, token); await ReadAuditAsync(context, "proposals.query", new() { { "target", B.Target(target) }, { "purpose", B.Purpose(purpose) } }, items.Count, token);
            await FenceAsync(context, token); return new BsonDocument { { "items", items }, { "nextProposalId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        var permission = B.Permission(operation); Metadata(new(key, "Recover original postal address operation", "postal-address-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query);
            if (!query.Matches(new(IsAdministrative: true))) throw new AuthorizationDeniedException("scope_denied");
            var row = await records.ForKeyAsync(tx, actor.UserId, operation, key, token);
            if (row is null) { await FenceReadQueryAsync(actor, authority, tx, query, new(IsAdministrative: true), token); return null; }
            var proposal = await records.ProposalAsync(tx, row["proposalId"].AsInt64, token);
            var context = await ContextAsync(actor, authority, tx, B.Target(row["target"]), B.Purpose(row["purpose"]), permission, token);
            BindPeople(context, proposal, operation == B.Prefix + "propose" ? null : row);
            await PeopleAsync(context, operation == B.Prefix + "propose" ? null : actor.UserId, operation == B.Prefix + "propose" ? null : proposal["actorId"].AsInt64, token);
            await ReadAuditAsync(context, "operation.query", new() { { "operation", operation }, { "key", key } }, 1, token); await FenceAsync(context, token); return B.Result(row);
        }, ct);
    }
}
