using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    public Task<BsonDocument> ProposeRevisionAsync(ActorContext actor, long poolId, long expectedPoolRevision,
        StockSourceSetupInput input, AuthorizationCommandMetadata metadata, CancellationToken ct = default) =>
        ProposeRevisionCoreAsync(actor, poolId, expectedPoolRevision, input, metadata, false, ct);
    public Task<BsonDocument> DecideRevisionAsync(ActorContext actor, long proposalId, long expectedRevision,
        StockSourceSetupDecision decision, string reviewReference, AuthorizationCommandMetadata metadata, CancellationToken ct = default) =>
        DecideRevisionCoreAsync(actor, proposalId, expectedRevision, decision, reviewReference, metadata, false, ct);
    private async Task<BsonDocument> ProposeRevisionCoreAsync(ActorContext actor, long poolId, long expectedPoolRevision,
        StockSourceSetupInput input, AuthorizationCommandMetadata metadata, bool recoveryOnly, CancellationToken ct)
    {
        StockSourceSetupPolicy.Validate(input); Metadata(metadata);
        if (poolId <= 0 || expectedPoolRevision <= 0 || expectedPoolRevision == long.MaxValue) throw StockSourceRevisionBson.Invalid();
        await RevisionStorageAsync(ct);
        var id = (await ids.NextAsync("stockSourceRevisionProposals", ct)).Value;
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        return await CommandAsync(actor, "stock.source.revision.propose", Permissions.StockSourcePropose, metadata,
            new { poolId, expectedPoolRevision, input },
            async (snapshot, tx, token) => new(await RevisionPoolTargetAsync(actor, snapshot, tx, poolId, Permissions.StockSourcePropose, token)),
            async (tx, token) =>
            {
                var current = await CurrentSourceAsync(tx, poolId, token);
                var pool = current["pool"].AsBsonDocument;
                if (pool["revision"].AsInt64 != expectedPoolRevision) throw RevisionChanged();
                StockSourceRevisionPolicy.Validate(StockSourceSetupBson.ReadInput(current["input"].AsBsonDocument), input);
                var now = Now();
                if (now < StockSourceSetupBson.Time(current["approvedAt"])) throw StockSourceRevisionBson.Invalid();
                var row = StockSourceRevisionBson.Header("stockSourceRevisionProposals", id);
                row.Add("poolId", poolId); row.Add("basePoolRevision", expectedPoolRevision);
                row.Add("baseInput", current["input"].DeepClone()); row.Add("input", StockSourceSetupBson.Input(input));
                row.Add("revision", 1L); row.Add("proposedBy", actor.UserId); row.Add("proposedAt", now.UtcDateTime);
                StockSourceSetupBson.Metadata(row, auditId, metadata); StockSourceRevisionBson.Proposal(row);
                await FenceRevisionPoolAsync(tx, pool, false, token);
                await Rows("stockSourceRevisionProposals").InsertOneAsync(tx, row, cancellationToken: token);
                await Rows("auditEvents").InsertOneAsync(tx, StockSourceRevisionBson.Audit(row, actor, "stock.source.revision.propose", now), cancellationToken: token);
                return new BsonDocument { { "proposalId", id }, { "revision", 1L }, { "poolId", poolId }, { "basePoolRevision", expectedPoolRevision } };
            }, ct, recoveryOnly);
    }

    private async Task<BsonDocument> DecideRevisionCoreAsync(ActorContext actor, long proposalId, long expectedRevision,
        StockSourceSetupDecision decision, string reviewReference, AuthorizationCommandMetadata metadata, bool recoveryOnly, CancellationToken ct)
    {
        if (proposalId <= 0 || expectedRevision <= 0 || !Enum.IsDefined(decision) || !StockSourceSetupPolicy.Text(reviewReference, 160))
            throw StockSourceRevisionBson.Invalid();
        Metadata(metadata); await RevisionStorageAsync(ct);
        var id = (await ids.NextAsync("stockSourceRevisionDecisions", ct)).Value;
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        long? revisionId = decision == StockSourceSetupDecision.Approve ? (await ids.NextAsync("stockSourceRevisions", ct)).Value : null;
        var permission = decision == StockSourceSetupDecision.Withdraw ? Permissions.StockSourcePropose : Permissions.StockSourceApprove;
        var name = decision.ToString().ToLowerInvariant(); var operation = "stock.source.revision." + name;
        return await CommandAsync(actor, operation, permission, metadata, new { proposalId, expectedRevision, decision, reviewReference },
            async (snapshot, tx, token) =>
            {
                var scoped = await Rows("stockSourceRevisionProposals").Find(tx, F.Eq("_id", proposalId))
                    .Project(new BsonDocument("input.location", 1)).SingleOrDefaultAsync(token);
                if (scoped is null) throw new AuthorizationDeniedException("scope_denied");
                var resource = Resource(InventoryGraph.Location(scoped["input"]["location"].AsBsonDocument));
                Require(actor, snapshot, permission, resource);
                var proposal = await RevisionProposalAsync(tx, proposalId, token);
                if (decision == StockSourceSetupDecision.Withdraw && proposal["proposedBy"].AsInt64 != actor.UserId)
                    throw new AuthorizationDeniedException("stock_source_proposer_required");
                return new(resource, decision == StockSourceSetupDecision.Withdraw ? null :
                    new(1, expectedRevision, ProposedBy: proposal["proposedBy"].AsInt64));
            }, async (tx, token) =>
            {
                var proposal = await RevisionProposalAsync(tx, proposalId, token);
                if (proposal["revision"].AsInt64 != expectedRevision) throw RevisionChanged();
                if (await Rows("stockSourceRevisionDecisions").Find(tx, F.Eq("proposalId", proposalId)).AnyAsync(token))
                    throw new PersistenceConflictException("stock_source_already_decided", "The source revision already has a decision");
                var now = Now();
                if (now < StockSourceSetupBson.Time(proposal["proposedAt"])) throw StockSourceRevisionBson.Invalid();
                var row = StockSourceRevisionBson.Header("stockSourceRevisionDecisions", id);
                row.Add("proposalId", proposalId); row.Add("revision", 1L); row.Add("poolId", proposal["poolId"]);
                row.Add("basePoolRevision", proposal["basePoolRevision"]);
                row.Add("poolRevision", decision == StockSourceSetupDecision.Approve ? new BsonInt64(checked(proposal["basePoolRevision"].AsInt64 + 1)) : BsonNull.Value);
                row.Add("decision", name); row.Add("decidedBy", actor.UserId); row.Add("decidedAt", now.UtcDateTime);
                row.Add("reviewReference", reviewReference); StockSourceSetupBson.Metadata(row, auditId, metadata);
                StockSourceRevisionBson.Decision(row, proposal);
                if (revisionId.HasValue)
                {
                    var current = await CurrentSourceAsync(tx, proposal["poolId"].AsInt64, token);
                    var pool = current["pool"].AsBsonDocument;
                    if (pool["revision"] != proposal["basePoolRevision"] || !current["input"].Equals(proposal["baseInput"])) throw RevisionChanged();
                    if (pool["active"].AsBoolean)
                        throw new PersistenceConflictException("stock_source_handoff_required", "An active stock source needs a completed suspension and handoff before settings can change");
                    var revision = StockSourceRevisionBson.Header("stockSourceRevisions", revisionId.Value);
                    revision.Add("poolId", proposal["poolId"]); revision.Add("poolRevision", row["poolRevision"]);
                    revision.Add("proposalId", proposalId); revision.Add("decisionId", id); revision.Add("input", proposal["input"].DeepClone());
                    StockSourceRevisionBson.Revision(revision, proposal, row);
                    await FenceRevisionPoolAsync(tx, pool, true, token);
                    await Rows("stockSourceRevisions").InsertOneAsync(tx, revision, cancellationToken: token);
                }
                await Rows("stockSourceRevisionDecisions").InsertOneAsync(tx, row, cancellationToken: token);
                await Rows("auditEvents").InsertOneAsync(tx, StockSourceRevisionBson.Audit(row, actor, operation, now), cancellationToken: token);
                return new BsonDocument { { "proposalId", proposalId }, { "decisionId", id }, { "poolId", proposal["poolId"] },
                    { "poolRevision", row["poolRevision"] }, { "status", name } };
            }, ct, recoveryOnly);
    }

    public async Task<BsonDocument> ReadCurrentSourceAsync(ActorContext actor, StockLocation location, long poolId, CancellationToken ct = default)
    {
        location.Validate(); if (poolId <= 0) throw StockSourceRevisionBson.Invalid(); await RevisionStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            Require(actor, snapshot, Permissions.StockSourceRead, Resource(location));
            var resource = await RevisionPoolTargetAsync(actor, snapshot, tx, poolId, Permissions.StockSourceRead, token);
            if (resource != Resource(location)) throw new AuthorizationDeniedException("scope_denied");
            var result = await CurrentSourceAsync(tx, poolId, token);
            await CompleteAsync(actor, snapshot, Permissions.StockSourceRead, resource, tx, token);
            return result;
        }, ct);
    }
    public async Task<BsonDocument?> ReadRevisionAsync(ActorContext actor, StockLocation location, long proposalId, CancellationToken ct = default)
    {
        location.Validate(); if (proposalId <= 0) throw StockSourceRevisionBson.Invalid(); await RevisionStorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (snapshot, tx, token) =>
        {
            Require(actor, snapshot, Permissions.StockSourceRead, Resource(location));
            var found = await Rows("stockSourceRevisionProposals").Find(tx, F.Eq("_id", proposalId) &
                F.Eq("input.location", StockSourceSetupBson.Location(location))).AnyAsync(token);
            var result = found ? await RevisionViewAsync(tx, proposalId, token) : null;
            await CompleteAsync(actor, snapshot, Permissions.StockSourceRead, Resource(location), tx, token);
            return result;
        }, ct);
    }
    private async Task<BsonDocument> CurrentSourceAsync(IClientSessionHandle tx, long poolId, CancellationToken ct)
    {
        var current = await SourceConfigurationAsync(tx, poolId, ct);
        await RequireActivationPoolProvenanceAsync(tx, current, ct);
        return current;
    }
    private async Task<BsonDocument> SourceConfigurationAsync(IClientSessionHandle tx, long poolId, CancellationToken ct)
    {
        try
        {
            var registration = await Rows("inventoryPoolRegistrations").Find(tx, F.Eq("_id", poolId)).SingleOrDefaultAsync(ct);
            if (registration is null || !registration["proposalId"].IsInt64) throw StockSourceRevisionBson.Invalid();
            var original = await ViewAsync(tx, await ProposalAsync(tx, registration["proposalId"].AsInt64, ct), ct);
            var pool = original["pool"].AsBsonDocument;
            if (pool["_id"].AsInt64 != poolId) throw StockSourceRevisionBson.Invalid();
            var originalInput = original["proposal"]["input"].AsBsonDocument;
            var latest = await Rows("stockSourceRevisions").Find(tx, F.Eq("poolId", poolId))
                .Sort(new BsonDocument("poolRevision", -1)).FirstOrDefaultAsync(ct);
            var input = originalInput;
            var approvedAt = original["decision"]["decidedAt"];
            if (latest is null)
            {
                if (pool["revision"].AsInt64 != 1) throw StockSourceRevisionBson.Invalid();
            }
            else
            {
                if (!latest["poolRevision"].IsInt64 || latest["poolRevision"] != pool["revision"] || !latest["proposalId"].IsInt64)
                    throw StockSourceRevisionBson.Invalid();
                // Compound uniqueness, the maximum revision and count together detect gaps.
                // This is configuration history, not the hot stock-availability read path.
                if (await Rows("stockSourceRevisions").CountDocumentsAsync(tx, F.Eq("poolId", poolId), cancellationToken: ct) != pool["revision"].AsInt64 - 1)
                    throw StockSourceRevisionBson.Invalid();
                var proposal = await RevisionProposalAsync(tx, latest["proposalId"].AsInt64, ct);
                var decision = await RevisionDecisionAsync(tx, proposal, ct) ?? throw StockSourceRevisionBson.Invalid();
                StockSourceRevisionBson.Revision(latest, proposal, decision);
                var predecessorInput = originalInput; var predecessorApprovedAt = approvedAt;
                if (pool["revision"].AsInt64 > 2)
                {
                    var previous = await Rows("stockSourceRevisions").Find(tx, F.Eq("poolId", poolId) &
                        F.Eq("poolRevision", pool["revision"].AsInt64 - 1)).SingleOrDefaultAsync(ct) ?? throw StockSourceRevisionBson.Invalid();
                    var previousProposal = await RevisionProposalAsync(tx, previous["proposalId"].AsInt64, ct);
                    var previousDecision = await RevisionDecisionAsync(tx, previousProposal, ct) ?? throw StockSourceRevisionBson.Invalid();
                    StockSourceRevisionBson.Revision(previous, previousProposal, previousDecision);
                    predecessorInput = previous["input"].AsBsonDocument; predecessorApprovedAt = previousDecision["decidedAt"];
                }
                if (!proposal["baseInput"].Equals(predecessorInput) ||
                    StockSourceSetupBson.Time(proposal["proposedAt"]) < StockSourceSetupBson.Time(predecessorApprovedAt))
                    throw StockSourceRevisionBson.Invalid();
                input = latest["input"].AsBsonDocument; approvedAt = decision["decidedAt"];
                var originalFacts = StockSourceSetupBson.ReadInput(originalInput);
                var facts = StockSourceSetupBson.ReadInput(input);
                if (facts.Location != originalFacts.Location || facts.PhysicalKey != originalFacts.PhysicalKey) throw StockSourceRevisionBson.Invalid();
            }
            var current = new BsonDocument { { "pool", pool.DeepClone() }, { "input", input.DeepClone() },
                { "approvedAt", approvedAt }, { "creationProposalId", registration["proposalId"] }, { "revisionProposalId", latest is null ? BsonNull.Value : latest["proposalId"] } };
            return current;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException or KeyNotFoundException or FormatException or OverflowException)
        { throw StockSourceRevisionBson.Invalid(); }
    }
    private async Task<UnitToko.Modules.Organizations.ResourceScope> RevisionPoolTargetAsync(ActorContext actor, AuthorizationSnapshot snapshot,
        IClientSessionHandle tx, long poolId, string permission, CancellationToken ct)
    {
        var scoped = await Rows("inventoryPools").Find(tx, F.Eq("_id", poolId)).Project(new BsonDocument("location", 1)).SingleOrDefaultAsync(ct);
        if (scoped is null) throw new AuthorizationDeniedException("scope_denied");
        var resource = Resource(InventoryGraph.Location(scoped["location"].AsBsonDocument));
        Require(actor, snapshot, permission, resource);
        return resource;
    }
    private async Task<BsonDocument> RevisionProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct)
    {
        var row = await Rows("stockSourceRevisionProposals").Find(tx, F.Eq("_id", id)).SingleOrDefaultAsync(ct) ?? throw StockSourceRevisionBson.Invalid();
        StockSourceRevisionBson.Proposal(row);
        await RevisionAuditAsync(tx, row, "stock.source.revision.propose", row["proposedBy"].AsInt64, row["proposedAt"], ct);
        return row;
    }
    private async Task<BsonDocument?> RevisionDecisionAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct)
    {
        var row = await Rows("stockSourceRevisionDecisions").Find(tx, F.Eq("proposalId", proposal["_id"])).SingleOrDefaultAsync(ct);
        if (row is not null)
        {
            StockSourceRevisionBson.Decision(row, proposal);
            await RevisionAuditAsync(tx, row, "stock.source.revision." + row["decision"].AsString, row["decidedBy"].AsInt64, row["decidedAt"], ct);
        }
        return row;
    }
    private async Task<BsonDocument> RevisionViewAsync(IClientSessionHandle tx, long proposalId, CancellationToken ct)
    {
        var proposal = await RevisionProposalAsync(tx, proposalId, ct);
        var decision = await RevisionDecisionAsync(tx, proposal, ct);
        BsonDocument? revision = null;
        if (decision?["decision"] == "approve")
        {
            revision = await Rows("stockSourceRevisions").Find(tx, F.Eq("poolId", decision["poolId"]) &
                F.Eq("poolRevision", decision["poolRevision"])).SingleOrDefaultAsync(ct) ?? throw StockSourceRevisionBson.Invalid();
            StockSourceRevisionBson.Revision(revision, proposal, decision);
        }
        return new() { { "proposal", proposal }, { "decision", (BsonValue?)decision ?? BsonNull.Value }, { "appliedRevision", (BsonValue?)revision ?? BsonNull.Value } };
    }
    private async Task RevisionAuditAsync(IClientSessionHandle tx, BsonDocument row, string operation, long actor, BsonValue time, CancellationToken ct)
    {
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        if (audit is null || audit.ElementCount != 8 || audit.GetValue("kind", "") != "stock.source.revision.audit" ||
            !audit["_id"].IsInt64 || !audit["schemaVersion"].IsInt32 || audit["schemaVersion"].AsInt32 != 1 ||
            !audit["actorGrantVersion"].IsInt64 || audit["actorGrantVersion"].AsInt64 <= 0 ||
            audit.GetValue("operation", "") != operation || !audit["actorId"].IsInt64 || audit["actorId"].AsInt64 != actor ||
            audit.GetValue("createdAt", BsonNull.Value) != time || !audit.GetValue("record", BsonNull.Value).Equals(row))
            throw StockSourceRevisionBson.Invalid();
    }
    private async Task FenceRevisionPoolAsync(IClientSessionHandle tx, BsonDocument pool, bool advance, CancellationToken ct)
    {
        await new StockSourceTopologyFence(database).FenceAsync(tx,
            InventoryGraph.Location(pool["location"].AsBsonDocument).OrganizationId.Value, ct);
        if (pool["fence"].AsInt64 == long.MaxValue || advance && pool["revision"].AsInt64 == long.MaxValue) throw StockSourceRevisionBson.Invalid();
        var update = Builders<BsonDocument>.Update.Inc("fence", 1L);
        if (advance) update = update.Inc("revision", 1L);
        var result = await Rows("inventoryPools").UpdateOneAsync(tx, pool, update, cancellationToken: ct);
        if (result.ModifiedCount != 1) throw RevisionChanged();
    }
    private async Task RevisionStorageAsync(CancellationToken ct)
    {
        await StorageAsync(ct);
        foreach (var name in StockSourceRevisionSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, StockSourceRevisionSchemas.Validator(name), StockSourceRevisionSchemas.Key(name), ct);
    }
    private static PersistenceConflictException RevisionChanged() => new("stock_source_revision_changed", "The reviewed source settings changed; refresh before proposing or reviewing again");
}
