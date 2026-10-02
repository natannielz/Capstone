using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Procurement.ProcurementRequestBson;
using S = UnitToko.Platform.Procurement.ProcurementRequestPurchaseOrderSchemas;

namespace UnitToko.Platform.Procurement;

internal sealed class ProcurementPurchaseOrderCapture
{
    private readonly object owner;
    private readonly ActorContext actor;
    private readonly AuthorizationSnapshot authority;
    private readonly IClientSessionHandle tx;
    private readonly long transaction;
    private readonly string hash;
    private readonly Action current;
    private readonly Func<CancellationToken, Task> fence;
    private readonly Func<long, long, string, DateTimeOffset, CancellationToken, Task<BsonArray>> allocate;
    private bool allocated;
    internal BsonDocument Request { get; }
    internal BsonDocument Decision { get; }
    internal BsonDocument Sources { get; }
    internal BsonArray Capacity { get; }
    internal ProcurementPurchaseOrderCapture(object owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        BsonDocument request, BsonDocument decision, BsonDocument sources, BsonArray capacity, Action current, Func<CancellationToken, Task> fence,
        Func<long, long, string, DateTimeOffset, CancellationToken, Task<BsonArray>> allocate) {
        this.owner = owner; this.actor = actor; this.authority = authority; this.tx = tx; transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        Request = request.DeepClone().AsBsonDocument; Decision = decision.DeepClone().AsBsonDocument; Sources = sources.DeepClone().AsBsonDocument; Capacity = capacity.DeepClone().AsBsonArray;
        this.current = current; this.fence = fence; this.allocate = allocate; hash = Digest();
    }
    private string Digest() => B.Hash(new() { { "request", Request }, { "decision", Decision }, { "sources", Sources }, { "capacity", Capacity } });
    internal void RequireContext(object owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx) {
        if (!ReferenceEquals(this.owner, owner) || this.actor != actor || !ReferenceEquals(this.authority, authority) || !ReferenceEquals(this.tx, tx) ||
            !tx.IsInTransaction || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction || Digest() != hash) throw B.Broken();
        current();
    }
    internal void RequireCurrent() => RequireContext(owner, actor, authority, tx);
    internal async Task FenceAsync(CancellationToken ct) { RequireContext(owner, actor, authority, tx); await fence(ct); RequireContext(owner, actor, authority, tx); ct.ThrowIfCancellationRequested(); }
    internal async Task<BsonArray> AllocateAsync(long orderId, long approvalId, string approvalHash, DateTimeOffset recordedAt, CancellationToken ct) {
        RequireContext(owner, actor, authority, tx); if (allocated) throw B.Broken();
        var rows = await allocate(orderId, approvalId, approvalHash, recordedAt, ct); allocated = true; RequireContext(owner, actor, authority, tx); return rows;
    }
}

public sealed partial class ProcurementRequestService
{
    private sealed record CommitmentLine(BsonDocument? Head, BsonDocument? Previous, ProcurementRequestLineInput Input, int Committed);
    private void RequirePurchaseOrderTransaction(IMongoDatabase owner, IClientSessionHandle tx) {
        if (owner is null || !ReferenceEquals(owner.Client, database.Client) || owner.DatabaseNamespace != database.DatabaseNamespace ||
            !ReferenceEquals(tx.Client, database.Client) || !tx.IsInTransaction) throw B.Broken();
    }
    private async Task PurchaseOrderStorageAsync(CancellationToken ct) {
        await StorageAsync(ct);
        foreach (var name in S.Collections) {
            await InventoryRepository.RequireStorageAsync(database, name, S.Validator(name), S.Keys(name), ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
                options.Contains("encryptedFields") || options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw B.Broken();
            if ((await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression"))) throw B.Broken();
        }
    }
    private async Task<BsonDocument> CommitmentRowAsync(IClientSessionHandle tx, string collection, BsonDocument row, CancellationToken ct) =>
        await Rows(collection).Find(tx, new BsonDocument { { "_id", row["_id"] }, { "$and", new BsonArray { S.Validator(collection) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
    private static void RequireCommitmentLink(BsonDocument row, BsonDocument request, BsonDocument decision, long skuId) {
        if (row["requestId"] != request["_id"] || row["requestHash"] != request["snapshotHash"] || row["requestDecisionId"] != decision["_id"] ||
            row["requestDecisionHash"] != decision["snapshotHash"] || row["skuId"] != skuId) throw B.Broken();
    }
    private async Task<CommitmentLine> CommitmentAsync(IClientSessionHandle tx, BsonDocument request, BsonDocument decision, ProcurementRequestLineInput line, CancellationToken ct) {
        var filter = new BsonDocument { { "requestId", request["_id"] }, { "skuId", line.SkuId } };
        var rawHead = await Rows(S.Commitments).Find(tx, filter).SingleOrDefaultAsync(ct);
        var head = rawHead is null ? null : await CommitmentRowAsync(tx, S.Commitments, rawHead, ct);
        var allocations = await Rows(S.Allocations).Find(tx, filter).Sort(new BsonDocument("sequence", 1)).ToListAsync(ct);
        var owner = new PurchaseOrderRecords(database, catalog);
        var claims = await owner.RequestClaimsAsync(tx, request["_id"].AsInt64, line.SkuId, ct);
        if (claims.Count != allocations.Count || (head is null) != (allocations.Count == 0)) throw B.Broken();
        BsonDocument? previous = null; long sequence = 0; var sum = 0;
        foreach (var raw in allocations) {
            var row = await CommitmentRowAsync(tx, S.Allocations, raw, ct); RequireCommitmentLink(row, request, decision, line.SkuId);
            if (row["snapshotHash"] != B.Hash(row) || row["sequence"] != ++sequence || row["previousAllocationId"] != (previous?["_id"] ?? BsonNull.Value) ||
                row["previousHash"] != (previous?["snapshotHash"] ?? BsonNull.Value) || row["recordedAt"].ToUniversalTime() < decision["recordedAt"].ToUniversalTime() ||
                previous is not null && row["recordedAt"].ToUniversalTime() < previous["recordedAt"].ToUniversalTime()) throw B.Broken();
            var claim = claims.Where(value => value["orderId"] == row["orderId"] && value["approvalId"] == row["approvalId"]).ToArray();
            if (claim.Length != 1 || claim[0]["approvalHash"] != row["approvalHash"] || claim[0]["packages"] != row["packages"] || claim[0]["recordedAt"] != row["recordedAt"]) throw B.Broken();
            await owner.RequireRequestAllocationAsync(tx, row, ct); sum = checked(sum + row["packages"].AsInt32); if (sum > line.Packages) throw B.Broken(); previous = row;
        }
        if (head is not null) {
            RequireCommitmentLink(head, request, decision, line.SkuId);
            if (head["requestedPackages"] != line.Packages || head["committedPackages"] != sum || head["version"] != sequence ||
                head["createdAt"] != allocations[0]["recordedAt"] || head["updatedAt"] != previous!["recordedAt"]) throw B.Broken();
        }
        return new(head, previous, line, sum);
    }
    internal async Task<ProcurementPurchaseOrderCapture> CapturePurchaseOrderAsync(IMongoDatabase owner, ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long requestId, long requestVersion, long decisionId, string permission, IReadOnlyList<PurchaseOrderLineInput> lines, CancellationToken ct) {
        RequirePurchaseOrderTransaction(owner, tx);
        if (permission is not (Permissions.ProcurementRead or Permissions.ProcurementPrepare or Permissions.ProcurementApprove) || requestId <= 0 || requestVersion != 1 || decisionId <= 0 ||
            lines is null || lines.Count > 100 || permission != Permissions.ProcurementRead && lines.Count == 0 || lines.Any(line => line is null || line.RequestSkuId <= 0 || line.Packages is < 1 or > 1000000) ||
            lines.Select(line => line.RequestSkuId).Distinct().Count() != lines.Count) throw ProcurementRequestRules.Invalid();
        lines = lines.ToArray();
        await PurchaseOrderStorageAsync(ct); _ = Query(actor, authority, permission);
        var route = await Rows(B.Requests).Find(tx, new BsonDocument("_id", requestId)).Project(new BsonDocument("location", 1)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        var context = await ContextAsync(actor, authority, tx, B.Location(route["location"]), permission, ct);
        var request = await records.RequestAsync(tx, requestId, ct); var decision = await records.DecisionForAsync(tx, request, ct) ?? throw B.Changed();
        if (decision["_id"] != decisionId || decision["decision"] != "approve") throw B.Changed();
        var input = B.Input(request["input"]); context.Proposal = request; BindPeople(context, request, decision);
        foreach (var table in S.Collections) if (await Rows(table).Find(tx, new BsonDocument { { "requestId", requestId },
            { "skuId", new BsonDocument("$nin", new BsonArray(input.Lines.Select(line => new BsonInt64(line.SkuId)))) } }).AnyAsync(ct)) throw B.Broken();
        await PeopleAsync(context, decision["actorId"].AsInt64, request["actorId"].AsInt64, ct); Equal(context.Sources, request["sources"]);
        context.Catalog = await catalog.CaptureProcurementAsync(database, actor, authority, tx, input.Location, permission, input.Lines, ct); Equal(context.Catalog.Sources, request["catalog"]);
        if (lines.Any(line => !input.Lines.Any(original => original.SkuId == line.RequestSkuId && original.DefinitionId == line.DefinitionId && original.DefinitionVersion == line.DefinitionVersion))) throw B.Changed();
        var selected = lines.OrderBy(line => line.RequestSkuId).Select(line => (line.RequestSkuId, line.Packages)).ToArray();
        var commitments = new Dictionary<long, CommitmentLine>(); var capacity = new BsonArray();
        foreach (var line in input.Lines) {
            var state = await CommitmentAsync(tx, request, decision, line, ct); commitments.Add(line.SkuId, state);
            if (selected.Any(needed => needed.RequestSkuId == line.SkuId && needed.Packages > line.Packages - state.Committed)) throw CapacityUnavailable();
            capacity.Add(new BsonDocument { { "requestSkuId", line.SkuId }, { "requestedPackages", line.Packages }, { "committedPackages", state.Committed },
                { "availablePackages", line.Packages - state.Committed }, { "headId", state.Head?["_id"] ?? BsonNull.Value }, { "version", state.Head?["version"] ?? new BsonInt64(0) } });
        }
        async Task Fence(CancellationToken token) {
            await FenceAsync(context, token);
            foreach (var state in commitments.Values.OrderBy(value => value.Input.SkuId)) if (state.Head is { } head) {
                if (head["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(S.Commitments).UpdateOneAsync(tx, head, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
                head["fence"] = head["fence"].AsInt64 + 1;
            }
            Current(context); token.ThrowIfCancellationRequested();
        }
        async Task<BsonArray> Allocate(long orderId, long approvalId, string approvalHash, DateTimeOffset recordedAt, CancellationToken token) {
            if (permission != Permissions.ProcurementApprove || selected.Length == 0 || orderId <= 0 || approvalId <= 0 || approvalHash is not { Length: 64 } ||
                approvalHash.Any(c => !char.IsAsciiHexDigitLower(c)) || recordedAt.Offset != TimeSpan.Zero || recordedAt.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
                recordedAt < new DateTimeOffset(decision["recordedAt"].ToUniversalTime()) || recordedAt > Now) throw B.Broken();
            await Fence(token); var rows = new BsonArray(); var proof = new PurchaseOrderRecords(database, catalog);
            foreach (var line in selected) {
                var state = commitments[line.RequestSkuId]; var next = checked(state.Committed + line.Packages); if (next > state.Input.Packages) throw CapacityUnavailable();
                var allocationId = (await ids.NextAsync(S.Allocations, token)).Value;
                var allocation = new BsonDocument { { "_id", allocationId }, { "schemaVersion", 1 }, { "requestId", requestId }, { "requestHash", request["snapshotHash"] },
                    { "requestDecisionId", decisionId }, { "requestDecisionHash", decision["snapshotHash"] }, { "skuId", line.RequestSkuId }, { "packages", line.Packages },
                    { "orderId", orderId }, { "approvalId", approvalId }, { "approvalHash", approvalHash }, { "sequence", state.Head is null ? 1L : checked(state.Head["version"].AsInt64 + 1) },
                    { "previousAllocationId", state.Previous?["_id"] ?? BsonNull.Value }, { "previousHash", state.Previous?["snapshotHash"] ?? BsonNull.Value }, { "recordedAt", recordedAt.UtcDateTime } };
                allocation.Add("snapshotHash", B.Hash(allocation)); await proof.RequireRequestAllocationAsync(tx, allocation, token);
                BsonDocument head;
                if (state.Head is null) {
                    head = new BsonDocument { { "_id", (await ids.NextAsync(S.Commitments, token)).Value }, { "schemaVersion", 1 }, { "requestId", requestId }, { "requestHash", request["snapshotHash"] },
                        { "requestDecisionId", decisionId }, { "requestDecisionHash", decision["snapshotHash"] }, { "skuId", line.RequestSkuId }, { "requestedPackages", state.Input.Packages },
                        { "committedPackages", next }, { "version", 1L }, { "fence", 0L }, { "createdAt", recordedAt.UtcDateTime }, { "updatedAt", recordedAt.UtcDateTime } };
                    await Rows(S.Commitments).InsertOneAsync(tx, head, cancellationToken: token);
                } else {
                    head = state.Head.DeepClone().AsBsonDocument; head["committedPackages"] = next; head["version"] = allocation["sequence"]; head["updatedAt"] = recordedAt.UtcDateTime;
                    if (head["fence"].AsInt64 >= long.MaxValue - 1) throw B.Broken(); head["fence"] = head["fence"].AsInt64 + 1;
                    if ((await Rows(S.Commitments).ReplaceOneAsync(tx, state.Head, head, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
                }
                await Rows(S.Allocations).InsertOneAsync(tx, allocation, cancellationToken: token); commitments[line.RequestSkuId] = new(head, allocation, state.Input, next); rows.Add(allocation);
            }
            foreach (var line in selected) _ = await CommitmentAsync(tx, request, decision, commitments[line.RequestSkuId].Input, token);
            await FenceAsync(context, token); Current(context); token.ThrowIfCancellationRequested(); return rows;
        }
        await Fence(ct);
        return new(this, actor, authority, tx, request, decision, context.Sources, capacity, () => Current(context), Fence, Allocate);
    }
    internal Task FencePurchaseOrderAsync(IMongoDatabase owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementPurchaseOrderCapture capture, CancellationToken ct) {
        RequirePurchaseOrderTransaction(owner, tx); capture.RequireContext(this, actor, authority, tx); return capture.FenceAsync(ct);
    }
    internal Task<BsonArray> AllocatePurchaseOrderAsync(IMongoDatabase owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementPurchaseOrderCapture capture,
        long orderId, long approvalId, string approvalHash, DateTimeOffset recordedAt, CancellationToken ct) {
        RequirePurchaseOrderTransaction(owner, tx); capture.RequireContext(this, actor, authority, tx); return capture.AllocateAsync(orderId, approvalId, approvalHash, recordedAt, ct);
    }
    internal async Task RequirePurchaseOrderAllocationsAsync(IMongoDatabase owner, IClientSessionHandle tx, BsonDocument request, BsonDocument decision, CancellationToken ct) {
        RequirePurchaseOrderTransaction(owner, tx); await PurchaseOrderStorageAsync(ct);
        var actual = await records.RequestAsync(tx, request["_id"].AsInt64, ct); var approved = await records.DecisionForAsync(tx, actual, ct);
        if (!actual.Equals(request) || approved is null || !approved.Equals(decision) || approved["decision"] != "approve") throw B.Broken();
        var input = B.Input(actual["input"]);
        foreach (var table in S.Collections) if (await Rows(table).Find(tx, new BsonDocument { { "requestId", actual["_id"] },
            { "skuId", new BsonDocument("$nin", new BsonArray(input.Lines.Select(line => new BsonInt64(line.SkuId)))) } }).AnyAsync(ct)) throw B.Broken();
        foreach (var line in input.Lines) _ = await CommitmentAsync(tx, actual, approved, line, ct);
        RequirePurchaseOrderTransaction(owner, tx); ct.ThrowIfCancellationRequested();
    }
    private static PersistenceConflictException CapacityUnavailable() => new("procurement_request_capacity_unavailable", "The approved request no longer has enough uncommitted packages");
}
