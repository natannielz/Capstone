using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Catalog;

public sealed record CatalogPhotoStorageOptions(string Root, string StoreId, string[] TrustedOperatorSids);

/// <summary>CatalogSku photo authoring through one private evidence store and one owning catalog graph.</summary>
public sealed class CatalogPhotoWorkflow
{
    private readonly MongoUnitOfWork unit;
    private readonly IClock clock;
    private readonly EvidenceService evidence;
    private readonly CatalogPhotoService photos;

    public CatalogPhotoWorkflow(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        CatalogAuthoringService catalog, CatalogPhotoStorageOptions? storage = null)
        : this(database, unit, ids, clock, catalog, storage is null ? EvidenceStoreRegistration.Unavailable() :
            EvidenceStoreRegistration.OpenLocal(storage.Root, storage.StoreId, storage.TrustedOperatorSids)) { }

    internal CatalogPhotoWorkflow(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        CatalogAuthoringService catalog, EvidenceStoreRegistration registration)
    {
        this.unit = unit; this.clock = clock;
        evidence = new EvidenceService(database, unit, ids, clock, registration);
        photos = new CatalogPhotoService(database, unit, ids, clock, catalog, evidence);
    }

    private static EvidenceTargetRef Target(long skuId) => new(EvidenceTargetKind.CatalogSku, skuId);

    public Task<CatalogPrimaryPhotoCurrent> CurrentAsync(ActorContext actor, long skuId, CancellationToken ct = default) =>
        photos.CurrentAsync(actor, skuId, ct);

    public Task<ReservationResult> ReserveAsync(ActorContext actor, long skuId, long expectedSkuRevision,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) =>
        unit.ExecuteAsync((tx, token) => evidence.ReserveAsync(tx, actor,
            new ReserveCatalogImage(Target(skuId), expectedTargetRevision: expectedSkuRevision), metadata, token), ct);

    public Task<ReservationResult?> RecoverReservationAsync(ActorContext actor, long skuId, string key,
        CancellationToken ct = default) =>
        unit.ExecuteAsync((tx, token) => evidence.RecoverReservationAsync(tx, actor, Target(skuId), key, token), ct);

    public Task<SafeStatus> StatusAsync(ActorContext actor, long skuId, long reservationId, CancellationToken ct = default) =>
        unit.ExecuteAsync((tx, token) => evidence.GetStatusAsync(tx, actor, reservationId,
            new AuthorizationReadMetadata("evidence-status", "catalog-photo-status"), token, Target(skuId)), ct);

    public async Task<UploadOutcome> UploadAsync(ActorContext actor, long skuId, long reservationId,
        long expectedReservationRevision, Stream source, AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        _ = await StatusAsync(actor, skuId, reservationId, ct);
        return await evidence.UploadAsync(actor, reservationId, expectedReservationRevision, source, metadata, ct);
    }

    public async Task<UploadOutcome> RecoverUploadAsync(ActorContext actor, long skuId, long reservationId,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        _ = await StatusAsync(actor, skuId, reservationId, ct);
        return await evidence.RecoverAsync(actor, reservationId, metadata, ct);
    }

    public Task<ProtectedEvidenceRead> OpenStaffOriginalAsync(ActorContext actor, long skuId, long expectedSkuRevision,
        long reservationId, EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) =>
        evidence.OpenCatalogStaffOriginalAsync(actor, Target(skuId), expectedSkuRevision, reservationId, reference, metadata, ct);

    private sealed record SelectedLease(CatalogPhotoSelected Selected, EvidenceReadClaim Claim);
    private static bool SameCurrentPhoto(CatalogPhotoSelected before, CatalogPhotoSelected current) =>
        before.AssociationId == current.AssociationId && before.SkuId == current.SkuId &&
        before.PhotoTargetRevision == current.PhotoTargetRevision && before.DefinitionId == current.DefinitionId &&
        before.DefinitionVersion == current.DefinitionVersion && before.AssociationHash == current.AssociationHash &&
        before.Proof == current.Proof;

    private Task<T?> WithSelectedAsync<T>(ActorContext actor, string? publicSkuUuid, long? skuId,
        Func<IClientSessionHandle, CatalogPhotoSelected, CancellationToken, Task<T>> read,
        CancellationToken ct) where T : class =>
        publicSkuUuid is not null ? photos.WithBuyerSelectedAsync<T>(actor, publicSkuUuid, read, ct) :
        photos.WithStaffSelectedAsync<T>(actor, skuId ?? 0, read, ct);

    private async Task<ProtectedEvidenceRead?> OpenCurrentAsync(ActorContext actor, string? publicSkuUuid, long? skuId,
        AuthorizationReadMetadata metadata, CancellationToken ct)
    {
        if (metadata is null || metadata.Purpose != "catalog-photo-current")
            throw new DomainValidationException("invalid_catalog_purpose", "Current photo delivery requires its exact purpose");
        var started = Stopwatch.GetTimestamp();
        var selected = await WithSelectedAsync(actor, publicSkuUuid, skuId, async (tx, photo, token) =>
            new SelectedLease(photo, await evidence.AcquireCurrentCatalogPhotoAsync(tx, actor, photo, metadata, token)), ct);
        if (selected is null) return null;
        VerifiedOriginalHandle? handle = null;
        try {
            handle = await evidence.OpenCurrentCatalogOriginalAsync(selected.Claim, ct);
            var held = handle;
            var window = await WithSelectedAsync(actor, publicSkuUuid, skuId, async (tx, photo, token) => {
                if (!SameCurrentPhoto(selected.Selected, photo)) throw CatalogPhotoRecords.Broken();
                return await evidence.AuthorizeCurrentCatalogPhotoAsync(tx, actor, photo, selected.Claim, held, false, token);
            }, ct) ?? throw CatalogPhotoRecords.Broken();
            async Task<EvidenceReadWindow> RenewAsync(CancellationToken token) =>
                await WithSelectedAsync(actor, publicSkuUuid, skuId, async (tx, photo, inner) => {
                    if (!SameCurrentPhoto(selected.Selected, photo)) throw CatalogPhotoRecords.Broken();
                    return await evidence.AuthorizeCurrentCatalogPhotoAsync(tx, actor, photo, selected.Claim, held, true, inner);
                }, token) ?? throw CatalogPhotoRecords.Broken();
            var content = new EvidenceReadStream(held, clock, window,
                selected.Claim.RequestDuration - Stopwatch.GetElapsedTime(started), RenewAsync,
                () => evidence.ReleaseCurrentCatalogReadAsync(selected.Claim), ct);
            handle = null;
            return new ProtectedEvidenceRead(content, selected.Claim.Mime, selected.Claim.Original.Content.Length,
                selected.Claim.Reference);
        }
        catch {
            try { if (handle is not null) await handle.DisposeAsync(); } catch { }
            try { await evidence.ReleaseCurrentCatalogReadAsync(selected.Claim); } catch { }
            throw;
        }
    }

    public Task<ProtectedEvidenceRead?> OpenCurrentStaffAsync(ActorContext actor, long skuId,
        AuthorizationReadMetadata metadata, CancellationToken ct = default) =>
        OpenCurrentAsync(actor, null, skuId, metadata, ct);

    public Task<ProtectedEvidenceRead?> OpenBuyerAsync(ActorContext actor, string publicSkuUuid,
        AuthorizationReadMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(publicSkuUuid);
        return OpenCurrentAsync(actor, publicSkuUuid, null, metadata, ct);
    }

    public Task<CatalogPrimaryPhotoResult> AssociateAsync(ActorContext actor, CatalogAssociatePrimaryPhoto input,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) => photos.AssociateAsync(actor, input, metadata, ct);

    public Task<CatalogPrimaryPhotoResult?> LookupAssociationAsync(ActorContext actor, string key,
        CancellationToken ct = default) => photos.LookupAsync(actor, key, ct);

    // Only a CatalogVisibilityStore-selected SKU/definition inside its active read transaction may call this.
    // It proves metadata, not permission to stream bytes to a buyer.
    internal Task<CatalogImageProof?> RequireCurrentAssociationAsync(IClientSessionHandle tx,
        BsonDocument selectedSku, BsonDocument selectedDefinition, CancellationToken ct) =>
        photos.RequireCurrentAssociationAsync(tx, selectedSku, selectedDefinition, ct);
}
