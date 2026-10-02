using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Ordering.OrderSpendingPolicyBson;

namespace UnitToko.Platform.Ordering;

internal sealed class OrderSpendingPolicySource
{
    private readonly ActorContext actor;
    private readonly AuthorizationSnapshot authority;
    private readonly IClientSessionHandle tx;
    private readonly long transaction;
    private readonly string hash;
    private readonly Action current;
    internal BsonDocument Snapshot { get; }
    internal long PolicyId => Snapshot["policyId"].AsInt64;
    internal long VersionId => Snapshot["_id"].AsInt64;
    internal long Version => Snapshot["version"].AsInt64;
    internal OrderSpendingRule Rule => B.Rule(Snapshot["details"]["rule"]);
    internal Func<CancellationToken, Task> FenceAsync { get; }
    internal OrderSpendingPolicySource(BsonDocument snapshot, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, Action current, Func<CancellationToken, Task> fence) {
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

public sealed partial class OrderSpendingPolicyService
{
    /// <summary>Only an exact current order context can consume this source. It never admits an order.</summary>
    internal async Task<OrderSpendingPolicySource> ReadCurrentSourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        OrderBuyingContext buying, CancellationToken ct) {
        buying.Validate();
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
        var resource = buying.Resource(actor.UserId);
        if (!ScopePolicy.IsResourceActive(resource, authority.Directory) || !Query(actor, authority, Permissions.OrderSubmit).Matches(resource)) throw new AuthorizationDeniedException("scope_denied");
        await StorageAsync(ct);
        var target = buying.DivisionId is { } division ? new OrderSpendingPolicyTarget(ScopeNodeKind.Division, division.Value)
            : new OrderSpendingPolicyTarget(ScopeNodeKind.Branch, buying.BranchId.Value);
        var context = await ContextAsync(actor, authority, tx, target, Permissions.OrderSubmit, ct);
        if (context.Resource != resource) throw new AuthorizationDeniedException("scope_denied");
        var version = await VersionAsync(context, ct);
        if (version is null || !Active(version, context.Source)) throw B.Changed();
        context.NotBefore = B.Date(version["details"]["validFrom"]); context.ExpiresAt = B.Date(version["details"]["validUntil"]);
        await ApprovedPeopleAsync(context, ct); await FenceAsync(context, ct); var baseline = version.DeepClone().AsBsonDocument;
        return new(version, actor, authority, tx, () => Current(context), async token => {
            await FenceAsync(context, token);
            var actual = await records.RowAsync(tx, B.Versions, baseline["_id"].AsInt64, token);
            if (!actual.Equals(baseline)) throw B.Changed(); Current(context); token.ThrowIfCancellationRequested();
        });
    }
}
