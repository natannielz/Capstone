using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.PaymentDraftBson;

namespace UnitToko.Platform.Billing;
public sealed record PaymentInvoiceReference(LongId InvoiceId, string Number, Money Total, string BuyerName, string SellerName);
public sealed record PaymentDraftView(LongId Id, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string State,
    PaymentDraftInput Input, ImmutableArray<PaymentInvoiceReference> Invoices, PaymentSubmissionView? Submission = null);
public sealed record PaymentSubmissionView(LongId Id, long ReviewRevision, LongId ReservationId, UnitToko.Modules.Documents.EvidenceRef Evidence, DateTimeOffset SubmittedAt);
public sealed record PaymentDraftPage(ImmutableArray<PaymentDraftView> Items, bool HasMore);

// One declaration owner. Saving declared values never creates cash, alters AR or verifies a bank receipt.
public sealed partial class PaymentDraftService
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly IdAllocator ids;
    private readonly AuthorizationStore authorization;
    private readonly InvoiceService invoices;
    private readonly PaymentDraftRecords records;
    public PaymentDraftService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, InvoiceService invoices) {
        if (!ReferenceEquals(database.Client, invoices.PaymentSourceDatabase.Client) || !database.DatabaseNamespace.Equals(invoices.PaymentSourceDatabase.DatabaseNamespace))
            throw new ArgumentException("Payment and invoice owners must share the same transaction");
        this.database = database; this.clock = clock; this.ids = ids; this.invoices = invoices;
        authorization = new(database, unit, clock); records = new(database);
    }
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private async Task StorageAsync(CancellationToken ct) {
        await invoices.PaymentStorageAsync(ct);
        await InventoryRepository.RequireStorageAsync(database, B.Heads, PaymentSubmissionSchemas.DeclarationValidator(), PaymentDraftSchemas.Key(B.Heads), ct);
        await ReplacementStorageAsync(ct);
        foreach (var name in PaymentSubmissionSchemas.Collections)
            await SupplierDocumentEvidenceSchemas.RequireCompatibleStorageAsync(database, name, PaymentSubmissionSchemas.Validator(name), PaymentSubmissionSchemas.Key(name), ct);
    }
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot authority) {
        var query = ScopePolicy.Compile(actor, authority, Permissions.PaymentEvidence, clock.UtcNow); AuthorizationStore.Require(query); return query;
    }
    private void Require(ActorContext actor, AuthorizationSnapshot authority, BsonArray scopes) {
        var query = Query(actor, authority);
        if (scopes.Count == 0 || B.Scopes(scopes).Any(scope => scope.CreatedBy != actor.UserId || !query.Matches(scope))) throw B.Invalid();
    }
    private async Task FenceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, BsonArray scopes, CancellationToken ct) {
        var query = Query(actor, authority); var resources = B.Scopes(scopes);
        var clauses = query.Clauses.Where(clause => resources.Length == 0 || resources.Any(clause.Matches)).ToArray();
        var nodes = clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory))).Distinct();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, nodes, [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        if (actor.Session is { } session && !await records.Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId,
                actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).AnyAsync(ct)) throw new AuthorizationDeniedException("stale_or_inactive_session");
        _ = Query(actor, authority); if (scopes.Count > 0) Require(actor, authority, scopes); ct.ThrowIfCancellationRequested();
    }
    private async Task<BsonArray> SourcesAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, PaymentDraftInput input, CancellationToken ct) {
        var sources = new BsonArray();
        foreach (var id in input.InvoiceIds) sources.Add(await invoices.PaymentSourceAsync(actor, authority, tx, id.Value, ct));
        _ = B.Party(sources); Require(actor, authority, B.Scopes(sources, actor.UserId)); return sources;
    }
    private async Task<BsonDocument> HeadAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long id, CancellationToken ct) {
        var head = await records.HeadAsync(tx, actor.UserId, id, ct); Require(actor, authority, head["scopes"].AsBsonArray);
        var version = await records.VersionAsync(tx, head["versionId"].AsInt64, ct);
        if (!InvoiceRecords.Equal(version["invoices"], await SourcesAsync(actor, authority, tx, B.Input(version["input"].AsBsonDocument), ct))) throw B.Invalid();
        return head;
    }
    private async Task<PaymentDraftView> ViewAsync(IClientSessionHandle tx, BsonDocument head, BsonDocument version, CancellationToken ct) {
        var submission = head["state"] == "submitted" ? await new PaymentSubmissionRecords(database).ReadAsync(tx, head["submissionId"].AsInt64, ct) : null;
        return new(new(head["_id"].AsInt64), head["revision"].AsInt64,
        ShipmentReceiptRecords.Time(head["createdAt"]), ShipmentReceiptRecords.Time(head["updatedAt"]), head["state"].AsString, B.Input(version["input"].AsBsonDocument),
        [.. version["invoices"].AsBsonArray.Select(value => new PaymentInvoiceReference(new(value["invoiceId"].AsInt64),
            FormattableString.Invariant($"{value["numberEntity"].AsInt64}/{value["numberChannel"].AsInt64}/{value["numberYear"].AsInt64}/{value["number"].AsInt64}"),
            InvoiceBson.Money(value["total"]), value["buyerName"].AsString, value["sellerName"].AsString))],
        submission is null ? null : new(new(submission["_id"].AsInt64), submission["reviewRevision"].AsInt64, new(submission["reservationId"].AsInt64),
            PaymentSubmissionBson.Reference(submission["evidence"].AsBsonDocument), ShipmentReceiptRecords.Time(submission["submittedAt"])));
    }
    public Task<BsonDocument> SaveAsync(ActorContext actor, long? declarationId, long expectedRevision, PaymentDraftInput input,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) => SaveCoreAsync(actor, declarationId, expectedRevision, input, metadata, null, ct);
    private async Task<BsonDocument> SaveCoreAsync(ActorContext actor, long? declarationId, long expectedRevision, PaymentDraftInput? input,
        AuthorizationCommandMetadata metadata, ReplacementCreation? replacement, CancellationToken ct) {
        AuthorizationStore.ValidateMetadata(metadata); if (replacement is null) input = PaymentDraftPolicy.Copy(input!, AccountingCalendar.BusinessDate(clock.UtcNow));
        if (metadata.IdempotencyKey.Length > 128 || metadata.IdempotencyKey.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or ':')) ||
            (declarationId is null ? expectedRevision != 0 : declarationId <= 0 || expectedRevision is <= 0 or >= long.MaxValue - 1))
            throw PaymentDraftPolicy.Invalid();
        await StorageAsync(ct); var id = declarationId ?? (await ids.NextAsync(B.Heads, ct)).Value;
        var versionId = (await ids.NextAsync(B.Versions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var operation = replacement is null ? B.Operation : B.ReplacementOperation;
        var body = replacement is null ? B.Body(declarationId, expectedRevision, input!) : B.ReplacementBody(replacement.ParentId, replacement.Revision, replacement.Input);
        var bodyHash = AuthorizationStore.CommandHash(body, metadata.Reason);
        BsonDocument? previous = null, origin = null; BsonArray sources = []; BsonArray scopes = [];
        return await authorization.ExecuteCommandAsync(actor, operation, metadata, body, async (authority, tx, token) => {
            _ = Query(actor, authority); previous = null;
            var orphan = await records.Rows(B.Versions).Find(tx, new BsonDocument { { "recordedBy", actor.UserId }, { "requestKey", metadata.IdempotencyKey } }).Limit(2).ToListAsync(token);
            if (orphan.Count > 1) throw B.Invalid(); if (orphan.Count == 1) _ = await records.VersionAsync(tx, orphan[0]["_id"].AsInt64, token);
            if (declarationId.HasValue) previous = await HeadAsync(actor, authority, tx, declarationId.Value, token);
            if (replacement is not null) {
                origin = await ReplacementSourceAsync(actor, authority, tx, replacement, token);
                input = PaymentDraftPolicy.Copy(B.Input(origin["source"]["input"].AsBsonDocument), AccountingCalendar.BusinessDate(clock.UtcNow));
            }
            sources = await SourcesAsync(actor, authority, tx, input!, token); scopes = B.Scopes(sources, actor.UserId);
            if (previous is not null) {
                var first = await records.Rows(B.Versions).Find(tx, new BsonDocument { { "declarationId", previous["_id"] }, { "revision", 1L } }).SingleOrDefaultAsync(token) ?? throw B.Invalid();
                if (first["schemaVersion"] == 2 && !InvoiceRecords.Equal(first["replacement"]["source"]["input"]["invoiceIds"], B.Input(input!)["invoiceIds"]))
                    throw PaymentDraftPolicy.Invalid("payment_replacement_invoices_locked");
            }
            if (previous is not null && !InvoiceRecords.Equal(previous["party"], B.Party(sources))) throw PaymentDraftPolicy.Invalid("payment_parties_locked");
            var resources = B.Scopes(scopes);
            return new(ScopePolicy.Authorize(actor, authority, Permissions.PaymentEvidence, resources[0], clock.UtcNow),
                [.. resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory)).Distinct()], Permissions.PaymentEvidence, new(resources[0]));
        }, async (tx, token) => {
            if (previous is not null && (previous["revision"] != expectedRevision || previous["state"] != "draft")) throw PaymentDraftRecords.Changed();
            var now = Now; if (previous is not null && now < ShipmentReceiptRecords.Time(previous["updatedAt"])) throw B.Invalid();
            if (origin is not null) {
                var existing = await records.Rows(B.Versions).Find(tx, new BsonDocument("replacement.source.declarationId", replacement!.ParentId)).Limit(1).AnyAsync(token);
                if (existing) throw new PersistenceConflictException("payment_replacement_exists", "Read the existing linked replacement before creating another draft");
            }
            var prior = previous is null ? null : await records.VersionAsync(tx, previous["versionId"].AsInt64, token);
            var row = new BsonDocument { { "_id", versionId }, { "schemaVersion", origin is null ? 1 : 2 }, { "declarationId", id }, { "revision", expectedRevision + 1 },
                { "recordedBy", actor.UserId }, { "recordedAt", now.UtcDateTime }, { "previousVersionId", prior is null ? BsonNull.Value : prior["_id"] },
                { "previousHash", prior is null ? BsonNull.Value : prior["snapshotHash"] }, { "input", B.Input(input!) }, { "invoices", sources }, { "party", B.Party(sources) }, { "scopes", scopes },
                { "actorGrantVersion", actor.GrantVersion }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", bodyHash }, { "reason", metadata.Reason },
                { "correlationId", metadata.CorrelationId }, { "auditId", auditId } };
            if (origin is not null) row.Add("replacement", origin); row.Add("snapshotHash", B.Hash(row));
            await records.Rows(B.Versions).InsertOneAsync(tx, row, cancellationToken: token);
            var head = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "versionId", versionId }, { "revision", expectedRevision + 1 },
                { "recordedBy", actor.UserId }, { "state", "draft" }, { "party", row["party"].DeepClone() }, { "scopes", scopes.DeepClone() }, { "fence", 0L },
                { "createdAt", previous is null ? new BsonDateTime(now.UtcDateTime) : previous["createdAt"] }, { "updatedAt", now.UtcDateTime } };
            if (previous is null) await records.Rows(B.Heads).InsertOneAsync(tx, head, cancellationToken: token);
            else if ((await records.Rows(B.Heads).ReplaceOneAsync(tx, previous, head, cancellationToken: token)).ModifiedCount != 1) throw PaymentDraftRecords.Changed();
            await records.Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: token);
            var effect = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", operation },
                { "sourceKey", operation + ":" + versionId }, { "payload", B.Result(row) }, { "createdAt", now.UtcDateTime }, { "dueAt", now.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } };
            if (origin is not null) { effect.Add("actorId", actor.UserId); effect.Add("requestKey", metadata.IdempotencyKey); }
            await records.Rows("outboxEvents").InsertOneAsync(tx, effect, cancellationToken: token);
            return B.Result(row);
        }, ct, async (authority, tx, token) => {
            var version = await RecoveredVersionAsync(actor, tx, metadata.IdempotencyKey, token, operation) ?? throw B.Invalid();
            if (version["bodyHash"] != bodyHash) throw B.Invalid();
            var head = await HeadAsync(actor, authority, tx, version["declarationId"].AsInt64, token);
            await records.TouchAsync(tx, head, token); await FenceAsync(actor, authority, tx, head["scopes"].AsBsonArray, token);
        });
    }
    private async Task<BsonDocument?> RecoveredVersionAsync(ActorContext actor, IClientSessionHandle tx, string key, CancellationToken ct, string operation = B.Operation) {
        var receipt = await records.Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "operation", operation }, { "key", key } }).SingleOrDefaultAsync(ct);
        var rows = await records.Rows(B.Versions).Find(tx, new BsonDocument { { "recordedBy", actor.UserId }, { "requestKey", key }, { "schemaVersion", operation == B.ReplacementOperation ? 2 : 1 } }).Limit(2).ToListAsync(ct);
        if (rows.Count == 0 && receipt is null) return null;
        if (rows.Count != 1 || receipt is null) throw B.Invalid();
        var version = await records.VersionAsync(tx, rows[0]["_id"].AsInt64, ct);
        if (!InvoiceRecords.Equal(receipt["result"], B.Result(version))) throw B.Invalid(); return version;
    }
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor, string key, CancellationToken ct = default) {
        AuthorizationStore.ValidateMetadata(new(key, "Read original declaration", "payment-draft-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            _ = Query(actor, authority); var version = await RecoveredVersionAsync(actor, tx, key, token);
            if (version is null) { await FenceAsync(actor, authority, tx, [], token); return null; }
            var head = await HeadAsync(actor, authority, tx, version["declarationId"].AsInt64, token); await records.TouchAsync(tx, head, token);
            await FenceAsync(actor, authority, tx, head["scopes"].AsBsonArray, token); return B.Result(version);
        }, ct);
    }
    public async Task<PaymentDraftView> ReadAsync(ActorContext actor, long declarationId, CancellationToken ct = default) {
        if (declarationId <= 0) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var head = await HeadAsync(actor, authority, tx, declarationId, token); var version = await records.VersionAsync(tx, head["versionId"].AsInt64, token);
            var view = await ViewAsync(tx, head, version, token);
            await records.TouchAsync(tx, head, token); await FenceAsync(actor, authority, tx, head["scopes"].AsBsonArray, token); return view;
        }, ct);
    }
    public async Task<PaymentDraftPage> ListAsync(ActorContext actor, long? beforeId = null, CancellationToken ct = default) {
        if (beforeId is <= 0) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var query = Query(actor, authority);
            var match = new BsonDocument("recordedBy", actor.UserId); if (beforeId.HasValue) match.Add("_id", new BsonDocument("$lt", beforeId.Value));
            var scoped = ScopeQuery.Create(query).Render(new RenderArgs<BsonDocument>(BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry));
            // Current scope filtering precedes paging, including multi-invoice declarations.
            BsonDocument[] pipeline = [new("$match", match), new("$set", new BsonDocument("scopeCount", new BsonDocument("$size", "$scopes"))), new("$unwind", "$scopes"),
                new("$replaceRoot", new BsonDocument("newRoot", new BsonDocument("$mergeObjects", new BsonArray { "$scopes", new BsonDocument { { "declarationId", "$_id" }, { "scopeCount", "$scopeCount" } } }))),
                new("$match", scoped), new("$group", new BsonDocument { { "_id", "$declarationId" }, { "matches", new BsonDocument("$sum", 1) }, { "expected", new BsonDocument("$first", "$scopeCount") } }),
                new("$match", new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$matches", "$expected" }))), new("$sort", new BsonDocument("_id", -1)), new("$limit", 26)];
            var ids = await records.Rows(B.Heads).Aggregate<BsonDocument>(tx, pipeline).ToListAsync(token); var result = ImmutableArray.CreateBuilder<PaymentDraftView>(); var allScopes = new BsonArray();
            foreach (var value in ids) {
                var head = await HeadAsync(actor, authority, tx, value["_id"].AsInt64, token); var version = await records.VersionAsync(tx, head["versionId"].AsInt64, token);
                await records.TouchAsync(tx, head, token); result.Add(await ViewAsync(tx, head, version, token)); allScopes.AddRange(head["scopes"].AsBsonArray);
            }
            await FenceAsync(actor, authority, tx, allScopes, token); return new PaymentDraftPage([.. result.Take(25)], ids.Count > 25);
        }, ct);
    }
}
