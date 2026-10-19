using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Integrity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using S = UnitToko.Platform.Ordering.OrderSubstitutionBson;

namespace UnitToko.Platform.Ordering;

/// <summary>Ordering-owned order-progress checkpoint seals. A seal binds the order row, the exact history prefix up to
/// decision revision R and dispatch sequence D (decision rows, substitution decisions, shipments), every immutable row
/// the full replay checks for that prefix (proposals, preparation versions, suborders, audit events, receipts, outbox
/// events, movements and dispatch value transitions) and the derived replay state at that checkpoint as MAC-covered
/// facts. Later reads verify all of it exactly and replay only newer rows; reservation state is never trusted from the
/// seal (it is re-derived and compared to live rows).</summary>
internal static class OrderProgressSeals
{
    private const string Kind = OrderProgressSealSchemas.Kind;
    // Replays run (tests assert that a verified checkpoint replaces the full replay).
    internal static long FullReplays, IncrementalReplays;
    private static IMongoCollection<BsonDocument> Seals(IMongoDatabase database) => database.GetCollection<BsonDocument>(OrderProgressSealSchemas.Collection);
    private delegate IReadOnlyList<BsonDocument> RowsOf(string collection);

    internal static (string Collection, BsonDocument Filter)[] Scopes(long orderId, long revision, long dispatchSequence) => [
        ("orderDecisions", new BsonDocument { { "orderId", orderId }, { "revision", new BsonDocument("$lte", revision) } }),
        (S.Decisions, new BsonDocument { { "orderId", orderId }, { "revision", new BsonDocument("$lte", revision) } }),
        (ShipmentDispatchBson.Collection, new BsonDocument { { "orderId", orderId }, { "orderSequence", new BsonDocument("$lte", dispatchSequence) } })];

    // Rows the prefix names directly; their own evidence is bound by Evidence below.
    private static IEnumerable<(string Collection, BsonDocument Filter)> Sources(RowsOf rows) {
        foreach (var (collection, ids) in new[] {
            (S.Proposals, rows(S.Decisions).Select(row => row["proposalId"])),
            (ShipmentPreparationBson.Versions, rows(ShipmentDispatchBson.Collection).Select(row => row["preparationVersionId"])) })
            if (In(ids) is { } filter) yield return (collection, new BsonDocument("_id", filter));
    }

    // Immutable evidence each full-replay check reads for a sealed row (OrderDecisions, OrderReservationReleases,
    // OrderSubstitutionHistory/Records, OrderDispatchHistory, ShipmentDispatchRecords, ShipmentPreparationService).
    private static IEnumerable<(string Collection, BsonDocument Filter)> Evidence(RowsOf rows) {
        var decisions = rows("orderDecisions"); var substitutions = rows(S.Decisions); var proposals = rows(S.Proposals);
        var shipments = rows(ShipmentDispatchBson.Collection); var versions = rows(ShipmentPreparationBson.Versions);
        var receipts = new List<BsonDocument>(); var outbox = new List<BsonValue>(); var movements = new List<BsonValue>();
        BsonDocument Receipt(BsonValue actor, string operation, BsonValue key) => new() { { "actorId", actor }, { "operation", operation }, { "key", key } };
        void Moves(string source, BsonValue id, string kind, IEnumerable<BsonValue> pools) =>
            movements.AddRange(pools.Distinct().Select(pool => (BsonValue)new InventorySource(source, id.AsInt64, kind, pool.AsInt64).EventKey));
        foreach (var row in decisions) {
            receipts.Add(Receipt(row["decidedBy"], OrderSubmissionService.DecisionOperation(Enum.Parse<OrderReviewStage>(row["stage"].AsString),
                Enum.Parse<OrderReviewAction>(row["action"].AsString)), row["requestKey"]));
            Moves("ordering.decision", row["_id"], "release", row["releases"].AsBsonArray.Select(release => release["allocation"]["poolId"]));
        }
        foreach (var row in substitutions) {
            var operation = S.Operation(Enum.Parse<OrderSubstitutionDecision>(row["decision"].AsString));
            receipts.Add(Receipt(row["actorId"], operation, row["requestKey"])); outbox.Add(operation + ":" + row["_id"].AsInt64);
            Moves("ordering.substitution", row["_id"], "release", row["releases"].AsBsonArray.Select(release => release["allocation"]["poolId"]));
            Moves("ordering.substitution", row["_id"], "reserve", row["allocations"].AsBsonArray.Select(allocation => allocation["poolId"]));
        }
        foreach (var row in proposals) { receipts.Add(Receipt(row["actorId"], S.ProposeOperation, row["requestKey"])); outbox.Add(S.ProposeOperation + ":" + row["_id"].AsInt64); }
        foreach (var row in versions) receipts.Add(Receipt(row["preparedBy"], ShipmentPreparationBson.Operation, row["requestKey"]));
        var transitions = new List<BsonValue>();
        foreach (var row in shipments) {
            receipts.Add(Receipt(row["dispatchedBy"], ShipmentDispatchBson.Operation, row["requestKey"]));
            Moves("fulfillment.shipment", row["_id"], "dispatch", row["stockEffects"].AsBsonArray.Select(effect => effect["allocation"]["poolId"]));
            if (row["schemaVersion"] == 2) {
                outbox.Add("fulfillment.shipment.dispatched:" + row["_id"].AsInt64);
                transitions.AddRange(ShipmentDispatchSeals.Scopes(row).Select(scope => (BsonValue)scope.Filter));
            }
        }
        if (In(decisions.Concat(substitutions).Concat(proposals).Concat(shipments).Concat(versions).Select(row => row["auditId"])) is { } audits)
            yield return ("auditEvents", new BsonDocument("_id", audits));
        if (receipts.Count > 0) yield return ("idempotencyRecords", new BsonDocument("$or", Distinct(receipts)));
        if (In(outbox) is { } events) yield return ("outboxEvents", new BsonDocument("sourceKey", events));
        if (In(movements) is { } moved) yield return ("movements", new BsonDocument("sourceEvent", moved));
        if (In(substitutions.Concat(shipments).Select(row => row["suborderId"])) is { } suborders)
            yield return (OrderSubmissionBson.Suborders, new BsonDocument("_id", suborders));
        if (transitions.Count > 0) yield return (LotValueStore.Transitions, new BsonDocument("$or", Distinct(transitions)));
    }
    private static BsonArray Distinct(IEnumerable<BsonValue> values) =>
        new(values.DistinctBy(value => value.ToJson()).OrderBy(value => value.ToJson(), StringComparer.Ordinal));
    private static BsonDocument? In(IEnumerable<BsonValue> values) {
        var distinct = values.Distinct().Order().ToArray();
        return distinct.Length == 0 ? null : new BsonDocument("$in", new BsonArray(distinct));
    }
    private static string[] Shape(IEnumerable<(string Collection, BsonDocument Filter)> scopes) =>
        [.. scopes.Select(scope => scope.Collection + "\n" + scope.Filter.ToJson()).Order(StringComparer.Ordinal)];

    /// <summary>Verified checkpoint facts, or null when no key ring, no seal, or facts of another version exist (the
    /// caller then runs the full replay). A present seal whose MAC, order row, membership or shape fails refuses.</summary>
    internal static async Task<BsonDocument?> VerifyAsync(IMongoDatabase database, IClientSessionHandle tx, BsonDocument order, CancellationToken ct)
    {
        var ring = ProvenanceSealRing.Current;
        if (ring is null) return null;
        var seal = await Seals(database).Find(tx, new BsonDocument("_id", order["_id"])).SingleOrDefaultAsync(ct);
        if (seal is null) return null;
        var orderId = order["_id"].AsInt64;
        IReadOnlyList<BsonDocument> verified;
        try { verified = await new SealVerifier(database, ring).VerifyAsync(tx, seal, Kind, orderId, order, ct); }
        catch (SealUnavailableException) { throw Unavailable(); }
        var facts = seal["facts"].AsBsonDocument;
        if (facts["factsVersion"] != OrderProgressSealSchemas.FactsVersion) return null;
        // One row scope per collection; the expected scopes are recomputed from the verified rows themselves.
        var byCollection = verified.GroupBy(scope => scope["collection"].AsString).ToDictionary(group => group.Key, group => group.ToArray());
        var actual = Shape(verified.Select(scope => (scope["collection"].AsString, scope["filter"].AsBsonDocument)));
        IReadOnlyList<BsonDocument> Rows(string collection) => byCollection.TryGetValue(collection, out var scopes) && scopes.Length == 1 && scopes[0].Contains("rows")
            ? [.. scopes[0]["rows"].AsBsonArray.Select(row => row.AsBsonDocument)] : [];
        var prefix = Scopes(orderId, facts["revision"].AsInt64, facts["dispatchSequence"].AsInt64);
        if (seal["dependencies"].AsBsonArray.Count != 0 || verified.Any(scope => !scope.Contains("rows")) || byCollection.Values.Any(scopes => scopes.Length != 1) ||
            !actual.SequenceEqual(Shape([.. prefix, .. Sources(Rows), .. Evidence(Rows)]))) {
            SealIntegrityAlerts.Raise(Kind, orderId, "shape"); throw Unavailable();
        }
        return facts.DeepClone().AsBsonDocument;
    }

    /// <summary>Seals or advances the checkpoint after a successful replay. Only an owning operation writes.</summary>
    internal static async Task SealAsync(IMongoDatabase database, IClock clock, IClientSessionHandle tx, BsonDocument order,
        BsonDocument facts, CancellationToken ct)
    {
        var ring = ProvenanceSealRing.Current;
        if (ring is null || !MongoUnitOfWork.OwnsOperation(tx)) return;
        var orderId = order["_id"].AsInt64;
        var scopes = new List<(string Collection, BsonDocument Filter, IReadOnlyList<BsonDocument> Rows)>();
        IReadOnlyList<BsonDocument> Rows(string collection) => scopes.SingleOrDefault(scope => scope.Collection == collection).Rows ?? [];
        async Task<bool> ReadAsync(IEnumerable<(string Collection, BsonDocument Filter)> next) {
            foreach (var (collection, filter) in next.ToArray()) {
                var rows = await database.GetCollection<BsonDocument>(collection).Find(tx, filter).Sort(new BsonDocument("_id", 1))
                    .Limit(ProvenanceSeal.MaximumMembers + 1).ToListAsync(ct);
                if (rows.Count > ProvenanceSeal.MaximumMembers) return false; // Beyond the stored limits: the existing checkpoint (if any) stays.
                scopes.Add((collection, filter, rows));
            }
            return true;
        }
        if (!await ReadAsync(Scopes(orderId, facts["revision"].AsInt64, facts["dispatchSequence"].AsInt64)) ||
            !await ReadAsync(Sources(Rows)) || !await ReadAsync(Evidence(Rows))) return;
        var membership = SealVerifier.Membership(scopes);
        if (!ProvenanceSeal.Sealable(membership)) return;
        await Seals(database).ReplaceOneAsync(tx, new BsonDocument("_id", orderId), ProvenanceSeal.Create(ring, Kind, orderId,
            ProvenanceSeal.RowHash(order), membership, [], clock.UtcNow, facts), new ReplaceOptions { IsUpsert = true }, ct);
    }

    private static AuthorizationDeniedException Unavailable() => new("order_unavailable");
}
