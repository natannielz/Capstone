using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Ordering.OrderCommercialPolicyBson;

namespace UnitToko.Platform.Ordering;

internal sealed class OrderCommercialPolicySource
{
    private readonly ActorContext actor;
    private readonly AuthorizationSnapshot authority;
    private readonly IClientSessionHandle tx;
    private readonly long transaction;
    private readonly string hash;
    private readonly Action current;
    private readonly BsonDocument snapshot;
    internal BsonDocument Snapshot => snapshot.DeepClone().AsBsonDocument;
    internal long PolicyId => Snapshot["policyId"].AsInt64;
    internal long VersionId => Snapshot["_id"].AsInt64;
    internal long Version => Snapshot["version"].AsInt64;
    internal OrderCommercialRule Rule => B.Rule(Snapshot["details"]["rule"]);
    internal Func<CancellationToken, Task> FenceAsync { get; }
    internal OrderCommercialTotals Calculate(OrderCatalogQuote quote) {
        RequireCurrent(); var seller = B.Target(snapshot["target"]).Seller;
        if (quote is null || quote.Currency != seller.Currency || quote.Lines.IsDefaultOrEmpty || quote.Lines.Length > 100 ||
            quote.Lines.Any(line => line is null || line.FulfillmentOwner is null || line.FulfillmentOwner.OrganizationId != seller.OrganizationId ||
                line.FulfillmentOwner.ShopId != seller.ShopId || line.FulfillmentOwner.ChannelId != seller.ChannelId)) throw B.Changed();
        var totals = OrderCommercialPolicyRules.Calculate(Rule, quote.Lines.Select(line => line.CatalogSubtotal));
        if (totals.Subtotal != quote.CatalogSubtotal) throw B.Changed(); RequireCurrent(); return totals;
    }
    internal OrderCommercialPolicySource(BsonDocument snapshot, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, Action current, Func<CancellationToken, Task> fence) {
        this.snapshot = snapshot.DeepClone().AsBsonDocument; this.actor = actor; this.authority = authority; this.tx = tx;
        transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; hash = B.Hash(this.snapshot); this.current = current;
        FenceAsync = async token => { RequireCurrent(); await fence(token); RequireCurrent(); token.ThrowIfCancellationRequested(); };
    }
    internal void RequireCurrent() {
        if (!tx.IsInTransaction || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction || B.Hash(snapshot) != hash) throw B.Broken();
        current();
    }
    internal void RequireContext(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx) {
        if (this.actor != actor || !ReferenceEquals(this.authority, authority) || !ReferenceEquals(this.tx, tx)) throw B.Broken(); RequireCurrent();
    }
}

public sealed partial class OrderCommercialPolicyService
{
    /// <summary>Only an exact buyer/seller context can consume this reviewed bundle.
    /// It supplies no capacity, stock reservation, invoice policy IDs or order admission.</summary>
    internal async Task<OrderCommercialPolicySource> ReadCurrentSourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        OrderBuyingContext buying, OperationalBookKey seller, CancellationToken ct) {
        buying.Validate(); AccountingBookSetupRules.Key(seller);
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
        var resource = buying.Resource(actor.UserId) with { FulfillmentOrganizationId = seller.OrganizationId, ShopId = seller.ShopId, ChannelId = seller.ChannelId };
        if (!ScopePolicy.IsResourceActive(resource, authority.Directory) || !Query(actor, authority, Permissions.OrderSubmit).Matches(resource)) throw new AuthorizationDeniedException("scope_denied");
        await StorageAsync(ct);
        var target = buying.DivisionId is { } division ? new OrderCommercialPolicyTarget(ScopeNodeKind.Division, division.Value, seller)
            : new OrderCommercialPolicyTarget(ScopeNodeKind.Branch, buying.BranchId.Value, seller);
        var context = await ContextAsync(actor, authority, tx, target, Permissions.OrderSubmit, ct);
        if (context.Resource != resource) throw new AuthorizationDeniedException("scope_denied");
        var version = await VersionAsync(context, ct);
        if (version is null || !Active(version, context)) throw B.Changed();
        context.NotBefore = new[] { B.Date(version["details"]["validFrom"]), B.Date(version["effectiveAt"]) }.Max(); context.ExpiresAt = B.Date(version["details"]["validUntil"]);
        await ApprovedPeopleAsync(context, ct); await FenceAsync(context, ct); var baseline = version.DeepClone().AsBsonDocument;
        return new(version, actor, authority, tx, () => Current(context), async token => {
            await FenceAsync(context, token);
            var actual = await records.RowAsync(tx, B.Versions, baseline["_id"].AsInt64, token);
            if (!actual.Equals(baseline)) throw B.Changed(); Current(context); token.ThrowIfCancellationRequested();
        });
    }
}
