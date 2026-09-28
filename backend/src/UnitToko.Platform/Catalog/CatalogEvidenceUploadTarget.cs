using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Platform.Catalog;

/// <summary>
/// Server-loaded catalog facts for an upload metadata callback. This snapshot is not an authority
/// capability and cannot authorize later uploads, associations or reads outside that callback.
/// </summary>
internal sealed class CatalogImageUploadTarget
{
    internal EvidenceTargetRef Target { get; }
    internal GrantScope Owner { get; }
    internal long Revision { get; }
    internal long? DefinitionId { get; }
    internal long? DefinitionVersion { get; }
    internal CatalogImageUploadTarget(EvidenceTargetRef target, GrantScope owner, long revision, long? definitionId, long? definitionVersion)
    {
        Target = target; Owner = owner; Revision = revision; DefinitionId = definitionId; DefinitionVersion = definitionVersion;
    }
}

public sealed partial class CatalogAuthoringService
{
    /// <summary>
    /// Joins the owning Documents command transaction for CatalogImage upload metadata only.
    /// The trusted callback must use this session, never commit it, and perform no storage I/O.
    /// Completion fences the exact current graph and authority; any failure aborts caller effects.
    /// This helper neither issues an upload capability nor supports association or buyer delivery.
    /// </summary>
    internal async Task<T> WithCatalogImageUploadTargetAsync<T>(IMongoDatabase commandDatabase, IClientSessionHandle tx,
        ActorContext actor, EvidenceTargetRef target, long? expectedRevision,
        Func<CatalogImageUploadTarget, CancellationToken, Task<T>> mutation, CancellationToken cancellationToken = default)
    {
        try
        {
            if (tx is null || !tx.IsInTransaction || commandDatabase is null ||
                !ReferenceEquals(tx.Client, database.Client) || !ReferenceEquals(commandDatabase.Client, database.Client) ||
                !commandDatabase.DatabaseNamespace.Equals(database.DatabaseNamespace))
                throw new InvalidOperationException("Catalog evidence requires the owning database and active caller transaction");
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(mutation);
            var context = await BeginAsync(actor, tx, cancellationToken);
            CatalogImageUploadTarget snapshot;
            if (target.Kind == EvidenceTargetKind.CatalogFamily)
            {
                var graph = await FamilyGraphAsync(context, target.Id, cancellationToken);
                RequireActive(graph.Family); RequireActive(graph.Category);
                if (expectedRevision is { } expected) CatalogRepository.Expected(graph.Family, expected);
                snapshot = new(target, RowOwner(graph.Family), graph.Family["revision"].AsInt64, null, null);
            }
            else if (target.Kind == EvidenceTargetKind.CatalogSku)
            {
                var graph = await SkuGraphAsync(context, target.Id, cancellationToken);
                RequireActive(graph.Sku); RequireActive(graph.Family); RequireActive(graph.Category);
                if (expectedRevision is { } expected) CatalogRepository.Expected(graph.Sku, expected);
                snapshot = new(target, RowOwner(graph.Sku), graph.Sku["revision"].AsInt64,
                    graph.Definition["_id"].AsInt64, graph.Definition["version"].AsInt64);
            }
            else throw CatalogRepository.Invalid("evidence_target_unsupported");
            var result = await mutation(snapshot, cancellationToken);
            await CompleteAsync(context, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            await AbortEvidenceCallerAsync(tx);
            throw;
        }
    }

    private static async Task AbortEvidenceCallerAsync(IClientSessionHandle? tx)
    {
        if (tx is null) return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (tx.IsInTransaction) await tx.AbortTransactionAsync(cleanup.Token);
            if (tx.IsInTransaction) tx.Dispose();
        }
        catch
        {
            // An unconfirmed abort must not leave a supplied handle usable for a later commit.
            // Preserve the original failure and its Mongo retry labels.
            try { tx.Dispose(); } catch { }
        }
    }
}
