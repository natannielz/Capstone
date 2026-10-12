using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Organizations;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;

namespace UnitToko.Platform.Ordering;

/// <summary>Actual current buyer/commercial/postal/spending/source/finite-lot
/// admission. Not registered until its owning workflow has passed integration.
/// Required capacity obligations have no implemented owner and always refuse.</summary>
internal sealed partial class ApprovedOrderAdmissionReader : IOrderAdmissionReader
{
    private const int MaximumLots = 100;
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly AuthorizationStore authorization;
    private readonly OrderQuoteService buyers;
    private readonly OrganizationAddressService addresses;
    private readonly OrderSpendingPolicyService spending;
    private readonly OrderCommercialPolicyService commercial;
    private readonly StockSourceSetupService sources;
    private readonly ShipmentDispatchFiniteSource finite;
    // Native weak lifetime: retained proofs belong only to the caller's live
    // session/transaction attempt. No approval or availability result is cached.
    private readonly ConditionalWeakTable<IClientSessionHandle, Frame> frames = new();
    // Substitution only: exact value-transition sources the finite capture authenticated before
    // this transaction wrote; completion re-reads the head chain against exactly these sources.
    private readonly ConditionalWeakTable<ShipmentDispatchFiniteSource.Capture, AuthenticatedValueSources> substitutionValueSources = new();
    private sealed record AuthenticatedValueSources(ImmutableArray<(InventorySource Source, string Hash)> Sources);
    private readonly LotValueStore values;
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    public IMongoDatabase Database => database;
    internal ApprovedOrderAdmissionReader(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        OrganizationMasterDataService organizations) {
        this.database = database; this.clock = clock; authorization = new(database, unit, clock); buyers = new(database, unit, clock);
        addresses = new(database, unit, ids, clock, organizations); spending = new(database, unit, ids, clock, organizations);
        commercial = new(database, unit, ids, clock, organizations); sources = new(database, unit, ids, clock);
        finite = new(database, unit, ids, clock); values = new(database, ids, clock);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private sealed record Lot(OrderStockAllocation Allocation, ShipmentDispatchFiniteSource.Capture Physical,
        StockSourceSetupService.OrderSourceCapture Source, OrderQuotedLine Line);
    private sealed record CandidateSet(FilterDefinition<BsonDocument> Filter, ImmutableArray<InventoryBalanceDocument> Rows);
    private sealed record NotApplicableCapacityDecision(long BundleVersionId, long ProposalId, long DecisionId, long Revision) {
        internal OrderPolicyReference Reference => new(DecisionId, Revision);
        internal void Require(OrderCommercialPolicySource source) {
            var version = source.Snapshot;
            if (version["_id"] != BundleVersionId || version["proposalId"] != ProposalId || version["decisionId"] != DecisionId || Revision != 1 ||
                source.Rule.Applicability.Budget != OrderCommercialApplicability.NotApplicable || source.Rule.Applicability.CostCenter != OrderCommercialApplicability.NotApplicable ||
                source.Rule.Applicability.CreditHold != OrderCommercialApplicability.NotApplicable) throw Unavailable("order_capacity_unavailable");
        }
    }
    private sealed class Frame {
        internal Frame(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, DateTimeOffset at,
            OrderDraftInput input, OrderCatalogQuote quote, OrderAdmission admission, OrganizationPostalSource legal, OrganizationPostalSource delivery,
            OrderSpendingPolicySource spending, OrderCommercialPolicySource commercial, NotApplicableCapacityDecision capacity,
            ImmutableArray<Lot> lots, ImmutableArray<CandidateSet> candidates) {
            Actor = actor; Authority = authority; Tx = tx; At = at; Input = input; Quote = B.Quote(B.Quote(quote)); Admission = admission;
            Legal = legal; Delivery = delivery; Spending = spending; Commercial = commercial; Capacity = capacity; Lots = lots; Candidates = candidates;
            Number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; Binding = AdmissionBinding(admission);
        }
        internal ActorContext Actor { get; } internal AuthorizationSnapshot Authority { get; }
        internal IClientSessionHandle Tx { get; } internal DateTimeOffset At { get; }
        internal long Number { get; }
        internal OrderDraftInput Input { get; } internal OrderCatalogQuote Quote { get; } internal OrderAdmission Admission { get; }
        internal OrganizationPostalSource Legal { get; } internal OrganizationPostalSource Delivery { get; }
        internal OrderSpendingPolicySource Spending { get; } internal OrderCommercialPolicySource Commercial { get; }
        internal NotApplicableCapacityDecision Capacity { get; }
        internal ImmutableArray<Lot> Lots { get; }
        internal ImmutableArray<CandidateSet> Candidates { get; }
        internal BsonDocument Binding { get; }
        internal long? OrderId;
        internal bool ForSubstitution;
        internal long? SubstitutionId;
        internal OrderSubstitutionReleaseCapture? Release;
    }
    private static BsonDocument AdmissionBinding(OrderAdmission value) => new() { { "context", OrderDraftBson.Context(value.Context) },
        { "terms", B.Terms(value.Terms) }, { "allocations", new BsonArray(value.Stock.Select(B.Allocation)) } };
    private static string Postal(PostalAddress address) => address.Line1 + (address.Line2 is null ? "" : "\n" + address.Line2);
    private static string LegalPostal(PostalAddress address) => string.Join(", ", new[] { address.Line1, address.Line2,
        address.City, address.Region, address.PostalCode, address.CountryCode }.OfType<string>());
    private static DomainValidationException Unavailable(string code = "order_admission_unavailable") => OrderAdmissionPolicy.Invalid(code);

    public Task<OrderAdmission> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, OrderDraftInput input,
        OrderCatalogQuote quote, CancellationToken ct) => ReadCoreAsync(tx, actor, input, quote, ct, complete: true);
    internal Task<OrderAdmission> ReadForPreviewAsync(IClientSessionHandle tx, ActorContext actor, OrderDraftInput input,
        OrderCatalogQuote quote, CancellationToken ct) => ReadCoreAsync(tx, actor, input, quote, ct, complete: false);
    private async Task<OrderAdmission> ReadCoreAsync(IClientSessionHandle tx, ActorContext actor, OrderDraftInput input,
        OrderCatalogQuote quote, CancellationToken ct, bool complete, OrderCatalogQuote? replacement = null, OrderSubstitutionReleaseCapture? release = null) {
        new InventoryRepository(database).RequireSession(tx); input = OrderDraftPolicy.Copy(input);
        if ((replacement is null) != (release is null)) throw Unavailable();
        release?.Require(database, clock, actor, tx);
        if (actor.Session is null || quote is null || quote.Lines.IsDefaultOrEmpty || quote.Lines.Length > 100 ||
            quote.Lines.Any(line => line is null || line.Definition is null || line.Price is null || line.FulfillmentOwner is null)) throw Unavailable();
        var at = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
        var authority = await authorization.LoadAsync(actor, tx, ct); buyers.Require(actor, authority, input.Context);
        var sellerKeys = quote.Lines.Select(line => new OperationalBookKey(line.FulfillmentOwner.OrganizationId ?? 0,
            line.FulfillmentOwner.ShopId ?? 0, line.FulfillmentOwner.ChannelId ?? 0, quote.Currency)).Distinct().ToArray();
        // The retained OrderCommercialTerms contract has one immutable bundle reference.
        // A multi-seller commercial aggregate must not be fabricated into that slot.
        if (sellerKeys.Length != 1) throw Unavailable("order_commercial_scope_unavailable");
        var bundle = await commercial.ReadCurrentSourceAsync(actor, authority, tx, input.Context, sellerKeys[0], ct);
        var rule = bundle.Rule;
        if (rule.Applicability.Budget == OrderCommercialApplicability.Required || rule.Applicability.CostCenter == OrderCommercialApplicability.Required ||
            rule.Applicability.CreditHold == OrderCommercialApplicability.Required) throw Unavailable("order_capacity_unavailable");
        var spend = await spending.ReadCurrentSourceAsync(actor, authority, tx, input.Context, ct);
        if ((rule.Applicability.Spending == OrderCommercialApplicability.NotApplicable) != (spend.Rule.Mode == OrderSpendingMode.NotRequired))
            throw Unavailable("order_spending_applicability_changed");
        var resource = input.Context.Resource(actor.UserId);
        var legal = await addresses.ReadCurrentSourceAsync(actor, authority, tx, new(ScopeNodeKind.BuyingOrganization, input.Context.OrganizationId.Value),
            OrganizationAddressPurpose.LegalBilling, resource, Permissions.OrderSubmit, ct);
        var target = input.Context.DivisionId is { } division ? new OrganizationAddressTarget(ScopeNodeKind.Division, division.Value)
            : new OrganizationAddressTarget(ScopeNodeKind.Branch, input.Context.BranchId.Value);
        var delivery = await addresses.ReadCurrentSourceAsync(actor, authority, tx, target, OrganizationAddressPurpose.Delivery, resource, Permissions.OrderSubmit, ct);
        if (input.Delivery is null || input.Delivery.Address != Postal(delivery.Address) || input.Delivery.City != delivery.Address.City ||
            input.Delivery.PostalCode != delivery.Address.PostalCode) throw Unavailable("order_delivery_changed");
        var totals = bundle.Calculate(quote); var policy = new OrderPolicyReference(bundle.PolicyId, bundle.Version);
        var capacityDecision = await NotApplicableDecisionAsync(tx, bundle, ct);
        var terms = new OrderCommercialTerms(policy, policy, capacityDecision.Reference, new(spend.PolicyId, spend.Version), spend.Rule,
            new(new(legal.AddressId, legal.Version), legal.Snapshot["source"]["profile"]["name"].AsString, LegalPostal(legal.Address)),
            new(new(delivery.AddressId, delivery.Version), input.Delivery), totals.Tax, totals.Charges, totals.Total);
        var selectedLots = await SelectLotsAsync(actor, authority, tx, input, replacement ?? quote, ct, release); var lots = selectedLots.Lots;
        var admitted = new OrderAdmission(input.Context, terms, [.. lots.Select(lot => lot.Allocation)]);
        if (replacement is null) OrderAdmissionPolicy.Validate(input, quote, admitted);
        else OrderAdmissionPolicy.ValidateSubstitution(input, quote, replacement, admitted);
        var frame = new Frame(actor, authority, tx, at, input, quote, admitted, legal, delivery, spend, bundle, capacityDecision, lots, selectedLots.Candidates);
        frame.ForSubstitution = replacement is not null;
        frame.Release = release;
        frames.Remove(tx); frames.Add(tx, frame);
        if (complete) await CompleteAsync(tx, actor, admitted, ct);
        else Require(frame, actor, tx, admitted);
        return admitted;
    }
    // The generic legacy CreditDecision slot points to this actual immutable
    // commercial approval decision only when its explicitly reviewed capacity
    // applicability is NotApplicable. It is never a credit amount/approval.
    private async Task<NotApplicableCapacityDecision> NotApplicableDecisionAsync(IClientSessionHandle tx, OrderCommercialPolicySource source, CancellationToken ct) {
        var version = source.Snapshot; var records = new OrderCommercialPolicyRecords(database);
        var proposal = await records.ProposalAsync(tx, version["proposalId"].AsInt64, ct);
        var decision = await records.DecisionAsync(tx, version["decisionId"].AsInt64, proposal, ct);
        if (decision["decision"] != "approve" || decision["versionId"] != version["_id"] || decision["policyVersion"] != version["version"] ||
            source.Rule.Applicability.Budget != OrderCommercialApplicability.NotApplicable || source.Rule.Applicability.CostCenter != OrderCommercialApplicability.NotApplicable ||
            source.Rule.Applicability.CreditHold != OrderCommercialApplicability.NotApplicable) throw Unavailable("order_capacity_unavailable");
        var result = new NotApplicableCapacityDecision(version["_id"].AsInt64, proposal["_id"].AsInt64, decision["_id"].AsInt64, decision["revision"].AsInt64);
        result.Require(source); return result;
    }
    private async Task<(ImmutableArray<Lot> Lots, ImmutableArray<CandidateSet> Candidates)> SelectLotsAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        OrderDraftInput input, OrderCatalogQuote quote, CancellationToken ct, OrderSubstitutionReleaseCapture? release = null) {
        var result = ImmutableArray.CreateBuilder<Lot>(); var membership = ImmutableArray.CreateBuilder<CandidateSet>();
        foreach (var line in quote.Lines.OrderBy(value => value.Definition.SkuId.Value)) {
            var selected = input.Selections.SingleOrDefault(value => value.SkuId == line.Definition.SkuId) ?? throw Unavailable();
            if (selected.DefinitionId != line.Definition.Id || selected.DefinitionVersion != line.Definition.Version || selected.PriceId != line.Price.Id ||
                selected.PriceVersion != line.Price.Version || selected.Packages != line.Packages) throw Unavailable("order_selection_changed");
            var owner = line.FulfillmentOwner;
            var filter = F.Eq("skuId", line.Definition.SkuId.Value) & F.Eq("definitionId", line.Definition.Id.Value) &
                F.Eq("location.organizationId", owner.OrganizationId) & F.Eq("location.shopId", owner.ShopId) & F.Eq("location.channelId", owner.ChannelId);
            if (owner.WarehouseId is { } warehouse) filter &= F.Eq("location.warehouseId", warehouse);
            var rows = await Rows("stockBalances").Find(tx, filter).Sort(new BsonDocument { { "lotId", 1 }, { "_id", 1 } }).Limit(MaximumLots + 1).ToListAsync(ct);
            if (rows.Count > MaximumLots) throw Unavailable("order_stock_selection_limit");
            membership.Add(new(filter, [.. rows.Select(InventoryBson.ReadBalance)]));
            var eligible = new List<(InventoryBalanceDocument Balance, ShipmentDispatchFiniteSource.Capture Proof, StockSourceSetupService.OrderSourceCapture Source)>();
            var control = new Dictionary<long, StockSourceSetupService.OrderSourceCapture>();
            foreach (var row in rows) {
                var physical = InventoryBson.ReadBalance(row);
                if (StockBalancePolicy.AvailablePackages((release?.BeforeReserve(physical) ?? physical).Balance) <= 0) continue;
                if (!control.TryGetValue(physical.PhysicalPoolId, out var source)) {
                    var pool = await Rows("inventoryPools").Find(tx, F.Eq("_id", physical.PhysicalPoolId)).SingleOrDefaultAsync(ct) ?? throw Unavailable();
                    source = await sources.CaptureOrderSubmitSourceAsync(actor, authority, tx, input.Context, line, physical.Balance.Key.Location,
                        physical.PhysicalPoolId, pool["revision"].AsInt64, ct); control.Add(physical.PhysicalPoolId, source);
                }
                var proof = await finite.ReadAsync(tx, physical, ct);
                if (proof.Value.RemainingPackages != checked(physical.Balance.OnHand - physical.Balance.Held - physical.Balance.Expired)) throw Unavailable();
                if (release is not null) {
                    // Same transaction, no write since the full authentication above: record its exact sources.
                    var authenticated = ImmutableArray.CreateBuilder<(InventorySource Source, string Hash)>();
                    var recorded = await values.ReadAvailableAsync(tx, physical, (_, _, source, hash, _) => {
                        authenticated.Add((source, hash)); return Task.CompletedTask; }, ct);
                    if (recorded != proof.Value || authenticated.Count == 0) throw Unavailable();
                    substitutionValueSources.AddOrUpdate(proof, new(authenticated.ToImmutable()));
                }
                eligible.Add((physical, proof, source));
            }
            var remaining = (long)line.Packages;
            foreach (var pool in eligible.GroupBy(value => value.Balance.PhysicalPoolId).OrderBy(group => group.Key)) {
                var source = control[pool.Key]; var setup = StockSourceSetupBson.ReadInput(source.Snapshot["configuration"]["source"]["input"].AsBsonDocument);
                var buffer = checked(setup.SafetyBufferBaseUnits / line.Definition.BaseUnitsPerPackage +
                    (setup.SafetyBufferBaseUnits % line.Definition.BaseUnitsPerPackage == 0 ? 0 : 1));
                var available = pool.Aggregate(0L, (sum, value) => checked(sum + StockBalancePolicy.AvailablePackages((release?.BeforeReserve(value.Balance) ?? value.Balance).Balance)));
                var limit = Math.Max(0, checked(available - buffer));
                foreach (var candidate in pool.OrderBy(value => value.Balance.Balance.Key.LotId.Value).ThenBy(value => value.Balance.Id)) {
                    var reserveBefore = release?.BeforeReserve(candidate.Balance) ?? candidate.Balance;
                    var quantity = Math.Min(remaining, Math.Min(limit, StockBalancePolicy.AvailablePackages(reserveBefore.Balance)));
                    if (quantity == 0) continue;
                    if (result.Count >= MaximumLots) throw Unavailable("order_stock_selection_limit");
                    var change = new InventoryBalanceChange(candidate.Balance.Id, candidate.Balance.PhysicalPoolId, candidate.Balance.Balance.Key,
                        reserveBefore.Balance.Revision, InventoryBalanceAction.Reserve, quantity);
                    result.Add(new(new(line.Definition.SkuId.Value, source.Authority, source.Pool, change, line.LineRevision), candidate.Proof, source, line));
                    remaining -= quantity; limit -= quantity;
                }
            }
            if (remaining != 0) throw Unavailable("order_stock_unavailable");
        }
        return (result.ToImmutable(), membership.ToImmutable());
    }
    public async Task CompleteAsync(IClientSessionHandle tx, ActorContext actor, OrderAdmission admission, CancellationToken ct) {
        if (!frames.TryGetValue(tx, out var frame)) throw Unavailable();
        Require(frame, actor, tx, admission);
        foreach (var postal in new[] { frame.Legal, frame.Delivery }) { postal.RequireContext(actor, frame.Authority, tx); await postal.FenceAsync(ct); }
        frame.Spending.RequireContext(actor, frame.Authority, tx); await frame.Spending.FenceAsync(ct);
        frame.Commercial.RequireContext(actor, frame.Authority, tx); await frame.Commercial.FenceAsync(ct);
        foreach (var source in frame.Lots.Select(lot => lot.Source).Distinct()) await source.FenceAsync(ct);
        if (frame.Release is { } release) {
            await release.FenceAsync(frame.SubstitutionId is not null, ct);
            if (frame.SubstitutionId is not null) await RequireSubstitutionReleaseAsync(frame, ct);
        }
        foreach (var candidateSet in frame.Candidates) {
            var current = await Rows("stockBalances").Find(tx, candidateSet.Filter).Sort(new BsonDocument { { "lotId", 1 }, { "_id", 1 } })
                .Limit(MaximumLots + 1).ToListAsync(ct);
            if (current.Count != candidateSet.Rows.Length) throw Unavailable("order_stock_changed");
            for (var index = 0; index < current.Count; index++) {
                var actual = InventoryBson.ReadBalance(current[index]); var before = candidateSet.Rows[index];
                var chosen = frame.Lots.SingleOrDefault(lot => lot.Allocation.Change.Id == before.Id);
                var released = frame.Release?.BeforeReserve(before) ?? before;
                var after = chosen is null ? released : released with { Balance = StockBalancePolicy.Reserve(released.Balance,
                    chosen.Allocation.Change.ExpectedRevision, chosen.Allocation.Change.Packages) };
                if (actual != before && actual != after) throw Unavailable("order_stock_changed");
            }
        }
        foreach (var lot in frame.Lots) {
            var reserveBefore = frame.Release?.BeforeReserve(lot.Physical.Before) ?? lot.Physical.Before;
            var after = reserveBefore with { Balance = StockBalancePolicy.Reserve(reserveBefore.Balance,
                lot.Allocation.Change.ExpectedRevision, lot.Allocation.Change.Packages) };
            if (frame.SubstitutionId is not null) {
                // The substitution's own release/reserve movements may join the source-lot membership pinned before
                // this write. Its own writes never change lot value: require the exact reserved balance and the same
                // value head chain, every transition being one already authenticated by this capture before writing.
                if (!substitutionValueSources.TryGetValue(lot.Physical, out var authenticated)) throw Unavailable();
                lot.Physical.Require(database, tx);
                var reservedPhysical = await new InventoryRepository(database).ReadAsync(tx, lot.Physical.Before.Id, ct);
                if (reservedPhysical != after) throw Unavailable("order_stock_changed");
                var seen = 0;
                var reservedValue = await values.ReadAvailableAsync(tx, reservedPhysical, (_, _, source, hash, _) => {
                    if (seen >= authenticated.Sources.Length || authenticated.Sources[seen] != (source, hash)) throw Unavailable("order_stock_changed");
                    seen++; return Task.CompletedTask; }, ct);
                if (seen != authenticated.Sources.Length || reservedValue != lot.Physical.Value) throw Unavailable("order_stock_changed");
                await RequireOwnReserveAsync(frame, lot, after, ct);
                continue;
            }
            var checkedValue = await lot.Physical.ReadCurrentValueWithOriginAsync(database, tx, ct); var actual = checkedValue.Physical;
            if (actual != lot.Physical.Before && actual != after) throw Unavailable("order_stock_changed");
            if (actual == after) await RequireOwnReserveAsync(frame, lot, after, ct);
        }
        await buyers.FenceAsync(actor, frame.Authority, tx, [frame.Input.Context], ct);
        Require(frame, actor, tx, admission);
    }
    private void Require(Frame frame, ActorContext actor, IClientSessionHandle tx, OrderAdmission admission) {
        new InventoryRepository(database).RequireSession(tx);
        if (frame.Actor != actor || !ReferenceEquals(frame.Tx, tx) || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != frame.Number ||
            clock.UtcNow < frame.At || AdmissionBinding(admission) != frame.Binding) throw Unavailable();
        buyers.Require(actor, frame.Authority, frame.Input.Context);
        frame.Legal.RequireCurrent(); frame.Delivery.RequireCurrent(); frame.Spending.RequireCurrent(); frame.Commercial.RequireCurrent();
        frame.Capacity.Require(frame.Commercial);
        frame.Release?.Require(database, clock, actor, tx);
        foreach (var lot in frame.Lots) {
            lot.Source.RequireContext(sources, actor, frame.Authority, tx, frame.Input.Context, lot.Line);
            lot.Physical.Require(database, tx);
        }
    }
    private async Task RequireOwnReserveAsync(Frame frame, Lot lot, InventoryBalanceDocument after, CancellationToken ct) {
        if (frame.ForSubstitution) { await RequireSubstitutionReserveAsync(frame, lot, after, ct); return; }
        var candidates = await Rows(B.Orders).Find(frame.Tx, F.Eq("createdBy", frame.Actor.UserId) & F.Gte("orderedAt", frame.At.UtcDateTime) &
            F.Eq("allocations.balanceId", lot.Allocation.Change.Id)).Limit(3).ToListAsync(ct);
        var expectedAllocations = new BsonArray(frame.Admission.Stock.Select(B.Allocation));
        var orders = candidates.Where(row => row["input"] == OrderDraftBson.Input(frame.Input) && row["quote"] == B.Quote(frame.Quote) &&
            row["terms"] == B.Terms(frame.Admission.Terms) && row["allocations"] == expectedAllocations).ToArray();
        if (orders.Length != 1) throw Unavailable("order_reservation_owner_unavailable"); var order = orders[0];
        if (frame.OrderId is { } original && original != order["_id"].AsInt64 ||
            !await Rows(B.Orders).Find(frame.Tx, F.Eq("_id", order["_id"]) & OrderLineageSchemas.OrderValidator(B.Orders)).AnyAsync(ct) ||
            order["snapshotHash"] != B.SnapshotHash(order) || order["actorGrantVersion"] != frame.Actor.GrantVersion ||
            order["review"] != B.Review(frame.Input, frame.Quote, frame.Admission.Terms) ||
            order["bodyHash"] != AuthorizationStore.CommandHash(new { input = frame.Input, review = order["review"].AsString }, order["reason"].AsString))
            throw Unavailable("order_reservation_owner_unavailable");
        frame.OrderId = order["_id"].AsInt64;
        var receipt = await Rows("idempotencyRecords").Find(frame.Tx, F.Eq("actorId", frame.Actor.UserId) & F.Eq("operation", B.Operation) &
            F.Eq("key", order["requestKey"])).SingleOrDefaultAsync(ct) ?? throw Unavailable("order_reservation_owner_unavailable");
        if (receipt["bodyHash"] != order["bodyHash"] || receipt["createdAt"].CompareTo(order["orderedAt"]) > 0 ||
            receipt["result"] != new BsonDocument() && receipt["result"] != B.Result(order)) throw Unavailable("order_reservation_owner_unavailable");
        if (receipt["result"] != new BsonDocument())
            _ = await OrderSubmissionService.ReadStockSourceEventAsync(database, frame.Tx, "submission", frame.OrderId.Value, true, ct);
        var reservations = await Rows(B.Reservations).Find(frame.Tx, F.Eq("orderId", order["_id"])).Limit(101).ToListAsync(ct);
        if (reservations.Count != frame.Admission.Stock.Length || reservations.Any(row => row["revision"] != 1L || row["released"] != 0L || row["dispatched"] != 0L ||
            row["remaining"] != row["allocation"]["packages"] || expectedAllocations.Count(value => value == row["allocation"]) != 1 ||
            !order["suborderIds"].AsBsonArray.Contains(row["suborderId"]))) throw Unavailable("order_reservation_owner_unavailable");
        var source = new InventorySource("ordering.order", frame.OrderId.Value, "reserve", lot.Allocation.Pool.Id);
        var movementRows = await Rows("movements").Find(frame.Tx, F.Eq("sourceEvent", source.EventKey) |
            (F.Eq("source.namespace", source.Namespace) & F.Eq("source.documentId", source.DocumentId) & F.Eq("source.eventKind", source.EventKind) & F.Eq("source.occurrence", source.Occurrence)) |
            F.ElemMatch("legs", F.Eq("balanceId", lot.Allocation.Change.Id) & F.Eq("before.version", lot.Physical.Before.Balance.Revision) & F.Eq("after.version", after.Balance.Revision)))
            .Limit(2).ToListAsync(ct);
        if (movementRows.Count != 1) throw Unavailable("order_reservation_owner_unavailable"); var movement = movementRows[0];
        if (!await Rows("movements").Find(frame.Tx, F.Eq("_id", movement["_id"]) & InventoryMovementSchemas.Validator()).AnyAsync(ct) ||
            movement["sourceEvent"] != source.EventKey || movement["sourceRevision"] != 1L || movement["physicalPoolId"] != source.Occurrence ||
            movement["poolRevision"] != lot.Allocation.Pool.Revision || movement["actorId"] != frame.Actor.UserId || movement["actorGrantVersion"] != frame.Actor.GrantVersion ||
            movement["reason"] != order["reason"] || movement["correlationId"] != order["correlationId"] || movement["recordedAt"].CompareTo(order["orderedAt"]) < 0 ||
            movement["source"] != new BsonDocument { { "namespace", source.Namespace }, { "documentId", source.DocumentId }, { "eventKind", source.EventKind }, { "occurrence", source.Occurrence } })
            throw Unavailable("order_reservation_owner_unavailable");
        var expectedLegs = frame.Lots.Where(value => value.Allocation.Pool.Id == source.Occurrence).Select(value => new BsonDocument {
            { "balanceId", value.Physical.Before.Id }, { "action", "Reserve" }, { "packages", value.Allocation.Change.Packages },
            { "before", InventoryBson.Balance(value.Physical.Before) }, { "after", InventoryBson.Balance(value.Physical.Before with {
                Balance = StockBalancePolicy.Reserve(value.Physical.Before.Balance, value.Allocation.Change.ExpectedRevision, value.Allocation.Change.Packages) }) } }).ToArray();
        if (movement["legs"].AsBsonArray.Count != expectedLegs.Length || expectedLegs.Any(leg => movement["legs"].AsBsonArray.Count(value => value == leg) != 1))
            throw Unavailable("order_reservation_owner_unavailable");
    }
}
