using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;

namespace UnitToko.Platform.Billing;

public sealed record InvoiceCandidate(LongId ShipmentId, LongId ReceiptId, DateTimeOffset ReceivedAt, string BuyerName, int AcceptedLines, bool Invoiced);
public sealed record InvoiceCandidatePage(ImmutableArray<InvoiceCandidate> Items, LongId? NextShipmentId);
public sealed record InvoicePage(ImmutableArray<InvoiceView> Items, bool HasMore);
public sealed record InvoiceDownload(string Content, string Hash, int Length, string FileName);

public sealed partial class InvoiceService
{
    public async Task<InvoiceCandidatePage> CandidatesAsync(ActorContext actor, long? beforeShipmentId = null, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var shipments = await receipts.InvoiceShipmentsAsync(actor, authority, tx, beforeShipmentId, token);
            var result = ImmutableArray.CreateBuilder<InvoiceCandidate>(); var scopes = new List<ResourceScope>();
            foreach (var id in shipments) {
                var source = await receipts.InvoiceSourceAsync(actor, authority, tx, id, Permissions.InvoiceIssue, token);
                if (source is null) continue; scopes.Add(source.Resource);
                var accepted = source.Snapshot["receipt"]["lines"].AsBsonArray;
                if (accepted.Count == 0) continue;
                var allocations = await records.OrderAllocationsAsync(actor, tx, source.Snapshot["commercial"]["orderId"].AsInt64, token);
                var billed = allocations.Count(row => row["receiptId"] == source.Snapshot["receipt"]["id"]);
                if (billed != 0 && billed != accepted.Count) throw InvoiceRecords.Invalid();
                if (Array.IndexOf(shipments.ToArray(), id) < 25) result.Add(new(new(id), new(source.Snapshot["receipt"]["id"].AsInt64),
                    ShipmentReceiptRecords.Time(source.Snapshot["receipt"]["recordedAt"]), source.Snapshot["commercial"]["terms"]["buyer"]["name"].AsString, accepted.Count, billed > 0));
            }
            await receipts.InvoiceFenceAsync(actor, authority, tx, scopes, Permissions.InvoiceIssue, token);
            // Cursor tracks scanned, scoped shipments, including those with no accepted receipt.
            return new InvoiceCandidatePage(result.ToImmutable(), shipments.Length > 25 ? new(shipments[24]) : null);
        }, ct);
    }
    private async Task<BsonDocument> AuthorizedInvoiceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long id, string permission, CancellationToken ct) {
        AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, permission, clock.UtcNow));
        var row = await records.Rows(InvoiceBson.Invoices).Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct) ?? throw InvoiceRecords.Invalid();
        var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow);
        var resources = InvoiceBson.Sources(row["sources"].AsBsonArray).Select(source => source.Resource).ToArray();
        if (resources.Length == 0 || resources.Any(resource => !query.Matches(resource))) throw InvoiceRecords.Invalid();
        row = await records.VerifyAsync(actor, tx, row, ct);
        var live = await SourcesAsync(actor, authority, tx, InvoiceBson.Selected(row), permission, ct);
        if (!InvoiceRecords.Equal(row["sources"], InvoiceBson.Sources(live))) throw InvoiceRecords.Invalid();
        foreach (var orderId in row["orderIds"].AsBsonArray) await records.OrderAllocationsAsync(actor, tx, orderId.AsInt64, ct);
        await receipts.InvoiceFenceAsync(actor, authority, tx, resources, permission, ct); return row;
    }
    public async Task<InvoiceView> ReadAsync(ActorContext actor, long id, CancellationToken ct = default) {
        if (id <= 0) throw InvoiceRecords.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => View(await AuthorizedInvoiceAsync(actor, authority, tx, id, Permissions.InvoiceRead, token)), ct);
    }
    public async Task<InvoiceDownload> DownloadAsync(ActorContext actor, long id, CancellationToken ct = default) {
        if (id <= 0) throw InvoiceRecords.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var row = await AuthorizedInvoiceAsync(actor, authority, tx, id, Permissions.InvoiceRead, token);
            return new InvoiceDownload(row["document"].AsString, row["documentHash"].AsString, row["documentLength"].AsInt32, $"invoice-{id}.html");
        }, ct);
    }
    public async Task<InvoiceView?> RecoverAsync(ActorContext actor, string key, CancellationToken ct = default) {
        AuthorizationStore.ValidateMetadata(new(key, "Recover original invoice", "invoice-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, Permissions.InvoiceIssue, clock.UtcNow));
            var rows = await records.Rows(InvoiceBson.Invoices).Find(tx, new BsonDocument { { "issuedBy", actor.UserId }, { "requestKey", key } }).Limit(2).ToListAsync(token);
            var receipt = await records.Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "operation", InvoiceBson.Operation }, { "key", key } }).SingleOrDefaultAsync(token);
            if (rows.Count == 0 && receipt is null) { await receipts.InvoiceFenceAsync(actor, authority, tx, [], Permissions.InvoiceIssue, token); return null; }
            if (rows.Count != 1 || receipt is null) throw InvoiceRecords.Invalid();
            return View(await AuthorizedInvoiceAsync(actor, authority, tx, rows[0]["_id"].AsInt64, Permissions.InvoiceIssue, token));
        }, ct);
    }
    public Task<InvoicePage> ListAsync(ActorContext actor, long? beforeId = null, CancellationToken ct = default) =>
        ListCoreAsync(actor, beforeId, null, ct);
    public Task<InvoicePage> ListForOrderAsync(ActorContext actor, long orderId, long? beforeId = null, CancellationToken ct = default) {
        if (orderId <= 0) throw InvoiceRecords.Invalid();
        return ListCoreAsync(actor, beforeId, orderId, ct);
    }
    private async Task<InvoicePage> ListCoreAsync(ActorContext actor, long? beforeId, long? orderId, CancellationToken ct) {
        if (beforeId is <= 0) throw InvoiceRecords.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var query = ScopePolicy.Compile(actor, authority, Permissions.InvoiceRead, clock.UtcNow); AuthorizationStore.Require(query);
            var scoped = ScopeQuery.Create(query).Render(new RenderArgs<BsonDocument>(BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry));
            var pipeline = new List<BsonDocument>();
            if (beforeId.HasValue) pipeline.Add(new("$match", new BsonDocument("_id", new BsonDocument("$lt", beforeId.Value))));
            if (orderId.HasValue) pipeline.Add(new("$match", new BsonDocument("orderIds", orderId.Value)));
            // Every constituent scope must match before any pagination or existence disclosure.
            pipeline.Add(new("$set", new BsonDocument("scopeCount", new BsonDocument("$size", "$scopes"))));
            pipeline.Add(new("$unwind", "$scopes"));
            pipeline.Add(new("$replaceRoot", new BsonDocument("newRoot", new BsonDocument("$mergeObjects", new BsonArray {
                "$scopes", new BsonDocument { { "invoiceId", "$_id" }, { "scopeCount", "$scopeCount" } } }))));
            pipeline.Add(new("$match", scoped));
            pipeline.Add(new("$group", new BsonDocument { { "_id", "$invoiceId" }, { "matches", new BsonDocument("$sum", 1) }, { "expected", new BsonDocument("$first", "$scopeCount") } }));
            pipeline.Add(new("$match", new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$matches", "$expected" }))));
            pipeline.Add(new("$sort", new BsonDocument("_id", -1))); pipeline.Add(new("$limit", 26));
            var ids = await records.Rows(InvoiceBson.Invoices).Aggregate<BsonDocument>(tx, pipeline).ToListAsync(token);
            var result = ImmutableArray.CreateBuilder<InvoiceView>(); var allScopes = new List<ResourceScope>();
            foreach (var value in ids) {
                var row = await AuthorizedInvoiceAsync(actor, authority, tx, value["_id"].AsInt64, Permissions.InvoiceRead, token);
                result.Add(View(row)); allScopes.AddRange(InvoiceBson.Sources(row["sources"].AsBsonArray).Select(source => source.Resource));
            }
            await receipts.InvoiceFenceAsync(actor, authority, tx, allScopes, Permissions.InvoiceRead, token);
            return new InvoicePage([.. result.Take(25)], ids.Count > 25);
        }, ct);
    }
}
