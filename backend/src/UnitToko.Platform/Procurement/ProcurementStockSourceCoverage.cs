using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Procurement;

internal sealed record ProcurementCoverageSnapshot(BsonDocument Head, long OpenCount);

/// <summary>Fresh causal membership, separate from the retained held-receipt protocol.
/// An absent old association is an admission blocker, never an empty source.</summary>
internal sealed class ProcurementStockSourceCoverage(IMongoDatabase database, IdAllocator ids, IClock clock)
{
    internal const string Heads = "procurementStockSourceCoverageHeads", Transitions = "procurementStockSourceCoverageTransitions";
    internal const int MaximumRows = ProcurementStockSourceRecords.MaximumRows;
    internal static readonly (string Kind, string Table)[] Sources = [
        ("request", ProcurementRequestBson.Requests), ("requestDecision", ProcurementRequestBson.Decisions),
        ("proposal", PurchaseOrderBson.Proposals), ("decision", PurchaseOrderBson.Decisions),
        ("notice", ProcurementReceivingBson.Notices), ("intake", ProcurementReceivingBson.Intakes),
        ("inspectionDraft", ProcurementInspectionBson.Drafts), ("inspection", ProcurementInspectionBson.Submissions),
        ("claim", SupplierClaimBson.Requests), ("claimDecision", SupplierClaimBson.Decisions) ];
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static PersistenceConflictException Unavailable() => new("procurement_stock_source_coverage_unavailable",
        "Procurement causal source membership is missing or cannot be authenticated");
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name != "transitionHash")));
    private void Context(IClientSessionHandle tx) {
        new InventoryRepository(database).RequireSession(tx);
        if (clock.UtcNow == default || clock.UtcNow.Offset != TimeSpan.Zero) throw Unavailable();
    }
    private StockSourceSetupService SourceOwner => new(database, new((MongoClient)database.Client), ids, clock);
    internal static BsonDocument SourceValidator(string kind) => kind switch {
        "request" => ProcurementRequestSchemas.Validator(ProcurementRequestBson.Requests),
        "requestDecision" => ProcurementRequestSchemas.Validator(ProcurementRequestBson.Decisions),
        "proposal" => PurchaseOrderSchemas.Validator(PurchaseOrderBson.Proposals),
        "decision" => PurchaseOrderSchemas.Validator(PurchaseOrderBson.Decisions),
        "notice" => ProcurementReceivingSchemas.Validator(ProcurementReceivingBson.Notices),
        "intake" => ProcurementReceivingSchemas.Validator(ProcurementReceivingBson.Intakes),
        "inspectionDraft" => ProcurementInspectionSchemas.Validator(ProcurementInspectionBson.Drafts),
        "inspection" => ProcurementInspectionSchemas.Validator(ProcurementInspectionBson.Submissions),
        "claim" => SupplierClaimSchemas.Validator(SupplierClaimBson.Requests),
        "claimDecision" => SupplierClaimSchemas.Validator(SupplierClaimBson.Decisions),
        _ => throw Unavailable() };
    internal static string SourceHash(string kind, BsonDocument row) => kind == "inspectionDraft"
        ? ProcurementInspectionBson.DraftHash(row) : ProcurementRequestBson.Hash(row);
    private async Task<BsonDocument[]> BoundedAsync(IClientSessionHandle tx, string name, BsonDocument filter, CancellationToken ct) {
        var rows = await Rows(name).Find(tx, filter).Sort(new BsonDocument("_id", 1)).Limit(MaximumRows + 1).ToListAsync(ct);
        if (rows.Count > MaximumRows) throw Unavailable(); return rows.ToArray();
    }
    private async Task<BsonDocument> HeadAsync(IClientSessionHandle tx, long poolId, CancellationToken ct) =>
        await Rows(Heads).Find(tx, new BsonDocument { { "_id", poolId }, { "$and", new BsonArray { ProcurementStockSourceCoverageSchemas.Validator(Heads) } } })
            .SingleOrDefaultAsync(ct) ?? throw Unavailable();
    private async Task<long[]> TopologyAsync(IClientSessionHandle tx, BsonDocument location, CancellationToken ct) {
        Context(tx); var requested = InventoryGraph.Location(location);
        // Shared with setup and alias changes: even the absence of a pool is serialized.
        await new StockSourceTopologyFence(database).FenceAsync(tx, requested.OrganizationId.Value, ct);
        var pools = await BoundedAsync(tx, "inventoryPools", new BsonDocument("location.organizationId", requested.OrganizationId.Value), ct);
        var result = new List<long>();
        foreach (var pool in pools) {
            InventoryGraph.RequirePool(pool);
            var proof = await SourceOwner.ReadProcurementCoverageTopologyAsync(tx, pool["_id"].AsInt64, ct);
            if (proof.Locations.Contains(requested)) result.Add(pool["_id"].AsInt64);
        }
        return result.Order().ToArray();
    }
    private async Task<long[]> MembershipAsync(IClientSessionHandle tx, string kind, long id, CancellationToken ct) {
        var rows = await BoundedAsync(tx, Transitions, new BsonDocument { { "sourceKind", kind }, { "sourceId", id } }, ct);
        if (rows.Select(row => row["poolId"].AsInt64).Distinct().Count() != rows.Length) throw Unavailable();
        foreach (var row in rows)
            if (row["transitionHash"] != Hash(row) || !await Rows(Transitions).Find(tx, new BsonDocument {
                { "_id", row["_id"] }, { "$and", new BsonArray { ProcurementStockSourceCoverageSchemas.Validator(Transitions) } } }).AnyAsync(ct)) throw Unavailable();
        return rows.Select(row => row["poolId"].AsInt64).Order().ToArray();
    }
    private async Task<long[]> IntendedAsync(IClientSessionHandle tx, string kind, BsonDocument row, long[] topology, CancellationToken ct) {
        if (kind == "request") return topology;
        string parent; long parentId;
        switch (kind) {
            case "requestDecision": parent = "request"; parentId = row["requestId"].AsInt64; break;
            case "proposal": {
                var selection = await Rows(PurchaseOrderInboundBindingBson.Selections).Find(tx, new BsonDocument("proposalId", row["_id"])).SingleOrDefaultAsync(ct);
                if (selection is null) {
                    if (topology.Length != 0) throw Unavailable();
                    return [];
                }
                var members = PurchaseOrderInboundBindingBson.Selection(PurchaseOrderBson.Input(row["input"]), selection["allocations"])
                    .Allocations.Select(value => value.PoolId).Distinct().Order().ToArray();
                if (members.Length == 0 || members.Except(topology).Any()) throw Unavailable();
                return members;
            }
            case "decision": parent = "proposal"; parentId = row["proposalId"].AsInt64; break;
            case "notice": parent = "decision"; parentId = row["orderApprovalId"].AsInt64; break;
            case "intake": parent = "notice"; parentId = row["noticeId"].AsInt64; break;
            case "inspectionDraft": case "inspection": parent = "intake"; parentId = row["intakeId"].AsInt64; break;
            case "claim": parent = "decision"; parentId = row["source"]["approval"]["_id"].AsInt64; break;
            case "claimDecision": parent = "claim"; parentId = row["claimId"].AsInt64; break;
            default: throw Unavailable();
        }
        var frozen = await MembershipAsync(tx, parent, parentId, ct);
        if (frozen.Length == 0 && topology.Length != 0 || frozen.Except(topology).Any()) throw Unavailable();
        return frozen;
    }
    internal async Task InitializeAsync(IClientSessionHandle tx, ProcurementStockSourceEvent setup, CancellationToken ct) {
        Context(tx); await RequireStorageAsync(database, ct);
        if (setup.Kind != "setup" || setup.PoolId <= 0 || setup.Time.ToUniversalTime() > clock.UtcNow.UtcDateTime ||
            await Rows(Heads).Find(tx, new BsonDocument("_id", setup.PoolId)).AnyAsync(ct) ||
            await Rows(Transitions).Find(tx, new BsonDocument("poolId", setup.PoolId)).AnyAsync(ct)) throw Unavailable();
        var row = new BsonDocument { { "_id", (await ids.NextAsync(Transitions, ct)).Value }, { "schemaVersion", 1 },
            { "poolId", setup.PoolId }, { "revision", 1L }, { "previousId", BsonNull.Value }, { "previousHash", BsonNull.Value },
            { "sourceKind", "setup" }, { "sourceId", setup.Id }, { "sourceHash", setup.Hash }, { "auditId", setup.Audit["_id"] },
            { "location", setup.Record["proposal"]["input"]["location"] }, { "recordedAt", setup.Time },
            { "configuration", BsonNull.Value }, { "suspensionRevision", 0L }, { "poolRevision", 1L }, { "beforePoolFence", 0L }, { "afterPoolFence", 0L } };
        row.Add("transitionHash", Hash(row));
        await Rows(Transitions).InsertOneAsync(tx, row, cancellationToken: ct);
        await Rows(Heads).InsertOneAsync(tx, new BsonDocument { { "_id", setup.PoolId }, { "schemaVersion", 1 },
            { "setupDecisionId", setup.Id }, { "location", row["location"] }, { "revision", 1L }, { "lastTransitionId", row["_id"] },
            { "lastHash", row["transitionHash"] }, { "updatedAt", setup.Time }, { "fence", 0L } }, cancellationToken: ct);
    }
    /// <summary>Called only for a fresh command, after its genuine command receipt and final owner fence.
    /// It adds no stock/money effect and never assigns an old row to today's alias.</summary>
    internal async Task RecordAsync(IClientSessionHandle tx, string kind, BsonDocument source, CancellationToken ct) {
        Context(tx); await RequireStorageAsync(database, ct);
        var actual = await AuthenticateAsync(tx, kind, source["_id"].AsInt64, ct);
        if (actual != source || source["recordedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime) throw Unavailable();
        var topology = await TopologyAsync(tx, source["location"].AsBsonDocument, ct);
        var intended = await IntendedAsync(tx, kind, source, topology, ct);
        foreach (var poolId in intended) {
            var head = await HeadAsync(tx, poolId, ct);
            var topologyProof = await SourceOwner.ReadProcurementCoverageTopologyAsync(tx, poolId, ct);
            var before = topologyProof.Pool; InventoryGraph.RequirePool(before);
            if (head["revision"].AsInt64 >= MaximumRows || head["fence"].AsInt64 >= long.MaxValue - 2 ||
                before["fence"].AsInt64 >= long.MaxValue - 2 || source["recordedAt"].CompareTo(head["updatedAt"]) < 0 ||
                await Rows(Transitions).Find(tx, new BsonDocument { { "poolId", poolId }, { "sourceKind", kind }, { "sourceId", source["_id"] } }).AnyAsync(ct)) throw Unavailable();
            var row = new BsonDocument { { "_id", (await ids.NextAsync(Transitions, ct)).Value }, { "schemaVersion", 1 },
                { "poolId", poolId }, { "revision", checked(head["revision"].AsInt64 + 1) }, { "previousId", head["lastTransitionId"] }, { "previousHash", head["lastHash"] },
                { "sourceKind", kind }, { "sourceId", source["_id"] }, { "sourceHash", SourceHash(kind, source) }, { "auditId", source["auditId"] },
                { "location", source["location"] }, { "recordedAt", source["recordedAt"] }, { "configuration", topologyProof.Configuration },
                { "suspensionRevision", topologyProof.SuspensionRevision }, { "poolRevision", before["revision"] },
                { "beforePoolFence", before["fence"] }, { "afterPoolFence", checked(before["fence"].AsInt64 + 1) } };
            row.Add("transitionHash", Hash(row));
            await SourceOwner.RequireProcurementCoverageTopologyAsync(tx, row, ct);
            if ((await Rows("inventoryPools").UpdateOneAsync(tx, before, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1)
                throw ProcurementStockSourceRecords.Changed();
            await Rows(Transitions).InsertOneAsync(tx, row, cancellationToken: ct);
            if ((await Rows(Heads).UpdateOneAsync(tx, head, Builders<BsonDocument>.Update.Inc("revision", 1L).Inc("fence", 1L)
                .Set("lastTransitionId", row["_id"]).Set("lastHash", row["transitionHash"]).Set("updatedAt", row["recordedAt"]), cancellationToken: ct)).ModifiedCount != 1)
                throw ProcurementStockSourceRecords.Changed();
        }
        Context(tx); ct.ThrowIfCancellationRequested();
    }
    internal async Task<ProcurementCoverageSnapshot> ReadAsync(IClientSessionHandle tx, StockSourceObligationExpectation expected,
        StockLocation[] locations, CancellationToken ct) {
        Context(tx); await RequireStorageAsync(database, ct);
        await new StockSourceTopologyFence(database).FenceAsync(tx, expected.Location.OrganizationId.Value, ct);
        var head = await HeadAsync(tx, expected.PoolId, ct);
        if (head["location"] != StockSourceSetupBson.Location(expected.Location)) throw Unavailable();
        var history = await Rows(Transitions).Find(tx, new BsonDocument("poolId", expected.PoolId)).Sort(new BsonDocument("revision", 1)).Limit(MaximumRows + 1).ToListAsync(ct);
        if (history.Count is < 1 or > MaximumRows || head["revision"].AsInt64 != history.Count) throw Unavailable();
        BsonDocument? previous = null; var covered = new HashSet<(string Kind, long Id)>();
        foreach (var row in history) {
            if (!await Rows(Transitions).Find(tx, new BsonDocument { { "_id", row["_id"] }, { "$and", new BsonArray { ProcurementStockSourceCoverageSchemas.Validator(Transitions) } } }).AnyAsync(ct) ||
                row["revision"].AsInt64 != (previous is null ? 1L : checked(previous["revision"].AsInt64 + 1)) ||
                row["previousId"] != (previous is null ? BsonNull.Value : previous["_id"]) || row["previousHash"] != (previous is null ? BsonNull.Value : previous["transitionHash"]) ||
                row["transitionHash"] != Hash(row) || previous is not null && row["recordedAt"].CompareTo(previous["recordedAt"]) < 0) throw Unavailable();
            var kind = row["sourceKind"].AsString; var id = row["sourceId"].AsInt64;
            if ((previous is null) != (kind == "setup")) throw Unavailable();
            if (kind == "setup") {
                var setup = await new OrderStockSourceRecords(database).SetupAsync(tx, id, true, ct);
                var setupHash = CatalogRepository.Digest(new BsonDocument { { "kind", "setup" }, { "record", setup.Record } });
                if (setup.PoolIds.Length != 1 || setup.PoolIds[0] != expected.PoolId || row["sourceHash"] != setupHash || row["auditId"] != setup.Audit["_id"] ||
                    row["sourceId"] != head["setupDecisionId"] || row["location"] != head["location"] || row["recordedAt"] != setup.Time || !row["configuration"].IsBsonNull ||
                    row["suspensionRevision"] != 0L || row["poolRevision"] != 1L || row["beforePoolFence"] != 0L || row["afterPoolFence"] != 0L) throw Unavailable();
            } else {
                var source = await AuthenticateAsync(tx, kind, id, ct);
                if (!covered.Add((kind, id)) || row["sourceHash"] != SourceHash(kind, source) || row["auditId"] != source["auditId"] ||
                    row["location"] != source["location"] || row["recordedAt"] != source["recordedAt"] ||
                    row["afterPoolFence"].AsInt64 != checked(row["beforePoolFence"].AsInt64 + 1) || row["poolRevision"].AsInt64 < previous!["poolRevision"].AsInt64 ||
                    row["beforePoolFence"].AsInt64 < previous["afterPoolFence"].AsInt64) throw Unavailable();
                await SourceOwner.RequireProcurementCoverageTopologyAsync(tx, row, ct);
            }
            previous = row;
        }
        if (previous is null || head["lastTransitionId"] != previous["_id"] || head["lastHash"] != previous["transitionHash"] ||
            head["updatedAt"] != previous["recordedAt"] || expected.PoolRevision < previous["poolRevision"].AsInt64 || expected.PoolFence < previous["afterPoolFence"].AsInt64) throw Unavailable();
        var actualMembers = new HashSet<(string Kind, long Id)>(); long open = 0;
        // All current and disabled historical locations must be searched. Empty new owners
        // cannot hide a pre-existing unbound order, arrival, inspection or supplier action.
        var allSources = await ReadSourcesAsync(tx, null, ct);
        await RequireEffectsMembershipAsync(tx, allSources, ct);
        await new ProcurementStockSourceEvents(database, ids, clock).PreflightAsync(tx, ct);
        foreach (var (kind, source) in allSources.Where(value => locations.Contains(InventoryGraph.Location(value.Row["location"].AsBsonDocument)))) {
            var id = source["_id"].AsInt64;
            var memberships = await MembershipAsync(tx, kind, id, ct);
            if (memberships.Length == 0) throw Unavailable();
            if (kind != "request" && !(await IntendedAsync(tx, kind, source, memberships, ct)).SequenceEqual(memberships)) throw Unavailable();
            var unresolved = await OpenAsync(tx, kind, source, ct);
            // A new pool/alias cannot absorb an earlier open demand implicitly. Without
            // an actual handoff it is still relevant unassigned future inbound work.
            if (kind == "request" && unresolved > 0 && !memberships.Contains(expected.PoolId)) throw Unavailable();
            if (memberships.Contains(expected.PoolId)) {
                actualMembers.Add((kind, id)); open = checked(open + unresolved);
            }
        }
        if (!covered.SetEquals(actualMembers)) throw Unavailable();
        Context(tx); return new(head, open);
    }
    internal async Task<(string Kind, BsonDocument Row)[]> ReadSourcesAsync(IClientSessionHandle tx, StockLocation[]? locations, CancellationToken ct) {
        var result = new List<(string, BsonDocument)>();
        foreach (var (kind, table) in Sources) {
            var all = await BoundedAsync(tx, table, new BsonDocument(), ct);
            foreach (var row in all) {
                if (!row.TryGetValue("_id", out var id) || !id.IsInt64 || id.AsInt64 <= 0 ||
                    !await Rows(table).Find(tx, new BsonDocument { { "_id", id }, { "$and", new BsonArray { SourceValidator(kind) } } }).AnyAsync(ct)) throw Unavailable();
                if (locations is not null && !locations.Contains(InventoryGraph.Location(row["location"].AsBsonDocument))) continue;
                result.Add((kind, await AuthenticateAsync(tx, kind, row["_id"].AsInt64, ct)));
            }
        }
        var proved = result.ToArray();
        if (locations is null) await RequireDerivedMembershipAsync(tx, proved, ct);
        return proved;
    }
    private async Task RequireDerivedMembershipAsync(IClientSessionHandle tx, (string Kind, BsonDocument Row)[] sources, CancellationToken ct) {
        // Reverse detection matters when no ordinary source remains to invoke its owner:
        // an orphan raw PO/custody/inspection head must not look like an empty pool.
        var approved = sources.Where(source => source.Kind == "decision" && source.Row["decision"] == "approve").ToArray();
        var orders = await BoundedAsync(tx, PurchaseOrderBson.Orders, new BsonDocument(), ct);
        if (orders.Length != approved.Length || !orders.Select(row => row["_id"].AsInt64).ToHashSet()
            .SetEquals(approved.Select(source => source.Row["orderId"].AsInt64))) throw Unavailable();
        var notices = sources.Where(source => source.Kind == "notice").Select(source => source.Row).ToArray();
        var receivingHeads = await BoundedAsync(tx, ProcurementReceivingBson.Heads, new BsonDocument(), ct);
        if (receivingHeads.Length != notices.Length || !receivingHeads.Select(row => row["_id"].AsInt64).ToHashSet()
            .SetEquals(notices.Select(row => row["_id"].AsInt64))) throw Unavailable();
        var intakes = sources.Where(source => source.Kind == "inspectionDraft").Select(source => source.Row["intakeId"].AsInt64).ToHashSet();
        var inspectionHeads = await BoundedAsync(tx, ProcurementInspectionBson.Heads, new BsonDocument(), ct);
        if (inspectionHeads.Length != intakes.Count || !inspectionHeads.Select(row => row["_id"].AsInt64).ToHashSet().SetEquals(intakes)) throw Unavailable();
        foreach (var (kind, table) in new[] { ("proposal", PurchaseOrderInboundBindingBson.Selections), ("decision", PurchaseOrderInboundBindingBson.Decisions) }) {
            var rows = await BoundedAsync(tx, table, new BsonDocument(), ct);
            var parents = sources.Where(source => source.Kind == kind).Select(source => source.Row).ToArray();
            foreach (var row in rows)
                if (!row.TryGetValue("_id", out var id) || !id.IsInt64 || parents.Count(parent => parent["_id"] == id && parent["proposalId"] == row["proposalId"]) != 1)
                    throw Unavailable();
        }
    }
    internal async Task RequireEffectsMembershipAsync(IClientSessionHandle tx, (string Kind, BsonDocument Row)[] sources, CancellationToken ct) {
        BsonDocument Filter(string field) => new(field, new BsonDocument("$in", new BsonArray {
            ProcurementRequestBson.Prefix + "submit", ProcurementRequestBson.Prefix + "approve", ProcurementRequestBson.Prefix + "reject",
            PurchaseOrderBson.Prefix + "propose", PurchaseOrderBson.Prefix + "approve", PurchaseOrderBson.Prefix + "reject",
            ProcurementReceivingBson.NoticeOperation, ProcurementReceivingBson.IntakeOperation,
            ProcurementInspectionBson.DraftOperation, ProcurementInspectionBson.SubmitOperation,
            SupplierClaimBson.Prefix + "request", SupplierClaimBson.Prefix + "approve", SupplierClaimBson.Prefix + "reject" }));
        var audits = await BoundedAsync(tx, "auditEvents", Filter("kind"), ct);
        var effects = await BoundedAsync(tx, "outboxEvents", Filter("kind"), ct);
        var commands = await BoundedAsync(tx, "idempotencyRecords", Filter("operation"), ct);
        if (audits.Length != sources.Length || effects.Length != sources.Length || commands.Length != sources.Length ||
            !audits.Select(row => row["_id"].AsInt64).ToHashSet().SetEquals(sources.Select(source => source.Row["auditId"].AsInt64)) ||
            !commands.Select(row => (row["actorId"].AsInt64, row["operation"].AsString, row["key"].AsString)).ToHashSet()
                .SetEquals(sources.Select(source => (source.Row["actorId"].AsInt64, source.Row["operation"].AsString, source.Row["requestKey"].AsString)))) throw Unavailable();
        string Key((string Kind, BsonDocument Row) source) => source.Kind switch {
            "request" or "requestDecision" => ProcurementRequestBson.SourceKey(source.Row),
            "proposal" or "decision" => PurchaseOrderBson.SourceKey(source.Row),
            "notice" or "intake" => ProcurementReceivingBson.EffectKey(source.Row),
            "inspectionDraft" or "inspection" => ProcurementInspectionBson.EffectKey(source.Row),
            "claim" or "claimDecision" => SupplierClaimBson.EffectKey(source.Row), _ => throw Unavailable() };
        if (!effects.Select(row => row["sourceKey"].AsString).ToHashSet().SetEquals(sources.Select(Key))) throw Unavailable();
    }
    private async Task<BsonDocument> AuthenticateAsync(IClientSessionHandle tx, string kind, long id, CancellationToken ct) {
        Context(tx); var client = database.Client as MongoClient ?? throw Unavailable(); var unit = new MongoUnitOfWork(client);
        var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var requests = new ProcurementRequestService(database, unit, ids, clock, catalog);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog, requests, new SupplierService(database, unit, ids, clock, catalog));
        var requestOwner = new ProcurementRequestRecords(database, catalog); var orderOwner = new PurchaseOrderRecords(database, catalog, clock);
        var receiving = new ProcurementReceivingRecords(database, clock, catalog, orders);
        var inspections = new ProcurementInspectionRecords(database, unit, clock, catalog, orders);
        var claims = new SupplierClaimRecords(database, unit, clock, catalog, orders);
        var table = Sources.Single(value => value.Kind == kind).Table;
        var raw = await Rows(table).Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct) ?? throw Unavailable();
        switch (kind) {
            case "request": { var row = await requestOwner.RequestAsync(tx, id, ct); _ = await requestOwner.DecisionForAsync(tx, row, ct); return row; }
            case "requestDecision": return await requestOwner.DecisionAsync(tx, id, await requestOwner.RequestAsync(tx, raw["requestId"].AsInt64, ct), ct);
            case "proposal": { var row = await orderOwner.ProposalAsync(tx, id, ct); _ = await orderOwner.DecisionForAsync(tx, row, ct); return row; }
            case "decision": {
                var row = await orderOwner.DecisionAsync(tx, id, await orderOwner.ProposalAsync(tx, raw["proposalId"].AsInt64, ct), ct);
                if (row["decision"] == "approve") _ = await orders.FundingSourceAsync(database, tx, row["orderId"].AsInt64, row["orderVersion"].AsInt64, id, ct);
                return row;
            }
            case "notice": { var row = await receiving.NoticeAsync(tx, id, ct); _ = await receiving.HistoryAsync(tx, row, ct); return row; }
            case "intake": { var notice = await receiving.NoticeAsync(tx, raw["noticeId"].AsInt64, ct); return (await receiving.HistoryAsync(tx, notice, ct)).Intakes.SingleOrDefault(row => row["_id"] == id) ?? throw Unavailable(); }
            case "inspectionDraft": case "inspection": {
                var history = await inspections.HistoryAsync(tx, raw["noticeId"].AsInt64, raw["intakeId"].AsInt64, ct);
                return (kind == "inspectionDraft" ? history.Drafts : history.Submissions).SingleOrDefault(row => row["_id"] == id) ?? throw Unavailable();
            }
            case "claim": { var row = await claims.RequestAsync(tx, id, ct); _ = await claims.DecisionAsync(tx, row, ct); await claims.OrphansAsync(tx, ct); return row; }
            case "claimDecision": {
                var row = await claims.DecisionAsync(tx, await claims.RequestAsync(tx, raw["claimId"].AsInt64, ct), ct);
                if (row is null || row["_id"] != id) throw Unavailable(); await claims.OrphansAsync(tx, ct); return row;
            }
            default: throw Unavailable();
        }
    }
    private async Task<long> OpenAsync(IClientSessionHandle tx, string kind, BsonDocument row, CancellationToken ct) {
        switch (kind) {
            case "request": {
                var decision = await Rows(ProcurementRequestBson.Decisions).Find(tx, new BsonDocument("requestId", row["_id"])).SingleOrDefaultAsync(ct);
                return decision is not null && decision["decision"] == "reject" ? 0 : 1;
            }
            case "proposal": {
                var decision = await Rows(PurchaseOrderBson.Decisions).Find(tx, new BsonDocument("proposalId", row["_id"])).SingleOrDefaultAsync(ct);
                // Approved PO/demand has no durable terminal close owner yet. Receiving,
                // quality acceptance or a bank entry cannot silently extinguish it.
                return decision is not null && decision["decision"] == "reject" ? 0 : 1;
            }
            case "notice": return (await Rows(ProcurementReceivingBson.Heads).Find(tx, new BsonDocument("_id", row["_id"])).SingleAsync(ct))["matchedContainers"].AsInt32 < row["input"]["declaredContainers"].AsInt32 ? 1 : 0;
            case "intake": {
                var head = await Rows(ProcurementInspectionBson.Heads).Find(tx, new BsonDocument("_id", row["_id"])).SingleOrDefaultAsync(ct);
                return head is null || head["remainingContainers"].AsInt64 > 0 || row["input"]["heldContainers"].AsInt32 > 0 || row["input"]["excessContainers"].AsInt32 > 0 ? 1 : 0;
            }
            case "inspectionDraft": return row["submittedInspectionId"].IsBsonNull ? 1 : 0;
            case "inspection": return row["input"]["lines"].AsBsonArray.Any(line => line["receivedPackages"].AsInt32 > 0 || line["declaredPackages"].AsInt32 > line["receivedPackages"].AsInt32) ? 1 : 0;
            case "claim": {
                var decision = await Rows(SupplierClaimBson.Decisions).Find(tx, new BsonDocument("claimId", row["_id"])).SingleOrDefaultAsync(ct);
                return decision is not null && decision["decision"] == "reject" ? 0 : 1;
            }
            default: return 0;
        }
    }
    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct) {
        foreach (var name in ProcurementStockSourceCoverageSchemas.Collections) {
            var definition = await UnitToko.Platform.Identity.AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Unavailable();
            if (!UnitToko.Platform.Identity.AccountContactStorage.SafeDefinition(definition, ProcurementStockSourceCoverageSchemas.Validator(name))) throw Unavailable();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var required = ProcurementStockSourceCoverageSchemas.Keys(name);
            if (!OrderStockSourceStorage.SafeIndexes(indexes, required) || required.Any(key => !indexes.Any(row => UnitToko.Platform.Identity.AccountContactStorage.Matches(row, key)))) throw Unavailable();
        }
    }
}
