using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;

namespace UnitToko.Platform.Fulfillment;
public sealed record AfterSalesSourceLine(LongId OrderId, LongId ShipmentId, LongId ReceiptId, LongId SkuId, string SkuCode, string ProductName,
    string PackageLabel, int BaseUnitsPerPackage, string BaseUnitCode, long AcceptedBaseUnits);
public sealed record AfterSalesSourceView(LongId InvoiceId, string Binding, string Number, string BuyerName, string SellerName, DateTimeOffset IssuedAt, ImmutableArray<AfterSalesSourceLine> Lines);
public sealed record AfterSalesCaseView(LongId Id, long Revision, DateTimeOffset RequestedAt, string State, string Issue, string ReportedProblem,
    AfterSalesSourceView Source, ImmutableArray<AfterSalesCaseLine> Lines, AfterSalesDecisionView? Decision = null);
public sealed record AfterSalesCasePage(ImmutableArray<AfterSalesCaseView> Items, bool HasMore);
public sealed partial class AfterSalesCaseService
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly IdAllocator ids;
    private readonly InvoiceService invoices;
    private readonly AuthorizationStore authorization;
    private readonly R records;
    public AfterSalesCaseService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, InvoiceService invoices) {
        if (!ReferenceEquals(database.Client, invoices.PaymentSourceDatabase.Client) || !database.DatabaseNamespace.Equals(invoices.PaymentSourceDatabase.DatabaseNamespace))
            throw new ArgumentException("Case and finalized-sale owners must share the same transaction");
        this.database = database; this.clock = clock; this.ids = ids; this.invoices = invoices; authorization = new(database, unit, clock); records = new(database);
    }
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private async Task StorageAsync(CancellationToken ct) {
        await invoices.PaymentStorageAsync(ct);
        await invoices.CaseReturnStorageAsync(ct);
        await InventoryRepository.RequireStorageAsync(database, R.Collection, AfterSalesCaseSchemas.Validator(), AfterSalesCaseSchemas.Key(), ct);
        await InventoryRepository.RequireStorageAsync(database, AfterSalesDecisionRecords.Collection, AfterSalesDecisionSchemas.Validator(), AfterSalesDecisionSchemas.Key(), ct);
        if (!await new CollectionMigrations(database, clock).IsCurrentAsync(ct)) throw R.Broken();
    }
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot authority, string permission) {
        var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query); return query;
    }
    private void Require(ActorContext actor, AuthorizationSnapshot authority, BsonDocument row, string permission) {
        var query = Query(actor, authority, permission); var scopes = PaymentDraftBson.Scopes(row["scopes"].AsBsonArray);
        if (scopes.Length == 0 || scopes.Any(scope => !query.Matches(scope))) throw R.Broken();
    }
    private async Task<BsonDocument> AuthorizedAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long id, string permission, CancellationToken ct) {
        _ = Query(actor, authority, permission);
        var candidate = await records.Rows(R.Collection).Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct) ?? throw R.Broken(); Require(actor, authority, candidate, permission);
        var row = await records.ReadAsync(tx, id, ct);
        var source = await invoices.CaseSourceAsync(actor, authority, tx, row["source"]["invoiceId"].AsInt64, permission, ct);
        if (!InvoiceRecords.Equal(source, row["source"]) || row["requestedAt"].ToUniversalTime() > Now.UtcDateTime) throw R.Broken();
        var decision = await new AfterSalesDecisionRecords(database).ReadAsync(tx, row, ct);
        if (decision is not null && decision["decidedAt"].ToUniversalTime() > Now.UtcDateTime) throw R.Broken(); return row;
    }
    private async Task FenceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, BsonDocument row, string permission, CancellationToken ct) {
        await records.TouchAsync(tx, row, ct); await invoices.CaseFenceAsync(actor, authority, tx, row["source"]["scopes"].AsBsonArray, permission, ct); Require(actor, authority, row, permission);
    }
    private async Task AuditReadAsync(ActorContext actor, IClientSessionHandle tx, string kind, BsonValue id, CancellationToken ct) =>
        await records.Rows("auditEvents").InsertOneAsync(tx, new BsonDocument { { "_id", (await ids.NextAsync("auditEvents", ct)).Value }, { "schemaVersion", 1 },
            { "kind", "fulfillment.case." + kind }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "resourceId", id }, { "createdAt", Now.UtcDateTime } }, cancellationToken: ct);
    internal static AfterSalesSourceView SourceView(BsonDocument row) => new(new(row["invoiceId"].AsInt64), R.Binding(row), row["number"].AsString, row["buyerName"].AsString, row["sellerName"].AsString,
        new(row["issuedAt"].ToUniversalTime()), [.. row["lines"].AsBsonArray.Select(line => new AfterSalesSourceLine(new(line["orderId"].AsInt64), new(line["shipmentId"].AsInt64),
            new(line["receiptId"].AsInt64), new(line["skuId"].AsInt64), line["skuCode"].AsString, line["productName"].AsString, line["packageLabel"].AsString,
            line["baseUnitsPerPackage"].AsInt32, line["baseUnitCode"].AsString, line["acceptedBaseUnits"].AsInt64))]);
    private async Task<AfterSalesCaseView> ViewAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        var decision = await new AfterSalesDecisionRecords(database).ReadAsync(tx, row, ct);
        return new(new(row["_id"].AsInt64), decision is null ? 1 : 2, new(row["requestedAt"].ToUniversalTime()), decision?["state"].AsString ?? "requested",
            row["input"]["issue"].AsString, row["reason"].AsString, SourceView(row["source"].AsBsonDocument), R.Input(row["input"].AsBsonDocument).Lines,
            decision is null ? null : DecisionView(row, decision));
    }
    public async Task<AfterSalesSourceView> SourceAsync(ActorContext actor, long invoiceId, CancellationToken ct = default) {
        if (invoiceId <= 0) throw AfterSalesCasePolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var source = await invoices.CaseSourceAsync(actor, authority, tx, invoiceId, Permissions.CaseRequest, token);
            await AuditReadAsync(actor, tx, "source.read", invoiceId, token);
            await invoices.CaseFenceAsync(actor, authority, tx, source["scopes"].AsBsonArray, Permissions.CaseRequest, token); return SourceView(source);
        }, ct);
    }
    public async Task<BsonDocument> RequestAsync(ActorContext actor, AfterSalesCaseInput input, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = AfterSalesCasePolicy.Copy(input); AuthorizationStore.ValidateMetadata(metadata);
        if (!ReceivingAccountPolicy.Text(metadata.Reason, 1000) || !ReceivingAccountPolicy.Text(metadata.IdempotencyKey, 128)) throw AfterSalesCasePolicy.Invalid();
        await StorageAsync(ct); var id = (await ids.NextAsync(R.Collection, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var bodyHash = AuthorizationStore.CommandHash(input, metadata.Reason); BsonDocument? source = null, existing = null;
        return await authorization.ExecuteCommandAsync(actor, R.Operation, metadata, input, async (authority, tx, token) => {
            source = await invoices.CaseSourceAsync(actor, authority, tx, input.InvoiceId, Permissions.CaseRequest, token); R.RequireSource(input, source); existing = null;
            var original = await records.Rows(R.Collection).Find(tx, new BsonDocument { { "requestedBy", actor.UserId }, { "requestKey", metadata.IdempotencyKey } }).SingleOrDefaultAsync(token);
            if (original is not null) existing = await AuthorizedAsync(actor, authority, tx, original["_id"].AsInt64, Permissions.CaseRequest, token);
            else foreach (var (table, keyField) in new[] { ("idempotencyRecords", "key"), ("auditEvents", "requestKey"), ("outboxEvents", "requestKey") })
                if (await records.Rows(table).Find(tx, new BsonDocument { { table == "idempotencyRecords" ? "operation" : "kind", R.Operation }, { "actorId", actor.UserId }, { keyField, metadata.IdempotencyKey } }).AnyAsync(token)) throw R.Broken();
            var resources = PaymentDraftBson.Scopes(source["scopes"].AsBsonArray);
            return new(ScopePolicy.Authorize(actor, authority, Permissions.CaseRequest, resources[0], clock.UtcNow),
                [.. resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory)).Distinct()], Permissions.CaseRequest, new(resources[0]));
        }, async (tx, token) => {
            if (existing is not null || source is null) throw R.Broken(); var now = Now;
            if (source["issuedAt"].ToUniversalTime() > now.UtcDateTime) throw R.Broken();
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "revision", 1L }, { "input", R.Input(input) }, { "source", source },
                { "scopes", PaymentDraftBson.Scopes(new BsonArray { source }, actor.UserId) }, { "requestedBy", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
                { "requestedAt", now.UtcDateTime }, { "bodyHash", bodyHash }, { "requestKey", metadata.IdempotencyKey }, { "reason", metadata.Reason },
                { "correlationId", metadata.CorrelationId }, { "auditId", auditId }, { "fence", 0L } }; row.Add("snapshotHash", R.Hash(row));
            await records.Rows(R.Collection).InsertOneAsync(tx, row, cancellationToken: token); await records.Rows("auditEvents").InsertOneAsync(tx, R.Audit(row), cancellationToken: token);
            await records.Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", R.Operation }, { "sourceKey", R.Operation + ":" + id },
                { "actorId", actor.UserId }, { "requestKey", metadata.IdempotencyKey }, { "payload", R.Result(row) }, { "createdAt", now.UtcDateTime }, { "dueAt", now.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            return R.Result(row);
        }, ct, async (authority, tx, token) => {
            var row = await AuthorizedAsync(actor, authority, tx, existing?["_id"].AsInt64 ?? id, Permissions.CaseRequest, token);
            if (row["requestedBy"] != actor.UserId || row["requestKey"] != metadata.IdempotencyKey || row["bodyHash"] != bodyHash) throw R.Broken();
            await FenceAsync(actor, authority, tx, row, Permissions.CaseRequest, token);
        });
    }
    public async Task<AfterSalesCaseView> ReadAsync(ActorContext actor, long id, CancellationToken ct = default) {
        if (id <= 0) throw AfterSalesCasePolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var row = await AuthorizedAsync(actor, authority, tx, id, Permissions.CaseRead, token); await AuditReadAsync(actor, tx, "read", id, token);
            await FenceAsync(actor, authority, tx, row, Permissions.CaseRead, token); return await ViewAsync(tx, row, token);
        }, ct);
    }
    public async Task<AfterSalesCasePage> ListAsync(ActorContext actor, long? beforeId = null, CancellationToken ct = default) {
        if (beforeId is <= 0) throw AfterSalesCasePolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var query = Query(actor, authority, Permissions.CaseRead);
            var filter = ScopeQuery.Create(query).Render(new RenderArgs<BsonDocument>(BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry));
            var pipeline = new List<BsonDocument>(); if (beforeId.HasValue) pipeline.Add(new("$match", new BsonDocument("_id", new BsonDocument("$lt", beforeId.Value))));
            // Every constituent scope matches before pagination, including the sentinel that determines HasMore.
            pipeline.Add(new("$set", new BsonDocument("scopeCount", new BsonDocument("$size", "$scopes")))); pipeline.Add(new("$unwind", "$scopes"));
            pipeline.Add(new("$replaceRoot", new BsonDocument("newRoot", new BsonDocument("$mergeObjects", new BsonArray { "$scopes", new BsonDocument { { "caseId", "$_id" }, { "scopeCount", "$scopeCount" } } }))));
            pipeline.Add(new("$match", filter)); pipeline.Add(new("$group", new BsonDocument { { "_id", "$caseId" }, { "matches", new BsonDocument("$sum", 1) }, { "expected", new BsonDocument("$first", "$scopeCount") } }));
            pipeline.Add(new("$match", new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$matches", "$expected" })))); pipeline.Add(new("$sort", new BsonDocument("_id", -1))); pipeline.Add(new("$limit", 26));
            var candidates = await records.Rows(R.Collection).Aggregate<BsonDocument>(tx, pipeline).ToListAsync(token); var views = ImmutableArray.CreateBuilder<AfterSalesCaseView>(); var scopes = new BsonArray();
            foreach (var candidate in candidates) {
                var row = await AuthorizedAsync(actor, authority, tx, candidate["_id"].AsInt64, Permissions.CaseRead, token);
                await FenceAsync(actor, authority, tx, row, Permissions.CaseRead, token); scopes.AddRange(row["source"]["scopes"].AsBsonArray); views.Add(await ViewAsync(tx, row, token));
            }
            await AuditReadAsync(actor, tx, "list", BsonNull.Value, token); await invoices.CaseFenceAsync(actor, authority, tx, scopes, Permissions.CaseRead, token);
            return new AfterSalesCasePage([.. views.Take(25)], candidates.Count > 25);
        }, ct);
    }
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor, string key, CancellationToken ct = default) {
        AuthorizationStore.ValidateMetadata(new(key, "Recover original case request", "case-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            _ = Query(actor, authority, Permissions.CaseRequest);
            var row = await records.Rows(R.Collection).Find(tx, new BsonDocument { { "requestedBy", actor.UserId }, { "requestKey", key } }).SingleOrDefaultAsync(token);
            if (row is null) {
                foreach (var (name, field) in new[] { ("idempotencyRecords", "key"), ("auditEvents", "requestKey"), ("outboxEvents", "requestKey") })
                    if (await records.Rows(name).Find(tx, new BsonDocument { { name == "idempotencyRecords" ? "operation" : "kind", R.Operation }, { "actorId", actor.UserId }, { field, key } }).AnyAsync(token)) throw R.Broken();
                await AuditReadAsync(actor, tx, "request.recover", BsonNull.Value, token);
                await invoices.CaseFenceAsync(actor, authority, tx, [], Permissions.CaseRequest, token); return null;
            }
            row = await AuthorizedAsync(actor, authority, tx, row["_id"].AsInt64, Permissions.CaseRequest, token); await AuditReadAsync(actor, tx, "request.recover", row["_id"], token);
            await FenceAsync(actor, authority, tx, row, Permissions.CaseRequest, token); return R.Result(row);
        }, ct);
    }
}
