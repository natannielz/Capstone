using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Inventory;

internal sealed record InventoryCountSourceFacts(BsonDocument Pool, BsonDocument Configuration,
    StockCountPoolBinding Binding, ImmutableArray<GrantScope> Locations, ResourceScope[] Resources, DateTimeOffset CapturedAt);

/// <summary>Only this factory reads real source/person owners. No caller-supplied
/// configuration, personnel projection or old numeric pool fence can mint a proof.</summary>
internal sealed class InventoryCountPoolCapture
{
    private readonly StockSourceSetupService owner;
    private readonly ActorContext actor;
    private readonly AuthorizationSnapshot authority;
    private readonly IClientSessionHandle tx;
    private readonly long number;
    private readonly InventoryCountSourceFacts facts;
    private readonly PersonAuthorityProof person;
    private readonly DateTimeOffset capturedAt;
    private InventoryCountPoolCapture(StockSourceSetupService owner, ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, InventoryCountSourceFacts facts, PersonAuthorityProof person) {
        this.owner = owner; this.actor = actor; this.authority = authority; this.tx = tx;
        number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; this.facts = facts; this.person = person; capturedAt = facts.CapturedAt;
    }
    internal StockCountPoolBinding Binding => facts.Binding;
    internal BsonDocument Configuration => facts.Configuration.DeepClone().AsBsonDocument;
    internal BsonArray People => new(person.Evidence.Select(value => new BsonDocument { { "userId", value.UserId }, { "personId", value.PersonId },
        { "verification", value.Verification.ToString() }, { "version", value.Version } }));
    internal BsonDocument Pool => facts.Pool.DeepClone().AsBsonDocument;
    internal string SourceHash => InventoryCountStockSourceRecords.SourceHash(StockCountBson.PoolBinding(Binding), Configuration, People);
    internal static async Task<InventoryCountPoolCapture> ReadAsync(StockSourceSetupService owner, ActorContext actor,
        AuthorizationSnapshot authority, IClientSessionHandle tx, GrantScope requested, StockCountPoolSelection selected, CancellationToken ct) {
        var facts = await owner.ReadInventoryCountSourceFactsAsync(actor, authority, tx, selected.PoolId, Permissions.StockCount, ct);
        selected.Validate();
        if (facts.Binding.Selection != selected) throw InventoryCountStockSourceRecords.Changed();
        if (!StockSourceSetupService.CountSourceMatches(facts, requested)) throw new AuthorizationDeniedException("scope_denied");
        var person = await owner.ReadInventoryCountPersonAsync(tx, actor.UserId, ct);
        var result = new InventoryCountPoolCapture(owner, actor, authority, tx, facts, person); result.Require(owner.InventoryCountDatabase, tx); return result;
    }
    internal void Require(IMongoDatabase database, IClientSessionHandle session) {
        new InventoryRepository(database).RequireSession(session);
        if (!ReferenceEquals(session, tx) || !ReferenceEquals(database.Client, owner.InventoryCountDatabase.Client) ||
            database.DatabaseNamespace != owner.InventoryCountDatabase.DatabaseNamespace || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number ||
            owner.InventoryCountNow < capturedAt) throw InventoryCountStockSourceRecords.Unavailable();
        person.RequireCurrent(tx, owner.InventoryCountNow);
        owner.RequireInventoryCountAuthority(actor, authority, facts.Resources, Permissions.StockCount);
    }
    internal async Task FenceAsync(IMongoDatabase database, IClientSessionHandle session, CancellationToken ct) {
        Require(database, session);
        var fresh = await owner.ReadInventoryCountSourceFactsAsync(actor, authority, tx, Binding.PoolId, Permissions.StockCount, ct);
        if (fresh.Binding != Binding || fresh.Configuration != facts.Configuration || !fresh.Locations.SequenceEqual(facts.Locations))
            throw InventoryCountStockSourceRecords.Changed();
        await owner.FenceInventoryCountPersonAsync(tx, person, ct);
        await owner.FenceInventoryCountAuthorityAsync(actor, authority, tx, facts.Resources, Permissions.StockCount, ct); Require(database, session);
    }
}

public sealed partial class StockSourceSetupService
{
    internal IMongoDatabase InventoryCountDatabase => database;
    internal DateTimeOffset InventoryCountNow => Now();
    internal void RequireInventoryCountAuthority(ActorContext actor, AuthorizationSnapshot authority, ResourceScope[] resources, string permission) {
        foreach (var resource in resources) Require(actor, authority, permission, resource);
    }
    internal Task<PersonAuthorityProof> ReadInventoryCountPersonAsync(IClientSessionHandle tx, long actorId, CancellationToken ct) =>
        new PersonRegistryService(database, clock).ResolveProofsAsync(tx, [actorId], ct);
    internal Task FenceInventoryCountPersonAsync(IClientSessionHandle tx, PersonAuthorityProof proof, CancellationToken ct) =>
        new PersonAuthorizationFence(database, clock).TouchAsync(tx, proof, ct);
    internal Task FenceInventoryCountAuthorityAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ResourceScope[] resources, string permission, CancellationToken ct) => CompleteAuthorityAsync(actor, authority, permission, resources, tx, ct);
    internal async Task<InventoryCountSourceFacts> ReadInventoryCountSourceFactsAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long poolId, string permission, CancellationToken ct) {
        new InventoryRepository(database).RequireSession(tx);
        if (permission is not (Permissions.StockCount or Permissions.StockRead or Permissions.StockSourceRead)) throw InventoryCountStockSourceRecords.Unavailable();
        await SuspensionStorageAsync(ct); var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; var at = Now();
        // Bound each existing scope-membership traversal before the genuine owner
        // reconstructs approved mappings, including disabled historical aliases.
        foreach (var name in new[] { "stockSourceAliases", "stockSourceAliasDecisions", "stockSourceSuspensions" })
            if (await Rows(name).CountDocumentsAsync(tx, F.Eq("poolId", poolId), cancellationToken: ct) > InventoryCountStockSourceRecords.MaximumRows)
                throw InventoryCountStockSourceRecords.Unavailable();
        var context = await SuspensionContextAsync(actor, authority, tx, poolId, permission, ct);
        if (context.ApprovedAt > at || context.Locations.Length > InventoryCountStockSourceRecords.MaximumRows) throw InventoryCountStockSourceRecords.Unavailable();
        var registration = await Rows("inventoryPoolRegistrations").Find(tx, F.Eq("_id", poolId)).SingleOrDefaultAsync(ct) ?? throw InventoryCountStockSourceRecords.Unavailable();
        var setup = await new OrderStockSourceRecords(database).SetupAsync(tx, registration["decisionId"].AsInt64, true, ct);
        if (setup.PoolIds.Length != 1 || setup.PoolIds[0] != poolId) throw InventoryCountStockSourceRecords.Unavailable();
        var input = StockSourceSetupBson.ReadInput(context.Current["input"].AsBsonDocument);
        var binding = new StockCountPoolBinding(poolId, context.Pool["revision"].AsInt64, registration["decisionId"].AsInt64,
            StockSourceSuspensionBson.Hash(context.Configuration), input.PhysicalKey, input.Label, context.Pool["active"].AsBoolean);
        binding.Validate(); new InventoryRepository(database).RequireSession(tx);
        if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || Now() < at) throw InventoryCountStockSourceRecords.Unavailable();
        return new(context.Pool.DeepClone().AsBsonDocument, context.Configuration.DeepClone().AsBsonDocument, binding,
            [.. context.Locations.Select(location => location.ToScope()).OrderBy(scope => scope.ChannelId)], context.Resources, at);
    }
    internal static bool CountSourceMatches(InventoryCountSourceFacts facts, GrantScope requested) {
        StockCountPolicy.Location(requested);
        var location = StockCountLocation(requested);
        var configured = StockSourceSetupBson.ReadInput(facts.Configuration["source"]["input"].AsBsonDocument);
        if (configured.Location == location) return true;
        return facts.Configuration["mappings"].AsBsonArray.Any(alias => alias["requestedLocation"] == StockSourceSetupBson.Location(location) &&
            alias["enabled"] == true && alias["poolRevision"] == facts.Pool["revision"]);
    }
    internal static StockLocation StockCountLocation(GrantScope location) {
        StockCountPolicy.Location(location);
        return new(new(location.OrganizationId ?? throw InventoryCountStockSourceRecords.Unavailable()),
            new(location.ShopId ?? throw InventoryCountStockSourceRecords.Unavailable()), new(location.WarehouseId ?? throw InventoryCountStockSourceRecords.Unavailable()),
            new(location.ChannelId ?? throw InventoryCountStockSourceRecords.Unavailable()));
    }
    internal async Task FenceInventoryCountScopeAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        long poolId, string permission, CancellationToken ct) {
        var facts = await ReadInventoryCountSourceFactsAsync(actor, authority, tx, poolId, permission, ct);
        var person = await ReadInventoryCountPersonAsync(tx, actor.UserId, ct);
        await FenceInventoryCountPersonAsync(tx, person, ct);
        await CompleteAuthorityAsync(actor, authority, permission, facts.Resources, tx, ct); person.RequireCurrent(tx, Now());
    }
    internal async Task VerifyInventoryCountConfigurationAsync(IClientSessionHandle tx, StockCountPoolBinding binding,
        BsonDocument configuration, GrantScope requested, DateTimeOffset at, CancellationToken ct) {
        new InventoryRepository(database).RequireSession(tx); binding.Validate();
        if (at > Now() || StockSourceSuspensionBson.Hash(configuration) != binding.ConfigurationHash ||
            configuration.ElementCount != 2 || !configuration.Contains("source") || !configuration.Contains("mappings")) throw InventoryCountStockSourceRecords.Unavailable();
        var source = configuration["source"].AsBsonDocument;
        StockSourceSuspensionBson.Exact(source, "poolRevision", "active", "input", "approvedAt", "creationProposalId", "revisionProposalId");
        if (source["poolRevision"] != binding.PoolRevision || source["active"] != binding.Active) throw InventoryCountStockSourceRecords.Unavailable();
        var setup = await new OrderStockSourceRecords(database).SetupAsync(tx, binding.SetupDecisionId, true, ct);
        if (setup.PoolIds.Length != 1 || setup.PoolIds[0] != binding.PoolId || source["creationProposalId"] != setup.Record["proposal"]["_id"])
            throw InventoryCountStockSourceRecords.Unavailable();
        var current = await CurrentSourceAsync(tx, binding.PoolId, ct);
        if (current["pool"]["revision"].AsInt64 < binding.PoolRevision) throw InventoryCountStockSourceRecords.Unavailable();
        BsonDocument original;
        if (binding.PoolRevision == 1) {
            if (!source["revisionProposalId"].IsBsonNull) throw InventoryCountStockSourceRecords.Unavailable();
            original = setup.Record["proposal"]["input"].AsBsonDocument;
            if (source["approvedAt"] != setup.Record["decision"]["decidedAt"]) throw InventoryCountStockSourceRecords.Unavailable();
        } else {
            var revision = await RevisionViewAsync(tx, source["revisionProposalId"].AsInt64, ct);
            if (revision["appliedRevision"].IsBsonNull || revision["appliedRevision"]["poolId"] != binding.PoolId ||
                revision["appliedRevision"]["poolRevision"] != binding.PoolRevision || source["approvedAt"] != revision["decision"]["decidedAt"])
                throw InventoryCountStockSourceRecords.Unavailable();
            original = revision["appliedRevision"]["input"].AsBsonDocument;
        }
        if (source["input"] != original || StockSourceSetupBson.Time(source["approvedAt"]) > at) throw InventoryCountStockSourceRecords.Unavailable();
        var input = StockSourceSetupBson.ReadInput(original);
        if (input.PhysicalKey != binding.PhysicalKey || input.Label != binding.Label || current["pool"]["location"] != StockSourceSetupBson.Location(input.Location))
            throw InventoryCountStockSourceRecords.Unavailable();
        long previous = 0;
        if (configuration["mappings"].AsBsonArray.Count > InventoryCountStockSourceRecords.MaximumRows) throw InventoryCountStockSourceRecords.Unavailable();
        foreach (var value in configuration["mappings"].AsBsonArray) {
            var mapping = value.AsBsonDocument;
            StockSourceSuspensionBson.Exact(mapping, "aliasId", "aliasRevision", "poolRevision", "requestedLocation", "enabled", "proposalId", "decisionId");
            var history = await AliasViewAsync(tx, mapping["proposalId"].AsInt64, ct);
            if (history["alias"].IsBsonNull) throw InventoryCountStockSourceRecords.Unavailable(); var alias = history["alias"].AsBsonDocument;
            if (alias["poolId"] != binding.PoolId || alias["_id"] != mapping["aliasId"] || alias["decisionId"] != mapping["decisionId"] ||
                alias["aliasRevision"] != mapping["aliasRevision"] || alias["poolRevision"] != mapping["poolRevision"] ||
                alias["requestedLocation"] != mapping["requestedLocation"] || alias["enabled"] != mapping["enabled"] ||
                StockSourceSetupBson.Time(history["decision"]["decidedAt"]) > at || mapping["requestedLocation"]["channelId"].AsInt64 <= previous)
                throw InventoryCountStockSourceRecords.Unavailable();
            previous = mapping["requestedLocation"]["channelId"].AsInt64;
        }
        var facts = new InventoryCountSourceFacts(current["pool"].AsBsonDocument, configuration, binding, [], [], at);
        // A historical enabled alias is checked at its captured pool revision,
        // rather than today's revision; this preserves legitimate original counts.
        var originalPool = facts.Pool.DeepClone().AsBsonDocument; originalPool["revision"] = binding.PoolRevision;
        if (!CountSourceMatches(facts with { Pool = originalPool }, requested)) throw InventoryCountStockSourceRecords.Unavailable();
    }
}
