using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Modules.Documents;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class CatalogPhotoTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Committed_photo_outlives_upload_window_but_current_buyer_entitlement_and_read_policy_remain_required()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture, sku: true);
        await data.Rows("evidenceStorePolicies").UpdateOneAsync(new BsonDocument("storeId", data.StoreId),
            Builders<BsonDocument>.Update.Set("reservationDurationMs", 30000L).Set("writerDurationMs", 30000L));
        var workflow = new CatalogPhotoWorkflow(data.DB, data.Unit, new(data.DB), data.Catalog.Clock,
            data.Catalog.Service, data.Registration);
        var sku = await data.Catalog.RowAsync("skus", data.Target.Id);
        await data.Catalog.AssortmentsAsync();
        await data.Catalog.Service.SetPublicationsAsync(data.Actor,
            new(new(data.Target.Id), sku["revision"].AsInt64, [new(501)], [new(new(501), 1)]),
            CatalogAuthoringTestData.Metadata("photo-initial-publication"));
        sku = await data.Catalog.RowAsync("skus", data.Target.Id);
        var reservation = await workflow.ReserveAsync(data.Actor, data.Target.Id, sku["revision"].AsInt64,
            EvidenceAdmissionTestData.Meta("photo-reserve"));
        var bytes = ImageDecoderTests.Png(13, 7);
        using (var original = new MemoryStream(bytes))
            _ = await workflow.UploadAsync(data.Actor, data.Target.Id, reservation.ReservationId, reservation.Revision,
                original, EvidenceAdmissionTestData.Meta("photo-upload"));
        var processor = EvidenceValidationTests.Processor(data, new EvidenceValidationTests.FixtureScanner(signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1)));
        await EvidenceValidationTests.Policy(data, processor.PipelineId);
        Assert.Equal("completed", await processor.RunOnceAsync());
        await ReadPolicyAsync(data);
        var reference = Assert.IsType<EvidenceRef>((await workflow.StatusAsync(data.Actor, data.Target.Id, reservation.ReservationId)).Reference);
        var associated = await workflow.AssociateAsync(data.Actor,
            new(new(data.Target.Id), sku["revision"].AsInt64, 0, reservation.ReservationId, reference),
            CatalogAuthoringTestData.Metadata("photo-associate"));
        var current = await workflow.CurrentAsync(data.Actor, data.Target.Id);
        Assert.Equal(associated.AssociationId, current.Photo?.AssociationId);
        Assert.Equal(1, current.PhotoRevision);
        Assert.True(current.Photo?.MatchesCurrentDefinition);
        data.Catalog.Clock.UtcNow = reservation.ExpiresAt.AddSeconds(1);
        var read = new AuthorizationReadMetadata("catalog-photo-current", "synthetic-current-photo");
        await using (var buyer = Assert.IsType<ProtectedEvidenceRead>(await workflow.OpenBuyerAsync(data.Catalog.Buyer,
            sku["publicId"].AsString, read))) {
            Assert.Equal("image/png", buyer.Mime);
            using var received = new MemoryStream(); await buyer.Content.CopyToAsync(received);
            Assert.Equal(bytes, received.ToArray());
        }
        await using (var staff = Assert.IsType<ProtectedEvidenceRead>(await workflow.OpenCurrentStaffAsync(data.Actor,
            data.Target.Id, read))) {
            using var received = new MemoryStream(); await staff.Content.CopyToAsync(received);
            Assert.Equal(bytes, received.ToArray());
        }
        await data.Rows("catalogEntitlements").UpdateOneAsync(new BsonDocument("_id", 10001L),
            Builders<BsonDocument>.Update.Set("status", "revoked"));
        var denied = await Record.ExceptionAsync(() => workflow.OpenBuyerAsync(data.Catalog.Buyer, sku["publicId"].AsString, read));
        Assert.True(denied is AuthorizationDeniedException or null);
        if (denied is null) Assert.Null(await workflow.OpenBuyerAsync(data.Catalog.Buyer, sku["publicId"].AsString, read));
        await data.Rows("evidenceReadPolicies").UpdateOneAsync(new BsonDocument("storeId", data.StoreId),
            Builders<BsonDocument>.Update.Set("active", false));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => workflow.OpenCurrentStaffAsync(data.Actor, data.Target.Id, read));
        await data.Rows("evidenceReadPolicies").UpdateOneAsync(new BsonDocument("storeId", data.StoreId),
            Builders<BsonDocument>.Update.Set("active", true));
        var head = await data.RowAsync(CatalogPhotoRecords.Heads, data.Target.Id);
        await data.Rows(CatalogPhotoRecords.Heads).DeleteOneAsync(new BsonDocument("_id", data.Target.Id));
        var missingHead = await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            workflow.OpenCurrentStaffAsync(data.Actor, data.Target.Id, read));
        Assert.Equal("catalog_photo_integrity_unavailable", missingHead.Code);
        await data.Rows(CatalogPhotoRecords.Heads).InsertOneAsync(head);
        var validation = await data.RowAsync("evidenceValidationResults", reservation.VersionRowId);
        await data.Rows("evidenceValidationResults").UpdateOneAsync(new BsonDocument("_id", reservation.VersionRowId),
            Builders<BsonDocument>.Update.Set("resultHash", new string('f', 64)));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => workflow.OpenCurrentStaffAsync(data.Actor, data.Target.Id, read));
        await data.Rows("evidenceValidationResults").ReplaceOneAsync(new BsonDocument("_id", reservation.VersionRowId), validation);
        await data.Rows("catalogEntitlements").UpdateOneAsync(new BsonDocument("_id", 10001L),
            Builders<BsonDocument>.Update.Set("status", "active"));
        _ = await data.Catalog.Service.ReviseDefinitionAsync(data.Actor,
            new(new(data.Target.Id), sku["revision"].AsInt64, CatalogAuthoringTestData.Definition(label: "Karton")),
            CatalogAuthoringTestData.Metadata("photo-definition-change"));
        var afterDefinition = await workflow.CurrentAsync(data.Actor, data.Target.Id);
        Assert.Equal(1, afterDefinition.PhotoRevision);
        Assert.False(afterDefinition.Photo?.MatchesCurrentDefinition);
        Assert.Null(await workflow.OpenBuyerAsync(data.Catalog.Buyer, sku["publicId"].AsString, read));
        Assert.Equal(0, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }

    private static async Task ReadPolicyAsync(EvidenceAdmissionTestData data) =>
        await data.Rows("evidenceReadPolicies").InsertOneAsync(new BsonDocument {
            { "_id", (await new IdAllocator(data.DB).NextAsync("evidenceReadPolicies")).Value },
            { "schemaVersion", 1 }, { "storeId", data.StoreId }, { "active", true }, { "revision", 1L }, { "fence", 0L },
            { "createdAt", data.Catalog.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Catalog.Clock.UtcNow.UtcDateTime },
            { "maximumBytes", 1024L }, { "verificationDurationMs", 10000L }, { "leaseDurationMs", 5000L },
            { "requestDurationMs", 60000L }, { "maximumValidationAgeMs", 86400000L },
            { "maximumSignatureAgeMs", 604800000L } });

    [WindowsStorageFact]
    public async Task Clean_private_sku_photo_associates_once_and_recovery_requires_its_unchanged_owner()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture, sku: true);
        var initialSku = await data.Catalog.RowAsync("skus", data.Target.Id);
        await data.Catalog.AssortmentsAsync();
        await data.Catalog.Service.SetPublicationsAsync(data.Actor,
            new(new(data.Target.Id), initialSku["revision"].AsInt64, [new(501)], [new(new(501), 1)]),
            CatalogAuthoringTestData.Metadata("photo-replacement-publication"));
        var sku = await data.Catalog.RowAsync("skus", data.Target.Id);
        var workflow = new CatalogPhotoWorkflow(data.DB, data.Unit, new(data.DB), data.Catalog.Clock,
            data.Catalog.Service, data.Registration);
        var reservation = await workflow.ReserveAsync(data.Actor, data.Target.Id, sku["revision"].AsInt64,
            EvidenceAdmissionTestData.Meta("photo-one-reserve"));
        using (var original = new MemoryStream(ImageDecoderTests.Png(4, 3)))
            _ = await workflow.UploadAsync(data.Actor, data.Target.Id, reservation.ReservationId, reservation.Revision,
                original, EvidenceAdmissionTestData.Meta("photo-one-upload"));
        var processor = EvidenceValidationTests.Processor(data,
            new EvidenceValidationTests.FixtureScanner(signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1)));
        await EvidenceValidationTests.Policy(data, processor.PipelineId);
        Assert.Equal("completed", await processor.RunOnceAsync());
        await ReadPolicyAsync(data);
        var reference = Assert.IsType<UnitToko.Modules.Documents.EvidenceRef>((await workflow.StatusAsync(data.Actor,
            data.Target.Id, reservation.ReservationId)).Reference);
        var service = new CatalogPhotoService(data.DB, data.Unit, new(data.DB), data.Catalog.Clock, data.Catalog.Service, data.Service);
        var input = new CatalogAssociatePrimaryPhoto(new(data.Target.Id), sku["revision"].AsInt64, 0, reservation.ReservationId, reference);
        var metadata = CatalogAuthoringTestData.Metadata("photo-one");
        var result = await service.AssociateAsync(data.Actor, input, metadata);
        Assert.Equal(data.Target.Id, result.SkuId.Value);
        Assert.Equal(sku["currentDefinitionId"].AsInt64, result.DefinitionId.Value);
        Assert.Equal(result, await service.AssociateAsync(data.Actor, input, metadata));
        Assert.Equal(result, await service.LookupAsync(data.Actor, metadata.IdempotencyKey));
        Assert.Single(await data.Rows(CatalogPhotoRecords.Photos).Find(new BsonDocument()).ToListAsync());
        Assert.Equal(1, await data.Rows(CatalogPhotoRecords.Audits).CountDocumentsAsync(new BsonDocument()));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.AssociateAsync(data.Actor,
            input with { ExpectedSkuRevision = input.ExpectedSkuRevision + 1 }, metadata));
        var latestSku = await data.Catalog.RowAsync("skus", data.Target.Id);
        var secondReservation = await workflow.ReserveAsync(data.Actor, data.Target.Id,
            latestSku["revision"].AsInt64, EvidenceAdmissionTestData.Meta("reserve-second-photo"));
        var replacementBytes = ImageDecoderTests.Png(17, 7);
        using (var original = new MemoryStream(replacementBytes))
            _ = await workflow.UploadAsync(data.Actor, data.Target.Id, secondReservation.ReservationId,
                secondReservation.Revision, original, EvidenceAdmissionTestData.Meta("upload-second-photo"));
        Assert.Equal("completed", await processor.RunOnceAsync());
        var secondReference = Assert.IsType<UnitToko.Modules.Documents.EvidenceRef>((await workflow.StatusAsync(data.Actor,
            data.Target.Id, secondReservation.ReservationId)).Reference);
        var replacement = await service.AssociateAsync(data.Actor,
            input with { ExpectedPhotoRevision = 1, ReservationId = secondReservation.ReservationId, Reference = secondReference },
            CatalogAuthoringTestData.Metadata("photo-two"));
        Assert.Equal(2, replacement.PhotoRevision);
        Assert.Equal(replacement.AssociationId.Value, (await data.RowAsync(CatalogPhotoRecords.Heads, data.Target.Id))["currentPhotoId"].AsInt64);
        await using (var current = Assert.IsType<ProtectedEvidenceRead>(await workflow.OpenBuyerAsync(data.Catalog.Buyer,
            sku["publicId"].AsString, new("catalog-photo-current", "synthetic-photo-replacement")))) {
            using var received = new MemoryStream(); await current.Content.CopyToAsync(received);
            Assert.Equal(replacementBytes, received.ToArray());
        }
        Assert.Equal(result, await service.LookupAsync(data.Actor, metadata.IdempotencyKey));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.AssociateAsync(data.Actor,
            input with { ReservationId = secondReservation.ReservationId, Reference = secondReference },
            CatalogAuthoringTestData.Metadata("stale-photo-head")));
        var head = await data.Catalog.RowAsync(CatalogPhotoRecords.Heads, data.Target.Id);
        await data.Rows(CatalogPhotoRecords.Heads).DeleteOneAsync(new BsonDocument("_id", data.Target.Id));
        var missingHead = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.LookupAsync(data.Actor, metadata.IdempotencyKey));
        Assert.Equal("catalog_photo_integrity_unavailable", missingHead.Code);
        await data.Rows(CatalogPhotoRecords.Heads).InsertOneAsync(head);
        Assert.Equal(0, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));

        var validation = await data.Rows("evidenceValidationResults").Find(new BsonDocument("_id", reservation.VersionRowId)).SingleAsync();
        await data.Rows("evidenceValidationResults").DeleteOneAsync(new BsonDocument("_id", reservation.VersionRowId));
        var missing = await Assert.ThrowsAsync<PersistenceConflictException>(() => service.LookupAsync(data.Actor, metadata.IdempotencyKey));
        Assert.Equal("evidence_validation_integrity", missing.Code);
        await data.Rows("evidenceValidationResults").InsertOneAsync(validation);
        Assert.Equal(result, await service.LookupAsync(data.Actor, metadata.IdempotencyKey));
    }

    [WindowsStorageFact]
    public async Task Unscanned_or_foreign_photo_cannot_create_an_association()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture, sku: true);
        var reservation = await EvidenceValidationTests.Upload(data);
        var reference = Assert.IsType<UnitToko.Modules.Documents.EvidenceRef>((await data.StatusAsync(reservation.ReservationId)).Reference);
        var sku = await data.Catalog.RowAsync("skus", data.Target.Id);
        var service = new CatalogPhotoService(data.DB, data.Unit, new(data.DB), data.Catalog.Clock, data.Catalog.Service, data.Service);
        var input = new CatalogAssociatePrimaryPhoto(new(data.Target.Id), sku["revision"].AsInt64, 0, reservation.ReservationId, reference);
        var unscanned = await Assert.ThrowsAsync<PersistenceConflictException>(() => service.AssociateAsync(data.Actor, input, CatalogAuthoringTestData.Metadata("unscanned")));
        Assert.Equal("catalog_photo_upload_again", unscanned.Code);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.AssociateAsync(data.Catalog.Other, input, CatalogAuthoringTestData.Metadata("foreign")));
        Assert.Equal(0, await data.Rows(CatalogPhotoRecords.Photos).CountDocumentsAsync(new BsonDocument()));
    }
}
