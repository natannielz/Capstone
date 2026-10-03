using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Inventory.StockSourceReconciliationBson;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    internal async Task ReconciliationPreflightAsync(IClientSessionHandle tx, CancellationToken ct)
    {
        var proposals = await Rows(B.Proposals).Find(tx, new BsonDocument()).ToListAsync(ct);
        long decisions = 0;
        foreach (var raw in proposals)
        {
            var proposal = await ReconciliationProposalAsync(tx, raw["_id"].AsInt64, ct);
            if (await ReconciliationDecisionAsync(tx, proposal, ct) is not null) decisions++;
        }
        if (await Rows(B.Decisions).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct) != decisions ||
            await Rows("auditEvents").CountDocumentsAsync(tx, F.Eq("kind", "stock.source.reconciliation.audit"), cancellationToken: ct) != proposals.Count + decisions ||
            await Rows("outboxEvents").CountDocumentsAsync(tx, F.In("kind", new[] { B.ProposeOperation, B.DecideOperation }), cancellationToken: ct) != proposals.Count + decisions ||
            await Rows("idempotencyRecords").CountDocumentsAsync(tx, F.In("operation", new[] { B.ProposeOperation, B.DecideOperation }), cancellationToken: ct) != proposals.Count + decisions)
            throw B.Unavailable();
    }
    public async Task<AuthorizedPage> ReconciliationTargetsAsync(ActorContext actor, StockLocation location,
        int offset, int limit, CancellationToken ct = default)
    {
        location.Validate(); AuthorizationStore.ValidatePage(offset, limit); await ReconciliationStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            Require(actor, snapshot, Permissions.StockSourceReconciliationRead, Resource(location));
            var filter = F.Eq("location", StockSourceSetupBson.Location(location));
            var total = await Rows("inventoryPools").CountDocumentsAsync(tx, filter, cancellationToken: token);
            var selected = await Rows("inventoryPools").Find(tx, filter).Sort(new BsonDocument("_id", 1)).Skip(offset).Limit(limit).ToListAsync(token);
            var items = new List<BsonDocument>();
            var resources = new HashSet<ResourceScope> { Resource(location) };
            foreach (var pool in selected)
            {
                var context = await OperatingPolicyContextAsync(actor, snapshot, tx, pool["_id"].AsInt64, Permissions.StockSourceReconciliationRead, token);
                foreach (var resource in context.Source.Resources) resources.Add(resource);
                var source = StockSourceSetupBson.ReadInput(context.Source.Current["input"].AsBsonDocument);
                var locations = context.Source.Locations;
                var nodes = locations.SelectMany(value => ScopePolicy.RequiredNodes(Resource(value), snapshot.Directory)).Distinct().ToArray();
                var profiles = await Rows("organizationProfiles").Find(tx, F.In("_id", nodes.Select(node => node.Id))).ToListAsync(token);
                string Name(long id, ScopeNodeKind kind)
                {
                    var node = snapshot.Directory.Find(id, kind) ?? throw B.Unavailable();
                    var profile = profiles.SingleOrDefault(value => value.GetValue("_id", BsonNull.Value) == new BsonInt64(id)) ?? throw B.Unavailable();
                    return ProfileName(profile, node);
                }
                items.Add(new BsonDocument { { "poolId", context.Source.Pool["_id"] }, { "label", source.Label },
                    { "physicalKey", source.PhysicalKey }, { "ownerLocation", StockSourceSetupBson.Location(source.Location) },
                    { "organizationName", Name(location.OrganizationId.Value, ScopeNodeKind.FulfillmentOrganization) },
                    { "shopName", Name(location.ShopId.Value, ScopeNodeKind.Shop) },
                    { "warehouseName", Name(location.WarehouseId.Value, ScopeNodeKind.Warehouse) },
                    { "channelNames", new BsonArray(locations.Select(value => Name(value.ChannelId.Value, ScopeNodeKind.Channel))) } });
            }
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceReconciliationRead, resources.ToArray(), tx, token);
            return new AuthorizedPage(total, items);
        }, ct);
    }
    private async Task<BsonDocument> ReconciliationProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct)
    {
        var row = await Rows(B.Proposals).Find(tx, F.Eq("_id", id)).SingleOrDefaultAsync(ct) ?? throw B.Unavailable();
        if (row.GetValue("snapshotHash", BsonNull.Value) != B.Hash(new BsonDocument(row.Where(field => field.Name != "snapshotHash"))) ||
            row.GetValue("kind", "") != "stock.source.reconciliation.proposal" || row.GetValue("revision", 0L) != 1L ||
            row.GetValue("committedPoolFence", 0L) != row["baseline"]["poolFence"].AsInt64 + 1 ||
            row["baseline"]["baselineHash"].AsString != BaselineHash(row["baseline"].AsBsonDocument) ||
            row["baseline"]["binding"].AsString != Binding(row["baseline"].AsBsonDocument) ||
            row["binding"] != row["baseline"]["binding"] ||
            !row["differences"].Equals(Differences(row["baseline"].AsBsonDocument, row["observation"].AsBsonDocument))) throw B.Unavailable();
        await ReconciliationEffectsAsync(tx, row, B.ProposeOperation, ct);
        return row;
    }
    private async Task<BsonDocument?> ReconciliationDecisionAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct)
    {
        var row = await Rows(B.Decisions).Find(tx, F.Eq("proposalId", proposal["_id"])).SingleOrDefaultAsync(ct);
        if (row is null)
        {
            var id = proposal["_id"];
            if (await Rows("auditEvents").Find(tx, F.Eq("kind", "stock.source.reconciliation.audit") &
                F.Eq("operation", B.DecideOperation) & F.Eq("record.proposalId", id)).AnyAsync(ct) ||
                await Rows("outboxEvents").Find(tx, F.Eq("kind", B.DecideOperation) & F.Eq("payload.proposalId", id)).AnyAsync(ct) ||
                await Rows("idempotencyRecords").Find(tx, F.Eq("operation", B.DecideOperation) & F.Eq("result.proposalId", id)).AnyAsync(ct))
                throw B.Unavailable();
            return null;
        }
        if (row.GetValue("snapshotHash", BsonNull.Value) != B.Hash(new BsonDocument(row.Where(field => field.Name != "snapshotHash"))) ||
            row.GetValue("kind", "") != "stock.source.reconciliation.decision" || row["proposalHash"] != proposal["snapshotHash"] ||
            row["poolId"] != proposal["poolId"] || row["revision"] != 1L || row["actorId"] == proposal["actorId"] ||
            row["decision"].AsString is not ("confirm" or "reject") ||
            (row["decision"] == "confirm") != !row["observation"].IsBsonNull) throw B.Unavailable();
        if (row["decision"] == "confirm")
        {
            if (proposal["differences"].AsBsonArray.Count != 0 || row["baselineHash"] != proposal["baseline"]["baselineHash"] ||
                !row["observation"]["entries"].Equals(proposal["observation"]["entries"])) throw B.Unavailable();
        }
        else if (!row["baselineHash"].IsBsonNull) throw B.Unavailable();
        await ReconciliationEffectsAsync(tx, row, B.DecideOperation, ct);
        return row;
    }
    private async Task ReconciliationEffectsAsync(IClientSessionHandle tx, BsonDocument row, string operation, CancellationToken ct)
    {
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        if (audit is null || !audit.Equals(B.Audit(row, operation))) throw B.Unavailable();
        var outbox = await Rows("outboxEvents").Find(tx, F.Eq("sourceKey", operation + ":" + row["_id"].AsInt64)).SingleOrDefaultAsync(ct);
        if (outbox is null || outbox["kind"] != operation || outbox["poolId"] != row["poolId"] ||
            outbox["actorId"] != row["actorId"] || outbox["requestKey"] != row["requestKey"] ||
            outbox["payload"] != B.Result(row) || outbox["createdAt"] != row["recordedAt"]) throw B.Unavailable();
        var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", row["actorId"]) &
            F.Eq("operation", operation) & F.Eq("key", row["requestKey"])).SingleOrDefaultAsync(ct);
        if (receipt is null || receipt["bodyHash"] != row["bodyHash"] || receipt["result"] != B.Result(row) ||
            receipt["createdAt"].CompareTo(row["recordedAt"]) > 0) throw B.Unavailable();
    }
    private static BsonDocument ReconciliationProposalView(BsonDocument row) => new() {
        { "id", row["_id"] }, { "version", row["revision"] }, { "poolId", row["poolId"] },
        { "baseline", row["baseline"] }, { "observation", row["observation"] }, { "differences", row["differences"] },
        { "recordedBy", row["actorId"] }, { "recordedAt", row["recordedAt"] }, { "reason", row["reason"] }, { "status", "proposed" } };
    private static BsonDocument ReconciliationDecisionView(BsonDocument row) => new() {
        { "id", row["_id"] }, { "decision", row["decision"] }, { "observation", row["observation"] },
        { "recordedBy", row["actorId"] }, { "recordedAt", row["recordedAt"] }, { "reason", row["reason"] } };
    private static BsonDocument ReconciliationView(BsonDocument proposal, BsonDocument? decision) => new() {
        { "proposal", ReconciliationProposalView(proposal) }, { "decision", (BsonValue?)
            (decision is null ? null : ReconciliationDecisionView(decision)) ?? BsonNull.Value } };

    public async Task<BsonDocument?> ReadReconciliationAsync(ActorContext actor, long proposalId, CancellationToken ct = default)
    {
        if (proposalId <= 0) throw B.Unavailable(); await ReconciliationStorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (snapshot, tx, token) =>
        {
            var route = await Rows(B.Proposals).Find(tx, F.Eq("_id", proposalId)).Project(new BsonDocument("poolId", 1)).SingleOrDefaultAsync(token);
            if (route is null) return null;
            var context = await OperatingPolicyContextAsync(actor, snapshot, tx, route["poolId"].AsInt64, Permissions.StockSourceReconciliationRead, token);
            var proposal = await ReconciliationProposalAsync(tx, proposalId, token);
            var decision = await ReconciliationDecisionAsync(tx, proposal, token);
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceReconciliationRead, context.Source.Resources, tx, token);
            return ReconciliationView(proposal, decision);
        }, ct);
    }
    public async Task<AuthorizedPage> ListReconciliationsAsync(ActorContext actor, long poolId, int offset, int limit, CancellationToken ct = default)
    {
        if (poolId <= 0) throw B.Unavailable(); AuthorizationStore.ValidatePage(offset, limit); await ReconciliationStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            var context = await OperatingPolicyContextAsync(actor, snapshot, tx, poolId, Permissions.StockSourceReconciliationRead, token);
            var filter = F.Eq("poolId", poolId);
            var total = await Rows(B.Proposals).CountDocumentsAsync(tx, filter, cancellationToken: token);
            var selected = await Rows(B.Proposals).Find(tx, filter).Sort(new BsonDocument("_id", 1)).Skip(offset).Limit(limit).ToListAsync(token);
            var items = new List<BsonDocument>();
            foreach (var raw in selected)
            {
                var proposal = await ReconciliationProposalAsync(tx, raw["_id"].AsInt64, token);
                items.Add(ReconciliationView(proposal, await ReconciliationDecisionAsync(tx, proposal, token)));
            }
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceReconciliationRead, context.Source.Resources, tx, token);
            return new AuthorizedPage(total, items);
        }, ct);
    }
    public async Task<BsonDocument?> RecoverReconciliationAsync(ActorContext actor, string operation, string key, CancellationToken ct = default)
    {
        var permission = operation switch { B.ProposeOperation => Permissions.StockSourceReconciliationPropose,
            B.DecideOperation => Permissions.StockSourceReconciliationDecide, _ => throw B.Unavailable() };
        if (key is not { Length: > 0 and <= 128 } || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw B.Unavailable();
        await ReconciliationStorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (snapshot, tx, token) =>
        {
            var table = operation == B.ProposeOperation ? B.Proposals : B.Decisions;
            var row = await Rows(table).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", key)).SingleOrDefaultAsync(token);
            if (row is null)
            {
                if (await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", key)).AnyAsync(token)) throw B.Unavailable();
                return null;
            }
            var context = await OperatingPolicyContextAsync(actor, snapshot, tx, row["poolId"].AsInt64, permission, token);
            var proposal = await ReconciliationProposalAsync(tx, operation == B.ProposeOperation ? row["_id"].AsInt64 : row["proposalId"].AsInt64, token);
            var decision = await ReconciliationDecisionAsync(tx, proposal, token);
            if (operation == B.DecideOperation && (decision is null || decision["_id"] != row["_id"])) throw B.Unavailable();
            await CompleteAuthorityAsync(actor, snapshot, permission, context.Source.Resources, tx, token);
            return B.Result(operation == B.ProposeOperation ? proposal : decision!);
        }, ct);
    }
}
