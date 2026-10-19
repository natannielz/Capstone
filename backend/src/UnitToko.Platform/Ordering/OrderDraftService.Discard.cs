using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderDraftBson;
using D = UnitToko.Platform.Ordering.OrderDraftDiscardSchemas;

namespace UnitToko.Platform.Ordering;

public sealed record OrderDraftDiscardedView(LongId Id, long Revision, DateTimeOffset DiscardedAt, DateTimeOffset RestorableUntil, OrderDraftInput Input);
public sealed record OrderDraftDiscardedPage(ImmutableArray<OrderDraftDiscardedView> Items, bool HasMore);

/// <summary>Approved D02 rule for unsubmitted drafts: discard hides a draft, restore brings it back within 30 days,
/// and OrderDraftPurge deletes it afterwards. Only the draft's creator may discard, restore or list discarded drafts.</summary>
public sealed partial class OrderDraftService
{
    public async Task<BsonDocument> DiscardAsync(ActorContext actor, long draftId, long expectedRevision, AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        Metadata(metadata);
        if (draftId <= 0 || expectedRevision is <= 0 or long.MaxValue) throw new DomainValidationException("invalid_order_draft_revision", "Choose the current draft revision");
        await StorageAsync(ct);
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value; var body = new { draftId, expectedRevision };
        var hash = AuthorizationStore.CommandHash(body, metadata.Reason); BsonDocument? head = null;
        return await authorization.ExecuteCommandAsync(actor, B.DiscardOperation, metadata, body, async (snapshot, tx, token) => {
            head = await HeadAsync(actor, snapshot, tx, draftId, token);
            // The same key replays its original result; any other discard of an already discarded draft is a conflict.
            if (await DiscardRowAsync(tx, head, token) is { } existing && existing["requestKey"] != metadata.IdempotencyKey) throw Discarded();
            return Authorize(actor, snapshot, head);
        }, async (tx, token) => {
            var current = head ?? throw B.Invalid();
            if (current["revision"].AsInt64 != expectedRevision) throw Changed();
            if (await DiscardRowAsync(tx, current, token) is not null) throw B.Invalid();
            var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
            if (now == default || now < B.Time(current["updatedAt"])) throw B.Invalid();
            var row = new BsonDocument { { "_id", draftId }, { "schemaVersion", 1 }, { "kind", D.Kind }, { "discardedBy", actor.UserId },
                { "actorGrantVersion", actor.GrantVersion }, { "context", current["context"].DeepClone() }, { "revision", expectedRevision },
                { "discardedAt", now.UtcDateTime }, { "restorableUntil", now.Add(D.Window).UtcDateTime }, { "requestKey", metadata.IdempotencyKey },
                { "bodyHash", hash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId } };
            await Rows(D.Collection).InsertOneAsync(tx, row, cancellationToken: token);
            // The head fence makes a racing save of this draft conflict instead of landing on a discarded draft.
            await TouchAsync(tx, current, token);
            await Rows("auditEvents").InsertOneAsync(tx, DiscardAudit(row), cancellationToken: token);
            return DiscardResult(row);
        }, ct, async (snapshot, tx, token) => await authority.FenceAsync(actor, snapshot, tx, [B.Context((head ?? throw B.Invalid())["context"].AsBsonDocument)], token));
    }

    public async Task<BsonDocument> RestoreAsync(ActorContext actor, long draftId, AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        Metadata(metadata); if (draftId <= 0) throw B.Invalid(); await StorageAsync(ct);
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value; var body = new { draftId };
        var hash = AuthorizationStore.CommandHash(body, metadata.Reason); BsonDocument? head = null;
        return await authorization.ExecuteCommandAsync(actor, B.RestoreOperation, metadata, body, async (snapshot, tx, token) => {
            head = await HeadAsync(actor, snapshot, tx, draftId, token);
            return Authorize(actor, snapshot, head);
        }, async (tx, token) => {
            var current = head ?? throw B.Invalid();
            var discard = await DiscardRowAsync(tx, current, token) ?? throw new PersistenceConflictException("order_draft_not_discarded", "Draf ini tidak sedang dibuang");
            var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
            if (now >= B.Time(discard["restorableUntil"]))
                throw new PersistenceConflictException("order_draft_restore_expired", "Masa pemulihan draf sudah berakhir");
            if ((await Rows(D.Collection).DeleteOneAsync(tx, discard, cancellationToken: token)).DeletedCount != 1) throw Changed();
            await TouchAsync(tx, current, token);
            var result = new BsonDocument { { "draftId", draftId }, { "revision", current["revision"] }, { "restoredAt", now.UtcDateTime }, { "discardAuditId", discard["auditId"] } };
            await Rows("auditEvents").InsertOneAsync(tx, new BsonDocument { { "_id", auditId }, { "schemaVersion", 1 }, { "kind", "ordering.draft.restored" },
                { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "operation", B.RestoreOperation }, { "requestKey", metadata.IdempotencyKey },
                { "bodyHash", hash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "createdAt", now.UtcDateTime },
                { "context", current["context"].DeepClone() }, { "result", result.DeepClone() } }, cancellationToken: token);
            return result;
        }, ct, async (snapshot, tx, token) => await authority.FenceAsync(actor, snapshot, tx, [B.Context((head ?? throw B.Invalid())["context"].AsBsonDocument)], token));
    }

    public async Task<OrderDraftDiscardedPage> ListDiscardedAsync(ActorContext actor, OrderBuyingContext context, long? beforeId = null, int limit = 25, CancellationToken ct = default)
    {
        context.Validate(); if (beforeId is <= 0 || limit is < 1 or > 25) throw B.Invalid("invalid_order_draft_page"); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            authority.Require(actor, snapshot, context);
            var filter = F.Eq("discardedBy", actor.UserId) & F.Eq("context.organizationId", context.OrganizationId.Value) & F.Eq("context.branchId", context.BranchId.Value) &
                F.Eq<BsonValue>("context.divisionId", context.DivisionId is { } division ? new BsonInt64(division.Value) : BsonNull.Value);
            if (beforeId.HasValue) filter &= F.Lt("_id", beforeId.Value);
            var rows = await Rows(D.Collection).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(limit + 1).ToListAsync(token);
            var result = ImmutableArray.CreateBuilder<OrderDraftDiscardedView>();
            foreach (var candidate in rows.Take(limit)) {
                var head = await HeadAsync(actor, snapshot, tx, candidate["_id"].AsInt64, token);
                var discard = await DiscardRowAsync(tx, head, token) ?? throw B.Invalid();
                var version = await VersionAsync(tx, head["versionId"].AsInt64, token); await TouchAsync(tx, head, token);
                result.Add(new(new(head["_id"].AsInt64), head["revision"].AsInt64, B.Time(discard["discardedAt"]), B.Time(discard["restorableUntil"]),
                    B.Input(version["input"].AsBsonDocument)));
            }
            await authority.FenceAsync(actor, snapshot, tx, [context], token);
            return new OrderDraftDiscardedPage(result.ToImmutable(), rows.Count > limit);
        }, ct);
    }

    private CommandAuthorization Authorize(ActorContext actor, AuthorizationSnapshot snapshot, BsonDocument head)
    {
        var resource = B.Context(head["context"].AsBsonDocument).Resource(actor.UserId);
        return new(ScopePolicy.Authorize(actor, snapshot, Permissions.OrderSubmit, resource, clock.UtcNow),
            ScopePolicy.RequiredNodes(resource, snapshot.Directory), Permissions.OrderSubmit, new(resource));
    }

    /// <summary>The draft's discard row, verified against the head it belongs to and its own audit event; null when the draft is active.</summary>
    private async Task<BsonDocument?> DiscardRowAsync(IClientSessionHandle tx, BsonDocument head, CancellationToken ct)
    {
        var row = await Rows(D.Collection).Find(tx, F.Eq("_id", head["_id"])).SingleOrDefaultAsync(ct);
        if (row is null) return null;
        if (!await Rows(D.Collection).Find(tx, F.Eq("_id", head["_id"]) & D.Validator()).AnyAsync(ct) || row["discardedBy"] != head["createdBy"] ||
            row["context"] != head["context"] || row["revision"].AsInt64 > head["revision"].AsInt64) throw B.Invalid();
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        if (audit is null || !CatalogSchemas.SemanticEquals(audit, DiscardAudit(row))) throw B.Invalid();
        return row;
    }

    internal static BsonDocument DiscardResult(BsonDocument row) => new() { { "draftId", row["_id"] }, { "revision", row["revision"] },
        { "discardedAt", row["discardedAt"] }, { "restorableUntil", row["restorableUntil"] } };
    internal static BsonDocument DiscardAudit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 },
        { "kind", "ordering.draft.discarded" }, { "actorId", row["discardedBy"] }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "operation", B.DiscardOperation }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] },
        { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["discardedAt"] },
        { "context", row["context"].DeepClone() }, { "result", DiscardResult(row) } };
    private static PersistenceConflictException Discarded() => new("order_draft_discarded", "Draf ini sudah dibuang; pulihkan dulu untuk mengubahnya");
}
