using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Organizations;
using B = UnitToko.Platform.Fulfillment.ShipmentDispatchBson;

namespace UnitToko.Platform.Fulfillment;

/// <summary>Dispatcher admission through actual approved source and postal owners.
/// Finite consumption and COGS remain owned by ShipmentDispatchService. Unregistered.</summary>
internal sealed class ApprovedShipmentDispatchAdmissionReader : IShipmentDispatchAdmissionReader
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly OrderSubmissionService orders;
    private readonly AuthorizationStore authorization;
    private readonly OrganizationAddressService addresses;
    private readonly StockSourceSetupService sources;
    private readonly ShipmentDispatchFiniteSource finite;
    private readonly ConditionalWeakTable<IClientSessionHandle, Frame> frames = new();
    private ShipmentDispatchService? dispatcher;
    public IMongoDatabase Database => database;
    internal ApprovedShipmentDispatchAdmissionReader(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        OrderSubmissionService orders, OrganizationMasterDataService organizations) {
        if (!ReferenceEquals(database.Client, orders.SourceDatabase.Client) || !database.DatabaseNamespace.Equals(orders.SourceDatabase.DatabaseNamespace) ||
            !ReferenceEquals(database.Client, organizations.AddressSourceDatabase.Client) || !database.DatabaseNamespace.Equals(organizations.AddressSourceDatabase.DatabaseNamespace))
            throw new ArgumentException("Dispatch admission must use the actual ordering database owner");
        this.database = database; this.clock = clock; this.orders = orders; authorization = new(database, unit, clock);
        addresses = new(database, unit, ids, clock, organizations); sources = new(database, unit, ids, clock); finite = new(database, unit, ids, clock);
    }
    internal void AttachDispatchOwner(ShipmentDispatchService owner) {
        owner.RequireAdmissionOwner(this, database, clock, orders);
        if (Interlocked.CompareExchange(ref dispatcher, owner, null) is not null) throw ShipmentDispatchAdmissionPolicy.Invalid();
    }
    private sealed record Lot(OrderDispatchReservation Reservation, ShipmentLotSelection Selected,
        ShipmentDispatchFiniteSource.Capture Physical, StockSourceSetupService.DispatchSourceCapture Source);
    private sealed class Frame(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, DateTimeOffset at,
        OrderDispatchSource source, ShipmentDispatchAdmission admission, OrganizationPostalSource seller, ImmutableArray<Lot> lots) {
        internal ActorContext Actor { get; } = actor;
        internal AuthorizationSnapshot Authority { get; } = authority;
        internal IClientSessionHandle Tx { get; } = tx;
        internal long Number { get; } = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        internal DateTimeOffset At { get; } = at;
        internal OrderDispatchSource Source { get; } = source;
        internal BsonDocument SourceSnapshot { get; } = SourceBinding(source).DeepClone().AsBsonDocument;
        internal ShipmentDispatchAdmission Admission { get; } = admission;
        internal BsonDocument Binding { get; } = AdmissionBinding(admission);
        internal OrganizationPostalSource Seller { get; } = seller;
        internal ImmutableArray<Lot> Lots { get; } = lots;
    }
    private static BsonDocument AdmissionBinding(ShipmentDispatchAdmission admission) {
        var value = B.Admission(admission); value.Remove("finiteValue"); return value;
    }
    private static BsonDocument SourceBinding(OrderDispatchSource source) => new() {
        { "order", ShipmentPreparationBson.Source(source.Order.View) }, { "scope", ShipmentPreparationBson.Scope(source.Order.Resource) },
        { "reservations", new BsonArray(source.Reservations.Select(row => new BsonDocument {
            { "row", row.Reservation }, { "allocation", OrderSubmissionBson.Allocation(row.Allocation) } })) } };
    private static string LegalPostal(PostalAddress address) => string.Join(", ", new[] {
        address.Line1, address.Line2, address.City, address.Region, address.PostalCode, address.CountryCode }.OfType<string>());
    private void Require(Frame frame, ActorContext actor, IClientSessionHandle tx, ShipmentDispatchAdmission admission) {
        new InventoryRepository(database).RequireSession(tx);
        if (dispatcher is null || frame.Actor != actor || !ReferenceEquals(frame.Tx, tx) || frame.Number != tx.WrappedCoreSession.CurrentTransaction.TransactionNumber ||
            clock.UtcNow == default || clock.UtcNow.Offset != TimeSpan.Zero || clock.UtcNow < frame.At || frame.Binding != AdmissionBinding(admission))
            throw ShipmentDispatchAdmissionPolicy.Invalid();
        if (!ScopePolicy.Authorize(actor, frame.Authority, Permissions.ShipmentDispatch, frame.Source.Order.Resource, clock.UtcNow).Allowed)
            throw ShipmentDispatchAdmissionPolicy.Invalid();
        ShipmentDispatchAdmissionPolicy.RequireCurrent(admission, clock.UtcNow);
    }
    // This is the already authenticated finite origin in this reader's private
    // live attempt. The attached dispatch owner still rechecks current quantity,
    // value, origin membership, source authority and expiry at completion.
    internal ShipmentDispatchFiniteSource.Capture RetainedFiniteCapture(ShipmentDispatchService owner, IMongoDatabase current,
        IClientSessionHandle tx, ActorContext actor, OrderDispatchSource source, ImmutableArray<ShipmentLotSelection> selected,
        ShipmentDispatchAdmission admission, ShipmentLotSelection chosen, InventoryBalanceDocument before) {
        owner.RequireAdmissionOwner(this, current, clock, orders);
        if (!ReferenceEquals(dispatcher, owner) || !ReferenceEquals(database.Client, current.Client) || !database.DatabaseNamespace.Equals(current.DatabaseNamespace) ||
            !frames.TryGetValue(tx, out var frame)) throw ShipmentDispatchAdmissionPolicy.Invalid();
        Require(frame, actor, tx, admission);
        if (!ReferenceEquals(frame.Admission, admission) || source is null || frame.SourceSnapshot != SourceBinding(source) ||
            selected.IsDefaultOrEmpty || !frame.Lots.Select(lot => lot.Selected).SequenceEqual(selected)) throw ShipmentDispatchAdmissionPolicy.Invalid();
        var lot = frame.Lots.SingleOrDefault(value => value.Selected == chosen) ?? throw ShipmentDispatchAdmissionPolicy.Invalid();
        if (lot.Physical.Before != before) throw ShipmentDispatchAdmissionPolicy.Invalid();
        frame.Seller.RequireContext(actor, frame.Authority, tx);
        lot.Source.RequireContext(sources, actor, frame.Authority, tx, frame.Source.Order.Resource,
            before.Balance.Key.Location, lot.Reservation.Allocation.SourceAuthority);
        lot.Physical.Require(current, tx); Require(frame, actor, tx, admission); return lot.Physical;
    }
    public Task<ShipmentDispatchAdmission> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, OrderDispatchSource source,
        ImmutableArray<ShipmentLotSelection> selected, CancellationToken ct) => ReadCoreAsync(tx, actor, source, selected, ct, complete: true);
    internal Task<ShipmentDispatchAdmission> ReadForPreviewAsync(IClientSessionHandle tx, ActorContext actor, OrderDispatchSource source,
        ImmutableArray<ShipmentLotSelection> selected, CancellationToken ct) => ReadCoreAsync(tx, actor, source, selected, ct, complete: false);
    private async Task<ShipmentDispatchAdmission> ReadCoreAsync(IClientSessionHandle tx, ActorContext actor, OrderDispatchSource source,
        ImmutableArray<ShipmentLotSelection> selected, CancellationToken ct, bool complete) {
        new InventoryRepository(database).RequireSession(tx); selected = ShipmentDispatchPolicy.Copy(selected);
        if (dispatcher is null || actor.Session is null || source is null || source.Reservations.IsDefaultOrEmpty || selected.IsDefaultOrEmpty ||
            clock.UtcNow == default || clock.UtcNow.Offset != TimeSpan.Zero) throw ShipmentDispatchAdmissionPolicy.Invalid();
        var at = clock.UtcNow; var authority = await authorization.LoadAsync(actor, tx, ct);
        var actual = await orders.ReadDispatchReservationsAsync(actor, authority, tx, source.Order.View.SuborderId.Value, ct);
        if (SourceBinding(actual) != SourceBinding(source)) throw ShipmentDispatchAdmissionPolicy.Invalid();
        var resource = actual.Order.Resource;
        if (resource.FulfillmentOrganizationId is not > 0 || resource.ShopId is not > 0 || resource.ChannelId is not > 0)
            throw ShipmentDispatchAdmissionPolicy.Invalid();
        var seller = await addresses.ReadCurrentSourceAsync(actor, authority, tx,
            new(ScopeNodeKind.FulfillmentOrganization, resource.FulfillmentOrganizationId.Value), OrganizationAddressPurpose.LegalBilling,
            resource, Permissions.ShipmentDispatch, ct);
        var captures = ImmutableArray.CreateBuilder<Lot>(); var lots = ImmutableArray.CreateBuilder<ShipmentLotAuthority>();
        var control = new Dictionary<long, StockSourceSetupService.DispatchSourceCapture>();
        OrderPolicyReference? operatingPolicy = null; BsonDocument? inventory = null;
        foreach (var chosen in selected) {
            var reserved = actual.Reservations.SingleOrDefault(row => row.Fact.ReservationId == chosen.ReservationId) ?? throw ShipmentDispatchAdmissionPolicy.Invalid();
            var allocation = reserved.Allocation;
            if (chosen.Packages > reserved.Fact.RemainingPackages) throw ShipmentDispatchAdmissionPolicy.Invalid();
            var physical = await new InventoryRepository(database).ReadAsync(tx, allocation.Change.Id, ct);
            if (physical.PhysicalPoolId != allocation.Pool.Id || physical.Balance.Key != allocation.Change.Key || physical.Balance.Revision != allocation.Change.ExpectedRevision)
                throw ShipmentDispatchAdmissionPolicy.Invalid();
            if (!control.TryGetValue(physical.PhysicalPoolId, out var sourceControl)) {
                sourceControl = await sources.CaptureShipmentDispatchSourceAsync(actor, authority, tx, resource, physical.Balance.Key.Location,
                    allocation.Pool.Id, allocation.Pool.Revision, allocation.SourceAuthority, ct); control.Add(physical.PhysicalPoolId, sourceControl);
            }
            sourceControl.RequireContext(sources, actor, authority, tx, resource, physical.Balance.Key.Location, allocation.SourceAuthority);
            if (operatingPolicy is not null && operatingPolicy != sourceControl.Authority) throw ShipmentDispatchAdmissionPolicy.Invalid();
            operatingPolicy = sourceControl.Authority;
            var captured = await finite.ReadAsync(tx, physical, ct);
            _ = StockBalancePolicy.Dispatch(physical.Balance, physical.Balance.Revision, chosen.Packages);
            if (inventory is not null && inventory != captured.InventoryAccount) throw ShipmentDispatchAdmissionPolicy.Invalid(); inventory = captured.InventoryAccount;
            var package = LotValuePolicy.TakeProportionalFloor(captured.Value, captured.Value.Revision, 1).TransferredValue;
            lots.Add(new(chosen.ReservationId.Value, allocation.SourceAuthority, new(physical.Id, captured.Value.Revision), package));
            captures.Add(new(reserved, chosen, captured, sourceControl));
        }
        if (captures.Select(value => value.Physical.Before.Id).Distinct().Count() != captures.Count) throw ShipmentDispatchAdmissionPolicy.Invalid();
        var admission = new ShipmentDispatchAdmission(new(new(seller.AddressId, seller.Version), seller.Snapshot["source"]["profile"]["name"].AsString,
            LegalPostal(seller.Address)), operatingPolicy!, lots.ToImmutable(), captures.Min(value => value.Source.ValidUntil));
        var frame = new Frame(actor, authority, tx, at, actual, admission, seller, captures.ToImmutable());
        frames.Remove(tx); frames.Add(tx, frame);
        if (complete) await CompleteAsync(tx, actor, admission, ct);
        else Require(frame, actor, tx, admission);
        return admission;
    }
    public async Task CompleteAsync(IClientSessionHandle tx, ActorContext actor, ShipmentDispatchAdmission admission, CancellationToken ct) {
        if (!frames.TryGetValue(tx, out var frame)) throw ShipmentDispatchAdmissionPolicy.Invalid(); Require(frame, actor, tx, admission);
        frame.Seller.RequireContext(actor, frame.Authority, tx); await frame.Seller.FenceAsync(ct);
        foreach (var control in frame.Lots.Select(value => value.Source).Distinct()) await control.FenceAsync(ct);
        foreach (var lot in frame.Lots) {
            lot.Source.RequireContext(sources, actor, frame.Authority, tx, frame.Source.Order.Resource, lot.Physical.Before.Balance.Key.Location, lot.Reservation.Allocation.SourceAuthority);
            var written = await dispatcher!.RequireAdmissionPhysicalAsync(this, tx, actor, admission, lot.Selected.ReservationId.Value,
                lot.Physical.Before, lot.Selected.Packages, ct);
            if (written) await lot.Physical.RequireOriginAsync(database, tx, ct);
            else await lot.Physical.RequireBeforeAsync(database, tx, ct);
            lot.Source.RequireContext(sources, actor, frame.Authority, tx, frame.Source.Order.Resource, lot.Physical.Before.Balance.Key.Location, lot.Reservation.Allocation.SourceAuthority);
        }
        frame.Seller.RequireContext(actor, frame.Authority, tx); Require(frame, actor, tx, admission); ct.ThrowIfCancellationRequested();
    }
}
