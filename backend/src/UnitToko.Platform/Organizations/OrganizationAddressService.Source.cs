using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Organizations.OrganizationAddressBson;

namespace UnitToko.Platform.Organizations;

internal sealed class OrganizationPostalSource
{
    private readonly ActorContext actor;
    private readonly AuthorizationSnapshot authority;
    private readonly IClientSessionHandle tx;
    private readonly long transaction;
    private readonly string hash;
    private readonly Action current;
    internal BsonDocument Snapshot { get; }
    internal long AddressId => Snapshot["addressId"].AsInt64;
    internal long VersionId => Snapshot["_id"].AsInt64;
    internal long Version => Snapshot["version"].AsInt64;
    internal PostalAddress Address => B.Details(Snapshot["details"]);
    internal Func<CancellationToken, Task> FenceAsync { get; }
    internal OrganizationPostalSource(BsonDocument snapshot, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, Action current, Func<CancellationToken, Task> fence) {
        Snapshot = snapshot.DeepClone().AsBsonDocument; this.actor = actor; this.authority = authority; this.tx = tx;
        transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; hash = B.Hash(Snapshot); this.current = current;
        FenceAsync = async token => { RequireCurrent(); await fence(token); RequireCurrent(); token.ThrowIfCancellationRequested(); };
    }
    internal void RequireCurrent() {
        if (!tx.IsInTransaction || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction || B.Hash(Snapshot) != hash) throw B.Broken();
        current();
    }
    internal void RequireContext(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx) {
        if (this.actor != actor || !ReferenceEquals(this.authority, authority) || !ReferenceEquals(this.tx, tx)) throw B.Broken(); RequireCurrent();
    }
}

public sealed partial class OrganizationAddressService
{
    /// <summary>The caller supplies its actual scoped operation resource and permission; postal approval never grants that operation.</summary>
    internal async Task<OrganizationPostalSource> ReadCurrentSourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        OrganizationAddressTarget target, OrganizationAddressPurpose purpose, ResourceScope resource, string permission, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || resource.IsAdministrative ||
            permission is not (Permissions.OrderSubmit or Permissions.ShipmentDispatch or Permissions.InvoiceIssue or Permissions.OrganizationAddressRead)) throw B.Broken();
        OrganizationAddressPolicy.RequireTarget(target, purpose); await StorageAsync(ct);
        var nodes = DirectoryTarget(authority, target);
        var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query);
        if (!MatchesTarget(resource, nodes[0], nodes) || !ScopePolicy.IsResourceActive(resource, authority.Directory) || !query.Matches(resource))
            throw new AuthorizationDeniedException("scope_denied");
        var source = await organizations.ReadAddressProfileAsync(tx, target, ct);
        if (!MatchesSource(resource, source)) throw new AuthorizationDeniedException("scope_denied");
        var context = new Context(actor, authority, tx, permission, resource, source) { AddressPurpose = purpose }; _ = Require(context);
        foreach (var node in source["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node))
            if (authority.Directory.Find(node.Id, node.Kind) != node) throw B.Changed();
        context.Head = await HeadAsync(tx, target, purpose, ct);
        if (context.Head is null || context.Head["versionId"].IsBsonNull) throw B.Changed();
        var version = await records.RowAsync(tx, B.Versions, context.Head["versionId"].AsInt64, ct);
        if (!version["source"].Equals(source) || version["effectiveAt"].ToUniversalTime() > Now.UtcDateTime) throw B.Changed();
        await ApprovedPeopleAsync(context, ct);
        // Capture performs its complete real fence; the consumer must execute the same owner fence
        // after its own awaits, in this exact transaction, before it can return an admitted effect.
        await FenceAsync(context, ct); var baseline = version.DeepClone().AsBsonDocument;
        return new(version, actor, authority, tx, () => Current(context), async token => {
            await FenceAsync(context, token);
            var actual = await records.RowAsync(tx, B.Versions, baseline["_id"].AsInt64, token);
            if (!actual.Equals(baseline)) throw B.Changed(); Current(context); token.ThrowIfCancellationRequested();
        });
    }
}
