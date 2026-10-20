using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Inventory;

internal sealed class HeldReceiptPoolCapture(object owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
    BsonDocument source, BsonDocument pool, Action current, Func<CancellationToken, Task> fence)
{
    private readonly long transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
    private readonly string hash = StockSourceSuspensionBson.Hash(source);
    private readonly string poolHash = Identity(pool);
    private long expectedFence = pool["fence"].AsInt64;
    private static string Identity(BsonDocument value) { var copy = value.DeepClone().AsBsonDocument; copy.Remove("fence"); return StockSourceSuspensionBson.Hash(copy); }
    internal BsonDocument Source { get; } = source;
    internal BsonDocument Pool { get; } = pool;
    internal void RequireContext(object expectedOwner, ActorContext value, AuthorizationSnapshot snapshot, IClientSessionHandle session) {
        if (!ReferenceEquals(owner, expectedOwner) || actor != value || !ReferenceEquals(authority, snapshot) || !ReferenceEquals(tx, session)) throw StockSourcePolicyBson.Invalid(); RequireCurrent();
    }
    internal void RequireCurrent() {
        if (!tx.IsInTransaction || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction || StockSourceSuspensionBson.Hash(Source) != hash || Identity(Pool) != poolHash || Pool["fence"].AsInt64 != expectedFence) throw StockSourcePolicyBson.Invalid(); current();
    }
    internal async Task FenceAsync(CancellationToken ct) { RequireCurrent(); await fence(ct); expectedFence = checked(expectedFence + 1); RequireCurrent(); ct.ThrowIfCancellationRequested(); }
}

public sealed partial class StockSourceSetupService
{
    internal Task RequireHeldReceiptStorageAsync(CancellationToken ct) => OperatingPolicyStorageAsync(ct);
    // One stock decision asks for the same pool's configuration for every held receipt and lot (see RememberedProofAsync).
    private Task<SuspensionContext> HeldConfigurationAsync(IClientSessionHandle tx, long poolId, CancellationToken ct) =>
        RememberedProofAsync(tx, "held-configuration:" + poolId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reader => reader.HeldConfigurationCoreAsync(tx, poolId, ct), SuspensionContext.Copy);
    private async Task<SuspensionContext> HeldConfigurationCoreAsync(IClientSessionHandle tx, long poolId, CancellationToken ct) {
        var current = await CurrentSourceAsync(tx, poolId, ct); var owner = InventoryGraph.Location(current["pool"]["location"].AsBsonDocument);
        var locations = new HashSet<StockLocation>();
        foreach (var table in new[] { "stockSourceAliases", "stockSourceAliasDecisions" }) {
            var filter = F.Eq("poolId", poolId); if (table.EndsWith("Decisions", StringComparison.Ordinal)) filter &= F.Eq("decision", "approve");
            var query = Rows(table).Find(tx, filter);
            var rows = await (historyCapture is null ? query.Project<BsonDocument>(new BsonDocument("requestedLocation", 1)) : query).ToListAsync(ct);
            HistoryRows(table, filter, rows);
            foreach (var row in rows) locations.Add(InventoryGraph.Location(row["requestedLocation"].AsBsonDocument));
        }
        var aliases = new List<BsonDocument>(); var approvedAt = StockSourceSetupBson.Time(current["approvedAt"]);
        foreach (var location in locations.OrderBy(value => value.ChannelId.Value)) {
            StockSourceAliasPolicy.Validate(owner, location); var view = await LatestAliasAsync(tx, poolId, location, ct) ?? throw StockSourcePolicyBson.Invalid();
            RequireAliasPool(view, current); aliases.Add(view["alias"].AsBsonDocument); var at = StockSourceSetupBson.Time(view["decision"]["decidedAt"]); if (at > approvedAt) approvedAt = at;
        }
        var latest = await LatestSuspensionAsync(tx, poolId, current, ct);
        return new(current, StockSourceSuspensionBson.Configuration(current, aliases), latest, [owner, .. locations], [Resource(owner), .. locations.Select(Resource)], approvedAt);
    }
    private static BsonDocument ReviewedHeldConfiguration(BsonDocument configuration) { var value = configuration.DeepClone().AsBsonDocument; value["source"]["active"] = false; return value; }
    private static void HeldPolicy(BsonDocument source) {
        var config = source["configuration"].AsBsonDocument; var view = source["policyView"]; var proposal = view["proposal"].AsBsonDocument; var policy = view["policy"].AsBsonDocument;
        if (!proposal["configuration"].Equals(ReviewedHeldConfiguration(config)) || proposal["suspensionRevision"] != source["suspensionRevision"] ||
            policy["poolId"] != source["poolId"] || proposal["poolRevision"] != config["source"]["poolRevision"] || proposal["input"]["writer"] != "unitToko") throw IneligibleHeldPool();
        var input = StockSourceSetupBson.ReadInput(config["source"]["input"].AsBsonDocument); var requested = InventoryGraph.Location(source["requestedLocation"].AsBsonDocument);
        if (input.Mode != StockAuthorityMode.WebLedger) throw IneligibleHeldPool();
        if (input.Location != requested && !config["mappings"].AsBsonArray.Any(value => value["requestedLocation"].Equals(source["requestedLocation"]) && value["enabled"] == true && value["poolRevision"] == config["source"]["poolRevision"]))
            throw IneligibleHeldPool();
    }
    private static UnitToko.Platform.Mongo.PersistenceConflictException IneligibleHeldPool() => new("procurement_stock_receipt_pool_ineligible", "The exact reviewed source configuration does not authorize Unit Toko held receipts");
    internal async Task<HeldReceiptPoolCapture> CaptureHeldReceiptAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        StockLocation requested, long poolId, long? expectedRevision, string permission, CancellationToken ct) {
        if (permission is not (Permissions.StockReceive or Permissions.StockRead) || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw StockSourcePolicyBson.Invalid();
        Require(actor, authority, permission, Resource(requested));
        await new StockSourceTopologyFence(database).FenceAsync(tx, requested.OrganizationId.Value, ct);
        var scope = await Rows("inventoryPools").Find(tx, F.Eq("_id", poolId)).Project(new BsonDocument("location", 1)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        var owner = InventoryGraph.Location(scope["location"].AsBsonDocument); Require(actor, authority, permission, Resource(owner));
        var context = await HeldConfigurationAsync(tx, poolId, ct); var history = await OperatingPolicyHistoryAsync(tx, context, ct); var selected = history.Latest ?? throw IneligibleHeldPool();
        var pool = context.Pool.DeepClone().AsBsonDocument;
        if (expectedRevision is { } revision && pool["revision"] != revision) throw RevisionChanged();
        var source = new BsonDocument { { "poolId", poolId }, { "requestedLocation", StockSourceSetupBson.Location(requested) }, { "configuration", context.Configuration.DeepClone() },
            { "policyView", selected.DeepClone() }, { "suspensionRevision", context.Latest?["suspensionRevision"] ?? 0L }, { "poolFence", pool["fence"] } }; HeldPolicy(source);
        void Current() { Require(actor, authority, permission, Resource(requested)); Require(actor, authority, permission, Resource(owner)); }
        async Task Fence(CancellationToken token) {
            Current(); await FenceRevisionPoolAsync(tx, pool, false, token); pool["fence"] = checked(pool["fence"].AsInt64 + 1);
            // A no-op update cannot protect a stale snapshot. An actual transactional write
            // detects deletion/replacement while immediate reinsertion preserves committed proof bytes.
            foreach (var (table, field) in new[] { (StockSourcePolicyBson.Proposals, "proposal"), (StockSourcePolicyBson.Decisions, "decision"), (StockSourcePolicyBson.Policies, "policy") }) {
                var row = source["policyView"][field].DeepClone().AsBsonDocument;
                if ((await Rows(table).DeleteOneAsync(tx, row, cancellationToken: token)).DeletedCount != 1) throw StockSourcePolicyBson.Invalid();
                await Rows(table).InsertOneAsync(tx, row, cancellationToken: token);
            }
            await CompleteAuthorityAsync(actor, authority, permission, new[] { Resource(requested), Resource(owner) }.Distinct().ToArray(), tx, token); Current();
        }
        Current(); return new(this, actor, authority, tx, source, pool, Current, Fence);
    }
    internal async Task RequireHeldReceiptHistoryAsync(IClientSessionHandle tx, BsonDocument source, DateTimeOffset recordedAt, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || source.ElementCount != 6 || source["poolFence"].AsInt64 < 0 || recordedAt > Now()) throw StockSourcePolicyBson.Invalid();
        var context = await HeldConfigurationAsync(tx, source["poolId"].AsInt64, ct); var history = await OperatingPolicyHistoryAsync(tx, context, ct);
        var stored = source["policyView"]; var view = history.Views.GetValueOrDefault(stored["proposal"]["_id"].AsInt64);
        if (view is null || !view.Equals(stored) || view["policy"].IsBsonNull || StockSourceSetupBson.Time(view["decision"]["decidedAt"]) > recordedAt) throw StockSourcePolicyBson.Invalid();
        HeldPolicy(source); await RequireOperatingPolicyBindingAtAsync(tx, view["proposal"].AsBsonDocument, recordedAt, ct);
        if (history.Views.Values.Any(value => !value["policy"].IsBsonNull && value["policy"]["policyRevision"].AsInt64 > view["policy"]["policyRevision"].AsInt64 && StockSourceSetupBson.Time(value["decision"]["decidedAt"]) < recordedAt)) throw StockSourcePolicyBson.Invalid();
    }
    internal static BsonDocument HeldReceiptPoolView(BsonDocument source) {
        var config = source["configuration"]; var input = config["source"]["input"]; var view = source["policyView"]; var policy = view["policy"]; var terms = view["proposal"]["input"];
        return new() { { "id", source["poolId"] }, { "revision", config["source"]["poolRevision"] }, { "active", config["source"]["active"] }, { "location", input["location"] },
            { "requestedLocation", source["requestedLocation"] }, { "physicalKey", input["physicalKey"] }, { "label", input["label"] }, { "mode", input["mode"] }, { "control", input["control"] },
            { "configurationHash", StockSourceSuspensionBson.Hash(config.AsBsonDocument) }, { "policyId", policy["_id"] }, { "policyRevision", policy["policyRevision"] },
            { "policyHash", StockSourceSuspensionBson.Hash(view.AsBsonDocument) }, { "writer", "unit_toko" }, { "reconciliationProcedureReference", terms["reconciliationProcedureReference"] }, { "outageProcedureReference", terms["outageProcedureReference"] } };
    }
}
