using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    public Task<BsonDocument> ProposeAliasAsync(ActorContext actor, long poolId, long expectedPoolRevision,
        StockLocation requestedLocation, long expectedAliasRevision, bool enabled, AuthorizationCommandMetadata metadata, CancellationToken ct = default) =>
        ProposeAliasCoreAsync(actor, poolId, expectedPoolRevision, requestedLocation, expectedAliasRevision, enabled, metadata, false, ct);

    private async Task<BsonDocument> ProposeAliasCoreAsync(ActorContext actor, long poolId, long expectedPoolRevision,
        StockLocation requestedLocation, long expectedAliasRevision, bool enabled, AuthorizationCommandMetadata metadata, bool recoveryOnly, CancellationToken ct)
    {
        requestedLocation.Validate(); Metadata(metadata);
        if (poolId <= 0 || expectedPoolRevision <= 0 || expectedAliasRevision < 0 || expectedAliasRevision == long.MaxValue)
            throw StockSourceAliasBson.Invalid();
        await AliasStorageAsync(ct);
        var id = (await ids.NextAsync("stockSourceAliasProposals", ct)).Value;
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        return await AliasCommandAsync(actor, "stock.source.alias.propose", Permissions.StockSourcePropose, metadata,
            new { poolId, expectedPoolRevision, requestedLocation, expectedAliasRevision, enabled },
            async (snapshot, tx, token) => (await AliasScopesAsync(actor, snapshot, tx, poolId, requestedLocation, Permissions.StockSourcePropose, token), null),
            async (tx, token) =>
            {
                var current = await CurrentSourceAsync(tx, poolId, token);
                var pool = current["pool"].AsBsonDocument;
                if (pool["revision"].AsInt64 != expectedPoolRevision) throw RevisionChanged();
                var input = StockSourceSetupBson.ReadInput(current["input"].AsBsonDocument);
                StockSourceAliasPolicy.Validate(input.Location, requestedLocation);
                var previous = await LatestAliasAsync(tx, poolId, requestedLocation, token);
                RequireAliasPool(previous, current); RequireAliasChange(previous, expectedAliasRevision, expectedPoolRevision, enabled);
                var now = Now();
                if (now < StockSourceSetupBson.Time(current["approvedAt"]) ||
                    previous is not null && now < StockSourceSetupBson.Time(previous["decision"]["decidedAt"])) throw StockSourceAliasBson.Invalid();
                var row = StockSourceAliasBson.Header("stockSourceAliasProposals", id);
                row.Add("poolId", poolId); row.Add("poolRevision", expectedPoolRevision);
                row.Add("ownerLocation", StockSourceSetupBson.Location(input.Location));
                row.Add("requestedLocation", StockSourceSetupBson.Location(requestedLocation)); row.Add("physicalKey", input.PhysicalKey);
                row.Add("baseAliasRevision", expectedAliasRevision); row.Add("enabled", enabled); row.Add("revision", 1L);
                row.Add("proposedBy", actor.UserId); row.Add("proposedAt", now.UtcDateTime);
                StockSourceSetupBson.Metadata(row, auditId, metadata); StockSourceAliasBson.Proposal(row);
                await FenceRevisionPoolAsync(tx, pool, false, token);
                await Rows("stockSourceAliasProposals").InsertOneAsync(tx, row, cancellationToken: token);
                await Rows("auditEvents").InsertOneAsync(tx, StockSourceAliasBson.Audit(row, actor, "stock.source.alias.propose", now), cancellationToken: token);
                return new BsonDocument { { "proposalId", id }, { "revision", 1L }, { "poolId", poolId },
                    { "poolRevision", expectedPoolRevision }, { "baseAliasRevision", expectedAliasRevision } };
            }, ct, recoveryOnly);
    }

    public Task<BsonDocument> DecideAliasAsync(ActorContext actor, long proposalId, long expectedRevision,
        StockSourceSetupDecision decision, string reviewReference, AuthorizationCommandMetadata metadata, CancellationToken ct = default) =>
        DecideAliasCoreAsync(actor, proposalId, expectedRevision, decision, reviewReference, metadata, false, ct);

    private async Task<BsonDocument> DecideAliasCoreAsync(ActorContext actor, long proposalId, long expectedRevision,
        StockSourceSetupDecision decision, string reviewReference, AuthorizationCommandMetadata metadata, bool recoveryOnly, CancellationToken ct)
    {
        if (proposalId <= 0 || expectedRevision <= 0 || !Enum.IsDefined(decision) || !StockSourceSetupPolicy.Text(reviewReference, 160))
            throw StockSourceAliasBson.Invalid();
        Metadata(metadata); await AliasStorageAsync(ct);
        var id = (await ids.NextAsync("stockSourceAliasDecisions", ct)).Value;
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        long? aliasId = decision == StockSourceSetupDecision.Approve ? (await ids.NextAsync("stockSourceAliases", ct)).Value : null;
        var permission = decision == StockSourceSetupDecision.Withdraw ? Permissions.StockSourcePropose : Permissions.StockSourceApprove;
        var operation = "stock.source.alias." + decision.ToString().ToLowerInvariant();
        return await AliasCommandAsync(actor, operation, permission, metadata, new { proposalId, expectedRevision, decision, reviewReference },
            async (snapshot, tx, token) =>
            {
                var scoped = await Rows("stockSourceAliasProposals").Find(tx, F.Eq("_id", proposalId))
                    .Project(new BsonDocument { { "ownerLocation", 1 }, { "requestedLocation", 1 } }).SingleOrDefaultAsync(token);
                if (scoped is null) throw new AuthorizationDeniedException("scope_denied");
                var resources = AliasResources(scoped);
                foreach (var resource in resources) Require(actor, snapshot, permission, resource);
                var proposal = await AliasProposalAsync(tx, proposalId, token);
                if (decision == StockSourceSetupDecision.Withdraw && proposal["proposedBy"].AsInt64 != actor.UserId)
                    throw new AuthorizationDeniedException("stock_source_proposer_required");
                return (resources, decision == StockSourceSetupDecision.Withdraw ? null :
                    new ApprovalRequest(1, expectedRevision, ProposedBy: proposal["proposedBy"].AsInt64));
            }, async (tx, token) =>
            {
                var proposal = await AliasProposalAsync(tx, proposalId, token);
                if (proposal["revision"].AsInt64 != expectedRevision) throw AliasChanged();
                if (await Rows("stockSourceAliasDecisions").Find(tx, F.Eq("proposalId", proposalId)).AnyAsync(token)) throw AliasChanged();
                var now = Now();
                if (now < StockSourceSetupBson.Time(proposal["proposedAt"])) throw StockSourceAliasBson.Invalid();
                var row = StockSourceAliasBson.Header("stockSourceAliasDecisions", id);
                row.Add("proposalId", proposalId); row.Add("revision", 1L); row.Add("poolId", proposal["poolId"]);
                row.Add("poolRevision", proposal["poolRevision"]); row.Add("requestedLocation", proposal["requestedLocation"].DeepClone());
                row.Add("aliasRevision", aliasId.HasValue ? new BsonInt64(checked(proposal["baseAliasRevision"].AsInt64 + 1)) : BsonNull.Value);
                row.Add("decision", decision.ToString().ToLowerInvariant()); row.Add("decidedBy", actor.UserId);
                row.Add("decidedAt", now.UtcDateTime); row.Add("reviewReference", reviewReference);
                StockSourceSetupBson.Metadata(row, auditId, metadata); StockSourceAliasBson.Decision(row, proposal);
                if (aliasId.HasValue)
                {
                    var current = await CurrentSourceAsync(tx, proposal["poolId"].AsInt64, token);
                    var pool = current["pool"].AsBsonDocument;
                    var input = StockSourceSetupBson.ReadInput(current["input"].AsBsonDocument);
                    if (pool["revision"] != proposal["poolRevision"] || !pool["location"].Equals(proposal["ownerLocation"]) ||
                        input.PhysicalKey != proposal["physicalKey"].AsString) throw RevisionChanged();
                    if (pool["active"].AsBoolean)
                        throw new PersistenceConflictException("stock_source_handoff_required", "Active source mappings require suspension and handoff before approval");
                    var requested = InventoryGraph.Location(proposal["requestedLocation"].AsBsonDocument);
                    var previous = await LatestAliasAsync(tx, proposal["poolId"].AsInt64, requested, token);
                    RequireAliasPool(previous, current); RequireAliasChange(previous, proposal["baseAliasRevision"].AsInt64, proposal["poolRevision"].AsInt64, proposal["enabled"].AsBoolean);
                    if (previous is not null && StockSourceSetupBson.Time(proposal["proposedAt"]) <
                        StockSourceSetupBson.Time(previous["decision"]["decidedAt"])) throw StockSourceAliasBson.Invalid();
                    var alias = StockSourceAliasBson.Header("stockSourceAliases", aliasId.Value);
                    foreach (var field in StockSourceAliasBson.BindingFields) alias.Add(field, proposal[field].DeepClone());
                    alias.Add("aliasRevision", row["aliasRevision"]); alias.Add("proposalId", proposalId); alias.Add("decisionId", id);
                    StockSourceAliasBson.Alias(alias, proposal, row);
                    await FenceRevisionPoolAsync(tx, pool, false, token);
                    await Rows("stockSourceAliases").InsertOneAsync(tx, alias, cancellationToken: token);
                }
                await Rows("stockSourceAliasDecisions").InsertOneAsync(tx, row, cancellationToken: token);
                await Rows("auditEvents").InsertOneAsync(tx, StockSourceAliasBson.Audit(row, actor, operation, now), cancellationToken: token);
                return new BsonDocument { { "proposalId", proposalId }, { "decisionId", id }, { "poolId", proposal["poolId"] },
                    { "poolRevision", proposal["poolRevision"] }, { "aliasRevision", row["aliasRevision"] }, { "status", row["decision"] } };
            }, ct, recoveryOnly);
    }

    /// <summary>Configuration only. bindingCurrent is never source acceptance, availability or permission to write stock.</summary>
    public async Task<BsonDocument> ReadAliasAsync(ActorContext actor, long poolId, StockLocation requestedLocation, CancellationToken ct = default)
    {
        requestedLocation.Validate(); if (poolId <= 0) throw StockSourceAliasBson.Invalid(); await AliasStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            var resources = await AliasScopesAsync(actor, snapshot, tx, poolId, requestedLocation, Permissions.StockSourceRead, token);
            var current = await CurrentSourceAsync(tx, poolId, token);
            var latest = await LatestAliasAsync(tx, poolId, requestedLocation, token);
            RequireAliasPool(latest, current);

            var bindingCurrent = latest is not null && latest["alias"]["enabled"].AsBoolean &&
                latest["alias"]["poolRevision"] == current["pool"]["revision"];
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceRead, resources, tx, token);
            return new BsonDocument { { "currentSource", current }, { "mapping", (BsonValue?)latest ?? BsonNull.Value }, { "bindingCurrent", bindingCurrent } };
        }, ct);
    }

    public async Task<AuthorizedPage> ListAliasProposalsAsync(ActorContext actor, long poolId, StockLocation requestedLocation,
        int offset, int limit, CancellationToken ct = default)
    {
        requestedLocation.Validate(); if (poolId <= 0) throw StockSourceAliasBson.Invalid();
        AuthorizationStore.ValidatePage(offset, limit); await AliasStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            var resources = await AliasScopesAsync(actor, snapshot, tx, poolId, requestedLocation, Permissions.StockSourceRead, token);
            var current = await CurrentSourceAsync(tx, poolId, token);
            var filter = AliasFilter(poolId, requestedLocation);
            var total = await Rows("stockSourceAliasProposals").CountDocumentsAsync(tx, filter, cancellationToken: token);
            var proposals = await Rows("stockSourceAliasProposals").Find(tx, filter).Sort(FSort).Skip(offset).Limit(limit).ToListAsync(token);
            var items = new List<BsonDocument>();
            foreach (var proposal in proposals)
            {
                var view = await AliasViewAsync(tx, proposal["_id"].AsInt64, token);
                RequireAliasBinding(proposal, current); items.Add(view);
            }
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceRead, resources, tx, token);
            return new AuthorizedPage(total, items);
        }, ct);
    }

    private Task<BsonDocument> AliasCommandAsync(ActorContext actor, string operation, string permission, AuthorizationCommandMetadata metadata,
        object body, Func<AuthorizationSnapshot, IClientSessionHandle, CancellationToken, Task<(ResourceScope[] Resources, ApprovalRequest? Approval)>> target,
        Func<IClientSessionHandle, CancellationToken, Task<BsonDocument>> write, CancellationToken ct, bool recoveryOnly,
        Action? validateCurrent = null)
    {
        ResourceScope[]? captured = null;
        return authorization.ExecuteCommandAsync(actor, operation, metadata, body, async (snapshot, tx, token) =>
        {
            AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, permission, Now()));
            var loaded = await target(snapshot, tx, token); captured = loaded.Resources;
            var grants = captured.SelectMany(resource => Require(actor, snapshot, permission, resource).Clauses
                .Where(clause => clause.Matches(resource)).Select(clause => clause.GrantId)).Distinct().ToImmutableArray();
            return new(new(true, "allowed", grants),
                captured.SelectMany(resource => ScopePolicy.RequiredNodes(resource, snapshot.Directory)).Distinct().ToImmutableArray(),
                permission, new(captured[0], loaded.Approval));
        }, write, ct, async (snapshot, tx, token) =>
            await CompleteAuthorityAsync(actor, snapshot, permission, captured ?? throw StockSourceAliasBson.Invalid(), tx, token),
            requireReceipt: recoveryOnly, validateCurrent: validateCurrent);
    }
    private async Task<ResourceScope[]> AliasScopesAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx,
        long poolId, StockLocation requested, string permission, CancellationToken ct)
    {
        Require(actor, snapshot, permission, Resource(requested));
        var scoped = await Rows("inventoryPools").Find(tx, F.Eq("_id", poolId)).Project(new BsonDocument("location", 1)).SingleOrDefaultAsync(ct);
        if (scoped is null) throw new AuthorizationDeniedException("scope_denied");
        var owner = InventoryGraph.Location(scoped["location"].AsBsonDocument);
        Require(actor, snapshot, permission, Resource(owner));
        StockSourceAliasPolicy.Validate(owner, requested);
        return [Resource(owner), Resource(requested)];
    }
    private static ResourceScope[] AliasResources(BsonDocument proposal) =>
        [Resource(InventoryGraph.Location(proposal["ownerLocation"].AsBsonDocument)), Resource(InventoryGraph.Location(proposal["requestedLocation"].AsBsonDocument))];
    private static FilterDefinition<BsonDocument> AliasFilter(long poolId, StockLocation requested) =>
        F.Eq("poolId", poolId) & F.Eq("requestedLocation", StockSourceSetupBson.Location(requested));
    private static void RequireAliasChange(BsonDocument? previous, long expected, long poolRevision, bool enabled)
    {
        if ((previous?["alias"]["aliasRevision"].AsInt64 ?? 0) != expected) throw AliasChanged();
        if (previous is null ? !enabled :
            previous["alias"]["enabled"].AsBoolean == enabled && previous["alias"]["poolRevision"].AsInt64 == poolRevision)
            throw new PersistenceConflictException("stock_source_alias_unchanged", "The channel mapping must change its reviewed binding or enabled state");
    }
    private async Task<BsonDocument> AliasProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct)
    {
        var row = await Rows("stockSourceAliasProposals").Find(tx, F.Eq("_id", id)).SingleOrDefaultAsync(ct) ?? throw StockSourceAliasBson.Invalid();
        StockSourceAliasBson.Proposal(row);
        await AliasAuditAsync(tx, row, "stock.source.alias.propose", row["proposedBy"], row["proposedAt"], ct);
        return row;
    }
    private async Task<BsonDocument> AliasViewAsync(IClientSessionHandle tx, long id, CancellationToken ct)
    {
        var proposal = await AliasProposalAsync(tx, id, ct);
        var decision = await Rows("stockSourceAliasDecisions").Find(tx, F.Eq("proposalId", id)).SingleOrDefaultAsync(ct);
        BsonDocument? alias = null;
        if (decision is not null)
        {
            StockSourceAliasBson.Decision(decision, proposal);
            await AliasAuditAsync(tx, decision, "stock.source.alias." + decision["decision"].AsString, decision["decidedBy"], decision["decidedAt"], ct);
            if (decision["decision"] == "approve")
            {
                alias = await Rows("stockSourceAliases").Find(tx, F.Eq("decisionId", decision["_id"])).SingleOrDefaultAsync(ct) ?? throw StockSourceAliasBson.Invalid();
                StockSourceAliasBson.Alias(alias, proposal, decision);
            }
        }
        return new() { { "proposal", proposal }, { "decision", (BsonValue?)decision ?? BsonNull.Value }, { "alias", (BsonValue?)alias ?? BsonNull.Value } };
    }
    private async Task<BsonDocument?> LatestAliasAsync(IClientSessionHandle tx, long poolId, StockLocation requested, CancellationToken ct)
    {
        var filter = AliasFilter(poolId, requested);
        var latest = await Rows("stockSourceAliases").Find(tx, filter).Sort(new BsonDocument("aliasRevision", -1)).FirstOrDefaultAsync(ct);
        var approved = await Rows("stockSourceAliasDecisions").CountDocumentsAsync(tx, filter & F.Eq("decision", "approve"), cancellationToken: ct);
        if (latest is null)
        {
            if (approved != 0) throw StockSourceAliasBson.Invalid();
            return null;
        }
        if (!latest.GetValue("proposalId", BsonNull.Value).IsInt64 || !latest.GetValue("aliasRevision", BsonNull.Value).IsInt64) throw StockSourceAliasBson.Invalid();
        var view = await AliasViewAsync(tx, latest["proposalId"].AsInt64, ct);
        if (!view["alias"].Equals(latest) || approved != latest["aliasRevision"].AsInt64 || await Rows("stockSourceAliases").CountDocumentsAsync(tx, filter, cancellationToken: ct) != latest["aliasRevision"].AsInt64)
            throw StockSourceAliasBson.Invalid();
        if (latest["aliasRevision"].AsInt64 > 1)
        {
            var preceding = await Rows("stockSourceAliases").Find(tx, filter & F.Eq("aliasRevision", latest["aliasRevision"].AsInt64 - 1))
                .SingleOrDefaultAsync(ct) ?? throw StockSourceAliasBson.Invalid();
            var before = await AliasViewAsync(tx, preceding["proposalId"].AsInt64, ct);
            if (!before["alias"].Equals(preceding) || preceding["poolRevision"].AsInt64 > latest["poolRevision"].AsInt64 ||
                StockSourceSetupBson.Time(before["decision"]["decidedAt"]) > StockSourceSetupBson.Time(view["proposal"]["proposedAt"]) ||
                !preceding["ownerLocation"].Equals(latest["ownerLocation"]) || preceding["physicalKey"] != latest["physicalKey"] ||
                preceding["enabled"] == latest["enabled"] && preceding["poolRevision"] == latest["poolRevision"]) throw StockSourceAliasBson.Invalid();
        }
        return view;
    }
    private async Task AliasAuditAsync(IClientSessionHandle tx, BsonDocument row, string operation, BsonValue actor, BsonValue at, CancellationToken ct)
    {
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        if (audit is null || audit.ElementCount != 8 || audit.GetValue("kind", "") != "stock.source.alias.audit" ||
            !audit.GetValue("_id", BsonNull.Value).IsInt64 || !audit.GetValue("schemaVersion", BsonNull.Value).IsInt32 ||
            audit["schemaVersion"].AsInt32 != 1 || !audit.GetValue("actorGrantVersion", BsonNull.Value).IsInt64 ||
            audit["actorGrantVersion"].AsInt64 <= 0 || audit.GetValue("actorId", BsonNull.Value) != actor ||
            audit.GetValue("operation", "") != operation || audit.GetValue("createdAt", BsonNull.Value) != at ||
            !audit.GetValue("record", BsonNull.Value).Equals(row)) throw StockSourceAliasBson.Invalid();
    }
    private static void RequireAliasPool(BsonDocument? view, BsonDocument current)
    {
        if (view is not null) RequireAliasBinding(view["alias"].AsBsonDocument, current);
    }
    private static void RequireAliasBinding(BsonDocument row, BsonDocument current)
    {
        var input = StockSourceSetupBson.ReadInput(current["input"].AsBsonDocument);
        if (!row["ownerLocation"].Equals(current["pool"]["location"]) || row["physicalKey"].AsString != input.PhysicalKey ||
            row["poolRevision"].AsInt64 > current["pool"]["revision"].AsInt64) throw StockSourceAliasBson.Invalid();
    }
    private async Task AliasStorageAsync(CancellationToken ct)
    {
        await RevisionStorageAsync(ct);
        foreach (var name in StockSourceAliasSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, StockSourceAliasSchemas.Validator(name), StockSourceAliasSchemas.Key(name), ct);
    }
    private static PersistenceConflictException AliasChanged() => new("stock_source_alias_changed", "The channel mapping or proposal changed; refresh and review again");
}
