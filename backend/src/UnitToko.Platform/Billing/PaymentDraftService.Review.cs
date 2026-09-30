using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Identity;
using B = UnitToko.Platform.Billing.PaymentDraftBson;

namespace UnitToko.Platform.Billing;

public sealed partial class PaymentDraftService
{
    // A separately granted read permission never confers correction, assignment or bank verification.
    private AuthorizationQuery[] ReviewQueries(ActorContext actor, AuthorizationSnapshot authority) =>
        [.. new[] { Permissions.PaymentReviewRead, Permissions.InvoiceRead }.Select(permission => {
            var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query); return query;
        })];
    private void RequireReview(ActorContext actor, AuthorizationSnapshot authority, BsonArray scopes) {
        var queries = ReviewQueries(actor, authority); var resources = B.Scopes(scopes);
        if (resources.Length == 0 || queries.Any(query => resources.Any(scope => !query.Matches(scope)))) throw B.Invalid();
    }
    private async Task ReviewFenceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, BsonArray scopes, CancellationToken ct) {
        var resources = B.Scopes(scopes);
        foreach (var query in ReviewQueries(actor, authority)) {
            var clauses = query.Clauses.Where(clause => resources.Length == 0 || resources.Any(clause.Matches)).ToArray();
            var nodes = clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory))).Distinct();
            await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, nodes, [],
                authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
                authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        }
        if (actor.Session is { } session) {
            var live = await records.Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId,
                actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct);
            if (live is null || live["idleExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime || live["absoluteExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime)
                throw new AuthorizationDeniedException("stale_or_inactive_session");
        }
        if (scopes.Count > 0) RequireReview(actor, authority, scopes); else _ = ReviewQueries(actor, authority);
        ct.ThrowIfCancellationRequested();
    }
    private async Task<BsonDocument> ReviewHeadAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long id, CancellationToken ct) {
        _ = ReviewQueries(actor, authority);
        var saved = await records.Rows(B.Heads).Find(tx, new BsonDocument { { "_id", id }, { "state", "submitted" } }).SingleOrDefaultAsync(ct) ?? throw B.Invalid();
        RequireReview(actor, authority, saved["scopes"].AsBsonArray);
        var head = await records.HeadAsync(tx, saved["recordedBy"].AsInt64, id, ct);
        var version = await records.VersionAsync(tx, head["versionId"].AsInt64, ct);
        var sources = new BsonArray();
        foreach (var invoice in B.Input(version["input"].AsBsonDocument).InvoiceIds)
            sources.Add(await invoices.PaymentSourceAsync(actor, authority, tx, invoice.Value, ct));
        if (!InvoiceRecords.Equal(version["invoices"], sources)) throw B.Invalid();
        return head;
    }
    private async Task ReviewAuditAsync(ActorContext actor, IClientSessionHandle tx, string operation, IEnumerable<long> selected, AuthorizationReadMetadata metadata, CancellationToken ct) {
        if (metadata.Purpose != "payment-review" || string.IsNullOrWhiteSpace(metadata.CorrelationId) || metadata.CorrelationId.Length > 128 || metadata.CorrelationId.Any(char.IsControl)) throw B.Invalid();
        await records.Rows("auditEvents").InsertOneAsync(tx, new BsonDocument {
            { "_id", (await ids.NextAsync("auditEvents", ct)).Value }, { "schemaVersion", 1 }, { "kind", "billing.payment.review.read" }, { "operation", operation },
            { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "permission", Permissions.PaymentReviewRead },
            { "declarationIds", new BsonArray(selected.Select(value => new BsonInt64(value))) }, { "correlationId", metadata.CorrelationId }, { "createdAt", Now.UtcDateTime }
        }, cancellationToken: ct);
    }
    public async Task<PaymentDraftView> ReadReviewAsync(ActorContext actor, long id, AuthorizationReadMetadata metadata, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var head = await ReviewHeadAsync(actor, authority, tx, id, token);
            var view = await ViewAsync(tx, head, await records.VersionAsync(tx, head["versionId"].AsInt64, token), token);
            await ReviewAuditAsync(actor, tx, "billing.payment.review.detail", [id], metadata, token);
            await records.TouchAsync(tx, head, token); await ReviewFenceAsync(actor, authority, tx, head["scopes"].AsBsonArray, token); return view;
        }, ct);
    }
    public async Task<PaymentDraftPage> ListReviewsAsync(ActorContext actor, long? beforeId, AuthorizationReadMetadata metadata, CancellationToken ct = default) {
        if (beforeId is <= 0) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var queries = ReviewQueries(actor, authority);
            var filter = new BsonDocument("state", "submitted"); if (beforeId.HasValue) filter.Add("_id", new BsonDocument("$lt", beforeId.Value));
            var scoped = new BsonDocument("$and", new BsonArray(queries.Select(query => ScopeQuery.Create(query).Render(new RenderArgs<BsonDocument>(BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry)))));
            BsonDocument[] pipeline = [new("$match", filter), new("$set", new BsonDocument("scopeCount", new BsonDocument("$size", "$scopes"))), new("$unwind", "$scopes"),
                new("$replaceRoot", new BsonDocument("newRoot", new BsonDocument("$mergeObjects", new BsonArray { "$scopes", new BsonDocument { { "declarationId", "$_id" }, { "scopeCount", "$scopeCount" } } }))),
                new("$match", scoped), new("$group", new BsonDocument { { "_id", "$declarationId" }, { "matches", new BsonDocument("$sum", 1) }, { "expected", new BsonDocument("$first", "$scopeCount") } }),
                new("$match", new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$matches", "$expected" }))), new("$sort", new BsonDocument("_id", -1)), new("$limit", 26)];
            var selected = await records.Rows(B.Heads).Aggregate<BsonDocument>(tx, pipeline).ToListAsync(token);
            var result = ImmutableArray.CreateBuilder<PaymentDraftView>(); var scopes = new BsonArray();
            foreach (var row in selected) {
                var head = await ReviewHeadAsync(actor, authority, tx, row["_id"].AsInt64, token);
                result.Add(await ViewAsync(tx, head, await records.VersionAsync(tx, head["versionId"].AsInt64, token), token));
                await records.TouchAsync(tx, head, token); scopes.AddRange(head["scopes"].AsBsonArray);
            }
            await ReviewAuditAsync(actor, tx, "billing.payment.review.list", selected.Select(row => row["_id"].AsInt64), metadata, token);
            await ReviewFenceAsync(actor, authority, tx, scopes, token); return new PaymentDraftPage([.. result.Take(25)], selected.Count > 25);
        }, ct);
    }
    internal async Task<T> WithReviewEvidenceAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || target.Kind != EvidenceTargetKind.PaymentDeclaration || expectedRevision is not > 0 || reference is null) throw B.Invalid();
        var authority = await authorization.LoadAsync(actor, tx, ct); var head = await ReviewHeadAsync(actor, authority, tx, target.Id, ct);
        if (head["revision"] != expectedRevision.Value) throw PaymentDraftRecords.Changed();
        var submitted = await new PaymentSubmissionRecords(database).ReadAsync(tx, head["submissionId"].AsInt64, ct);
        if (!InvoiceRecords.Equal(submitted["evidence"], PaymentSubmissionBson.Reference(reference))) throw PaymentDraftRecords.Changed();
        var party = head["party"]; var owner = new GrantScope(GrantScopeKind.FulfillmentShop, party["fulfillmentOrganizationId"].AsInt64, ShopId: party["shopId"].AsInt64, ChannelId: party["channelId"].AsInt64);
        var result = await action(new(target, owner, head["revision"].AsInt64, null, null, submitted["reservationId"].AsInt64, submitted["_id"].AsInt64, submitted["uploaderId"].AsInt64), ct);
        await ReviewAuditAsync(actor, tx, "billing.payment.review.evidence", [target.Id], new("payment-review", "payment-review-evidence"), ct);
        await records.TouchAsync(tx, head, ct); await ReviewFenceAsync(actor, authority, tx, head["scopes"].AsBsonArray, ct); return result;
    }
}

// Read-only adapter created by Billing. It cannot admit uploads, recover writes or pin new submissions.
internal sealed class PaymentReviewTarget(IMongoDatabase database, PaymentDraftService declarations) : IPaymentEvidenceUploadTarget
{
    public IMongoDatabase Database => database;
    public Task<T> WithTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) => throw PaymentDraftBson.Invalid();
    public Task<PaymentEvidenceAssociation> RequireSubmissionAssociationAsync(IClientSessionHandle tx, ActorContext actor, long submissionId, EvidenceReadClaim claim, CancellationToken ct) => throw PaymentDraftBson.Invalid();
    public Task<T> WithReadTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference, Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) =>
        declarations.WithReviewEvidenceAsync(tx, actor, target, expectedRevision, reference, action, ct);
}
