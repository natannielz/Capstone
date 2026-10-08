using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Catalog;

public sealed partial class CatalogAuthoringService
{
    public async Task<CatalogIdentifierView> IdentifiersAsync(ActorContext actor, long skuId, AuthorizationReadMetadata audit, CancellationToken ct = default)
    {
        ValidateRead(audit); await CatalogIdentifierRecords.RequireStorageAsync(database, ct);
        var readId = await ids.NextAsync("auditEvents", ct);
        return await unit.ExecuteAsync(async (tx, token) =>
        {
            var context = await BeginAsync(actor, tx, token); var view = await SkuViewAsync(context, skuId, token);
            var current = await SkuGraphAsync(context, skuId, token);
            var capture = await new CatalogIdentifierGraph(database).ReadSkuAsync(tx, current.Sku, current.Definition, token);
            await capture.CompleteAsync(tx, token);
            await AuditReadAsync(context, readId.Value, "catalog.identifiers", audit, new BsonDocument("skuId", skuId), capture.Identifiers.Count, token);
            return new CatalogIdentifierView(view, capture.Identifiers.Select(CatalogIdentifierRecords.Read).ToImmutableArray());
        }, ct);
    }
    public async Task<CatalogIdentifierResult> AssignIdentifierAsync(ActorContext actor, CatalogAssignIdentifier input,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        // Actual approved PO source codes continue through their existing supplier owner. This
        // extension authors internal aliases and supplied GTINs; it cannot invent a supplier account.
        if (input.Kind is not ("skuCode" or "gtin") || input.SourceAccount is not null) throw CatalogIdentifierPolicy.Invalid();
        _ = CatalogIdentifierPolicy.Canonical(input.Kind, input.RawValue); _ = CatalogIdentifierPolicy.ExactText(input.ProvenanceReference, 500);
        var identifierId = await ids.NextAsync(CatalogIdentifierRecords.Collection, ct); var historyId = await ids.NextAsync(CatalogIdentifierRecords.History, ct);
        return await ExecuteIdentifierAsync(actor, "catalog.identifier.assign", input, input.SkuId.Value, input.ExpectedSkuRevision, metadata,
            async (context, sku, definition, capture, auditId, at, token) =>
            {
                if (input.DefinitionId.Value != definition["_id"].AsInt64) throw IdentifierChanged();
                var value = new CatalogIdentifier(identifierId, new(sku["organizationId"].AsInt64), input.SkuId, input.Kind,
                    CatalogIdentifierPolicy.Namespace(input.Kind, RowOwner(sku), input.SourceAccount), input.Kind == "skuCode" ? CatalogIdentifierPolicy.Canonical(input.Kind, input.RawValue) : input.RawValue,
                    CatalogIdentifierPolicy.Canonical(input.Kind, input.RawValue), false, at, null, 1, input.ProvenanceReference, input.DefinitionId);
                await RequireUnusedIdentifierAsync(context, value, token);
                var row = CatalogIdentifierRecords.Write(value, auditId);
                await repository.Rows(CatalogIdentifierRecords.Collection).InsertOneAsync(context.Tx, row, cancellationToken: token);
                await repository.Rows(CatalogIdentifierRecords.History).InsertOneAsync(context.Tx,
                    CatalogIdentifierRecords.AssignmentHistory(historyId.Value, row, "assigned", actor.UserId, at), cancellationToken: token);
                return new([], [row], value.Id, value.Revision, null, new HashSet<long>());
            }, ct);
    }
    public Task<CatalogIdentifierResult> RetireIdentifierAsync(ActorContext actor, CatalogRetireIdentifier input,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input); _ = CatalogIdentifierPolicy.ExactText(input.ProvenanceReference, 500);
        return RetireIdentifierCoreAsync(actor, input, metadata, ct);
    }
    private async Task<CatalogIdentifierResult> RetireIdentifierCoreAsync(ActorContext actor, CatalogRetireIdentifier input,
        AuthorizationCommandMetadata metadata, CancellationToken ct)
    {
        var historyId = await ids.NextAsync(CatalogIdentifierRecords.History, ct);
        return await ExecuteIdentifierAsync(actor, "catalog.identifier.retire", input, input.SkuId.Value, input.ExpectedSkuRevision, metadata,
            async (context, sku, definition, capture, auditId, at, token) =>
            {
                var before = capture.Identifiers.SingleOrDefault(row => row["_id"].AsInt64 == input.IdentifierId.Value) ?? throw IdentifierChanged();
                var previous = CatalogIdentifierRecords.Read(before);
                if (previous.Revision != input.ExpectedIdentifierRevision || previous.IsPrimary || previous.RetiredAt is not null) throw IdentifierChanged();
                var retired = previous with { Revision = CatalogRepository.Next(previous.Revision), RetiredAt = at, ProvenanceReference = input.ProvenanceReference };
                var after = CatalogIdentifierRecords.Write(retired, auditId);
                await ReplaceIdentifierAsync(context, before, after, token);
                await repository.Rows(CatalogIdentifierRecords.History).InsertOneAsync(context.Tx,
                    CatalogIdentifierRecords.AssignmentHistory(historyId.Value, after, "retired", actor.UserId, at), cancellationToken: token);
                return new([before], [after], retired.Id, retired.Revision, null, new HashSet<long> { retired.Id.Value });
            }, ct);
    }
    public async Task<CatalogIdentifierResult> CorrectSkuCodeAsync(ActorContext actor, CatalogCorrectSkuCode input,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input); var code = CatalogPolicy.NormalizeSkuCode(input.NewCode);
        _ = CatalogIdentifierPolicy.ExactText(input.ProvenanceReference, 500);
        var identifierId = await ids.NextAsync(CatalogIdentifierRecords.Collection, ct);
        var aliasHistoryId = await ids.NextAsync(CatalogIdentifierRecords.History, ct); var primaryHistoryId = await ids.NextAsync(CatalogIdentifierRecords.History, ct);
        return await ExecuteIdentifierAsync(actor, "catalog.identifier.correct-sku", input, input.SkuId.Value, input.ExpectedSkuRevision, metadata,
            async (context, sku, definition, capture, auditId, at, token) =>
            {
                var before = capture.Identifiers.SingleOrDefault(row => row["_id"].AsInt64 == input.PrimaryIdentifierId.Value) ?? throw IdentifierChanged();
                var previous = CatalogIdentifierRecords.Read(before);
                if (!previous.IsPrimary || previous.Revision != input.ExpectedIdentifierRevision || code == previous.CanonicalValue) throw IdentifierChanged();
                var primary = new CatalogIdentifier(identifierId, previous.OrganizationId, previous.SkuId, "skuCode", "internal", code, code,
                    true, at, null, 1, input.ProvenanceReference, new(definition["_id"].AsInt64));
                await RequireUnusedIdentifierAsync(context, primary, token);
                var alias = previous with { IsPrimary = false, Revision = CatalogRepository.Next(previous.Revision),
                    DefinitionId = previous.DefinitionId ?? new LongId(definition["_id"].AsInt64), ProvenanceReference = input.ProvenanceReference };
                var aliasRow = CatalogIdentifierRecords.Write(alias, auditId); var primaryRow = CatalogIdentifierRecords.Write(primary, auditId);
                await ReplaceIdentifierAsync(context, before, aliasRow, token);
                await repository.Rows(CatalogIdentifierRecords.Collection).InsertOneAsync(context.Tx, primaryRow, cancellationToken: token);
                await repository.Rows(CatalogIdentifierRecords.History).InsertManyAsync(context.Tx,
                    [CatalogIdentifierRecords.AssignmentHistory(aliasHistoryId.Value, aliasRow, "aliased", actor.UserId, at),
                     CatalogIdentifierRecords.AssignmentHistory(primaryHistoryId.Value, primaryRow, "assigned", actor.UserId, at)], cancellationToken: token);
                return new([before], [aliasRow, primaryRow], primary.Id, primary.Revision, code, new HashSet<long> { alias.Id.Value });
            }, ct);
    }
    private sealed record IdentifierChange(BsonDocument[] Before, BsonDocument[] After, LongId IdentifierId, long Revision,
        string? PrimaryCode, IReadOnlySet<long> Written);
    private async Task<CatalogIdentifierResult> ExecuteIdentifierAsync(ActorContext actor, string operation, object input, long skuId, long expectedSkuRevision,
        AuthorizationCommandMetadata metadata,
        Func<Context, BsonDocument, BsonDocument, CatalogIdentifierGraph.Capture, long, DateTimeOffset, CancellationToken, Task<IdentifierChange>> write,
        CancellationToken ct)
    {
        ValidateCommand(metadata); if (skuId <= 0 || expectedSkuRevision <= 0) throw CatalogIdentifierPolicy.Invalid();
        await CatalogIdentifierRecords.RequireStorageAsync(database, ct); var hash = RequestHash(input, metadata.Reason.Trim());
        var auditId = await ids.NextAsync("auditEvents", ct);
        return await unit.ExecuteAsync(async (tx, token) =>
        {
            var context = await BeginAsync(actor, tx, token);
            var receipt = await repository.Rows("idempotencyRecords").Find(tx,
                F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", metadata.IdempotencyKey)).SingleOrDefaultAsync(token);
            if (receipt is not null)
            {
                var result = await IdentifierReceiptAsync(context, receipt, operation, metadata.IdempotencyKey, token);
                if (receipt["bodyHash"].AsString != hash) throw new PersistenceConflictException("idempotency_key_reused", "Original identifier key was used with different input");
                await CompleteAsync(context, token); return result;
            }
            var graph = await SkuGraphAsync(context, skuId, token); CatalogRepository.Expected(graph.Sku, expectedSkuRevision);
            RequireActive(graph.Sku); RequireActive(graph.Family); RequireActive(graph.Category);
            var captured = await new CatalogIdentifierGraph(database).ReadSkuAsync(tx, graph.Sku, graph.Definition, token); var at = Now();
            var change = await write(context, graph.Sku, graph.Definition, captured, auditId.Value, at, token);
            var updated = graph.Sku.DeepClone().AsBsonDocument; updated["revision"] = CatalogRepository.Next(graph.Sku["revision"].AsInt64);
            // The stored SKU code is part of existing immutable commercial/count provenance.
            // Correction changes the explicit registry primary, never that stable identity code.
            await SaveAsync(context, "skus", graph.Sku, updated, token);
            var saved = new CatalogIdentifierResult(new(skuId), updated["revision"].AsInt64, change.IdentifierId, change.Revision, at);
            var resultRow = CatalogIdentifierEvidence.Result(saved);
            var audit = new BsonDocument
            {
                { "_id", auditId.Value }, { "schemaVersion", 1 }, { "kind", "catalog.identifier.command" }, { "actorId", actor.UserId },
                { "actorGrantVersion", actor.GrantVersion }, { "operation", operation }, { "bodyHash", hash }, { "reason", metadata.Reason.Trim() },
                { "correlationId", metadata.CorrelationId }, { "createdAt", at.UtcDateTime }, { "owner", CatalogRepository.Owner(RowOwner(graph.Sku)) },
                { "skuBefore", graph.Sku }, { "skuAfter", updated }, { "definition", graph.Definition },
                { "identifiersBefore", new BsonArray(change.Before) }, { "identifiersAfter", new BsonArray(change.After) }, { "result", resultRow }
            };
            _ = CatalogIdentifierEvidence.RequireAudit(audit);
            await repository.Rows("auditEvents").InsertOneAsync(tx, audit, cancellationToken: token);
            await repository.Rows("idempotencyRecords").InsertOneAsync(tx, new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "actorId", actor.UserId }, { "operation", operation },
                { "key", metadata.IdempotencyKey }, { "bodyHash", hash }, { "createdAt", at.UtcDateTime }, { "result", resultRow.DeepClone() },
                { "catalogIdentifier", new BsonDocument { { "schemaVersion", 1 }, { "auditId", auditId.Value } } }
            }, cancellationToken: token);
            await repository.Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "sourceKey", "catalog.identifier:" + auditId.Value },
                { "kind", "catalog.changed" }, { "createdAt", at.UtcDateTime }, { "status", "pending" }, { "attempts", 0 }, { "dueAt", at.UtcDateTime },
                { "payload", new BsonDocument { { "kind", "sku" }, { "id", skuId }, { "version", saved.SkuRevision } } }
            }, cancellationToken: token);
            await captured.CompleteAsync(tx, token, change.Written); await CompleteAsync(context, token); return saved;
        }, ct);
    }
    private async Task RequireUnusedIdentifierAsync(Context context, CatalogIdentifier value, CancellationToken ct)
    {
        if (await repository.Rows(CatalogIdentifierRecords.Collection).Find(context.Tx, F.Eq("organizationId", value.OrganizationId.Value) &
            F.Eq("kind", value.Kind) & F.Eq("namespace", value.Namespace) & F.Eq("canonicalValue", value.CanonicalValue)).AnyAsync(ct) ||
            value.Kind == "skuCode" && await repository.Rows("skus").Find(context.Tx, F.Eq("organizationId", value.OrganizationId.Value) & F.Eq("sku", value.CanonicalValue)).AnyAsync(ct))
            throw new PersistenceConflictException("catalog_identifier_conflict", "This identifier remains permanently reserved, including after retirement");
    }
    private async Task ReplaceIdentifierAsync(Context context, BsonDocument before, BsonDocument after, CancellationToken ct)
    {
        if ((await repository.Rows(CatalogIdentifierRecords.Collection).ReplaceOneAsync(context.Tx, CatalogIdentifierRecords.Exact(before), after, cancellationToken: ct)).ModifiedCount != 1)
            throw IdentifierChanged();
    }
    private static PersistenceConflictException IdentifierChanged() => new("catalog_identifier_changed", "Reload the SKU, identifier and exact package, then review again");
}
