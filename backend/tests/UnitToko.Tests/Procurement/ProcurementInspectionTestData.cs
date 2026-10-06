using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using StorageRoot = UnitToko.Tests.Documents.PrivateEvidenceStorageTests.StorageRoot;

namespace UnitToko.Tests.Procurement;

// Genuine receiving, publication, queued worker, decoder, lifecycle and pin owners.
// The loopback ClamAV protocol server, signatures and approval references below are
// explicitly synthetic test inputs, never evidence of production antivirus acceptance.
[SupportedOSPlatform("windows")]
internal sealed class ProcurementInspectionTestData(ProcurementReceivingTestData receiving) : IAsyncDisposable
{
    internal const string Drafts = "procurementInspectionDrafts", Submissions = "procurementInspectionSubmissions", Heads = "procurementInspectionHeads";
    internal const string DraftOperation = "procurement.inspection.draft", SubmitOperation = "procurement.inspection.submit";
    internal const string Consumer = "synthetic-inspection-video-worker";
    internal const string HostReference = "synthetic-test-only-video-host-not-production-acceptance";
    internal const string ScannerReference = "synthetic-test-only-loopback-protocol-not-real-antivirus";
    internal const long MaximumBytes = 16 * 1024 * 1024;
    internal static readonly string StoreId = new('1', 64);
    internal ProcurementReceivingTestData Receiving { get; } = receiving;
    internal StoreTestData Base => Receiving.Base;
    internal IMongoDatabase Database => Receiving.Database;
    internal MongoUnitOfWork Unit => Receiving.Unit;
    internal MutableAuthorizationClock Clock => Receiving.Clock;
    internal StorageRoot Files = null!;
    internal EvidenceStoreRegistration Registration = null!;
    internal LocalScannerTests.Server Scanner = null!;
    internal VideoValidationRuntime Runtime = null!;
    internal ActorContext Actor = null!, Reader = null!, EvidenceReader = null!, OtherInspector = null!, Outsider = null!;
    internal BsonDocument Notice = null!, Intake = null!, Draft = null!;
    internal long NoticeId => Notice["noticeId"].AsInt64;
    internal long IntakeId => Intake["receiptId"].AsInt64;
    internal long DraftId => Draft["draftId"].AsInt64;
    internal ReservationResult Reservation = null!;
    internal EvidenceRef Reference = null!;
    internal byte[] Bytes = [];
    internal ProcurementInspectionService Service => new(Database, Unit, new(Database), Clock, Receiving.Procurement.Catalog, Receiving.Orders.Service, Receiving.Service);
    internal ProcurementInspectionEvidenceService Evidence => new(Database, Unit, new(Database), Clock, Service, Registration);
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic observed unboxing; all goods remain quarantined", "procurement-inspection-test");
    internal static AuthorizationReadMetadata ReadMetadata => new("procurement-inspection-evidence-view", "synthetic-private-unboxing-review");
    internal ProcurementInspectionInput Input(string reference = "SYNTHETIC-INSPECTION-001", int containers = 2) => new(
        ProcurementRequestTestData.Location, NoticeId, IntakeId, AccountingCalendar.BusinessDate(Clock.UtcNow), reference, containers,
        [new(Receiving.Procurement.Sku.Id.Value, Receiving.Procurement.Sku.CurrentDefinitionId!.Value.Value, Receiving.Procurement.Sku.DefinitionVersion!.Value,
            3, 3, 1, 1, 2, "SYNTHETIC-LOT-001", AccountingCalendar.BusinessDate(Clock.UtcNow).AddDays(-1), "SYNTHETIC-QUARANTINE-BAY-A",
            "Observed damaged, expired and opened flags overlap; all packages remain held")], "Two actual containers opened; quantities are physical observations only");
    internal static async Task<ProcurementInspectionTestData> CreateAsync(MongoFixture fixture, ProcurementRoute route = ProcurementRoute.Ddo,
        bool onReceipt = false, bool createDraft = true, string media = "mp4", bool aboveImageCeiling = false, decimal requiredAdvance = 0, bool boundStock = false) {
        var receiving = await ProcurementReceivingTestData.CreateAsync(fixture, route, onReceipt, requiredAdvance, boundStock); var data = new ProcurementInspectionTestData(receiving);
        try {
            data.Notice = await receiving.NoticeAsync(); data.Intake = await receiving.IntakeAsync(data.NoticeId);
            var scope = ProcurementRequestTestData.Scope;
            data.Actor = await data.Base.AddActorAsync(690, scope, Permissions.ProcurementRead, Permissions.ProcurementInspect, Permissions.ProcurementInspectionEvidenceRead);
            data.Reader = await data.Base.AddActorAsync(691, scope, Permissions.ProcurementRead);
            data.EvidenceReader = await data.Base.AddActorAsync(692, scope, Permissions.ProcurementRead, Permissions.ProcurementInspectionEvidenceRead);
            data.OtherInspector = await data.Base.AddActorAsync(693, scope, Permissions.ProcurementRead, Permissions.ProcurementInspect, Permissions.ProcurementInspectionEvidenceRead);
            if (boundStock) {
                await data.Base.AddGrantAsync(data.Actor, 7284, scope, [Permissions.StockRead]);
                await data.Base.AddGrantAsync(data.OtherInspector, 7285, scope, [Permissions.StockRead]);
            }
            data.Outsider = await data.Base.AddActorAsync(694, new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: receiving.Procurement.ForeignShopId,
                WarehouseId: receiving.Procurement.ForeignWarehouseId, ChannelId: 103), Permissions.ProcurementRead, Permissions.ProcurementInspect, Permissions.ProcurementInspectionEvidenceRead);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Actor.UserId, data.Reader.UserId, data.EvidenceReader.UserId, data.OtherInspector.UserId, data.Outsider.UserId);
            data.Files = new(); data.Registration = EvidenceStoreRegistration.OpenLocal(data.Files.Path, StoreId, StorageRoot.TrustedOperators);
            Assert.True(data.Registration.Store is not null, data.Registration.Reason);
            data.Bytes = File.ReadAllBytes(RequiredPath(media == "webm" ? "UNITTOKO_VIDEO_FIXTURE_WEBM" : "UNITTOKO_VIDEO_FIXTURE_MP4"));
            Assert.InRange(data.Bytes.Length, 1, 1_000_000);
            if (aboveImageCeiling) data.Bytes = AddFreeBox(data.Bytes);
            var storePolicy = EvidenceAdmissionSchemaTests.Policy(MaximumBytes, 10, MaximumBytes * 30); storePolicy["storeId"] = StoreId;
            storePolicy["_id"] = (await new IdAllocator(data.Database).NextAsync("evidenceVideoStorePolicies")).Value;
            await data.Rows("evidenceVideoStorePolicies").InsertOneAsync(storePolicy);
            // Independently configured image policy retains its original 10 MiB ceiling.
            var imagePolicy = EvidenceAdmissionSchemaTests.Policy(10 * 1024 * 1024, 10, 300 * 1024 * 1024); imagePolicy["storeId"] = StoreId;
            imagePolicy["_id"] = (await new IdAllocator(data.Database).NextAsync("evidenceStorePolicies")).Value;
            await data.Rows("evidenceStorePolicies").InsertOneAsync(imagePolicy);
            await data.Rows("evidenceVideoReadPolicies").InsertOneAsync(new BsonDocument {
                { "_id", (await new IdAllocator(data.Database).NextAsync("evidenceVideoReadPolicies")).Value }, { "schemaVersion", 1 }, { "storeId", StoreId },
                { "active", true }, { "revision", 1L }, { "fence", 0L }, { "createdAt", data.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Clock.UtcNow.UtcDateTime },
                { "maximumBytes", MaximumBytes }, { "verificationDurationMs", 10000L }, { "leaseDurationMs", 5000L }, { "requestDurationMs", 60000L },
                { "maximumValidationAgeMs", 86400000L }, { "maximumSignatureAgeMs", 604800000L } });
            var version = "ClamAV 1.5.1/30000/" + data.Clock.UtcNow.AddHours(-1).UtcDateTime.ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture);
            data.Scanner = new(version);
            data.Runtime = VideoValidationRuntime.Create(data.Database, data.Unit, new(data.Database), data.Clock, new(data.Files.Path, StoreId, StorageRoot.TrustedOperators,
                Consumer, HostReference, new(MaximumBytes, 128, 128, 16384, TimeSpan.FromSeconds(1), 100, 2, 48000, 100000, 100),
                new(RequiredPath("UNITTOKO_VIDEO_FFPROBE"), RequiredPath("UNITTOKO_VIDEO_FFMPEG"),
                    "b364c7974d2aa8195f7e97064e658fd7f738fe4e78a6db092a3bf6cd5cc9488c", "c61984150fbe7699cb7387f23b9e71c759d0b097dd7a61bf670b555e054d6afe",
                    256 * 1024 * 1024, TimeSpan.FromSeconds(20), 256 * 1024),
                new(data.Scanner.Port, "1.5.1", 30000, TimeSpan.FromDays(2), MaximumBytes, TimeSpan.FromSeconds(20)), MaximumBytes, ScannerReference,
                new([VideoValidationRecords.Kind], 3, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2))));
            await data.Rows(VideoValidationRecords.Policies).InsertOneAsync(new BsonDocument {
                { "_id", (await new IdAllocator(data.Database).NextAsync(VideoValidationRecords.Policies)).Value }, { "schemaVersion", 1 }, { "consumerId", Consumer }, { "storeId", StoreId },
                { "pipelineId", data.Runtime.PipelineId }, { "active", true }, { "revision", 1L }, { "fence", 0L }, { "approvedAt", data.Clock.UtcNow.AddMinutes(-1).UtcDateTime },
                { "expiresAt", data.Clock.UtcNow.AddDays(1).UtcDateTime }, { "hostAcceptanceRef", HostReference }, { "clamdConfigurationAcceptanceRef", ScannerReference } });
            if (createDraft) data.Draft = await data.DraftAsync("inspection-draft");
            return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    internal async Task<BsonDocument> DraftAsync(string key, ProcurementInspectionInput? input = null) {
        input ??= Input(); var preview = await Service.PreviewAsync(Actor, input); return await Service.DraftAsync(Actor, input, preview["binding"].AsString, Meta(key));
    }
    internal async Task<(ReservationResult Reservation, EvidenceRef Reference)> UploadForAsync(long draftId, string key, byte[]? bytes = null) {
        var reservation = await Evidence.ReserveAsync(Actor, draftId, 1, Meta(key + "-reserve"));
        Assert.Equal(reservation, await Evidence.RecoverReservationAsync(Actor, draftId, key + "-reserve"));
        using var source = new MemoryStream(bytes ?? Bytes); var upload = await Evidence.UploadAsync(Actor, draftId, reservation.ReservationId, reservation.Revision, source, Meta(key + "-upload"));
        Assert.Equal("quarantined", upload.State); return (reservation, upload.Evidence!.Reference);
    }
    internal async Task UploadAsync() => (Reservation, Reference) = await UploadForAsync(DraftId, "inspection-video");
    internal async Task ValidateAsync(byte[]? expectedBytes = null) {
        Assert.Equal("completed", await Runtime.RunOnceAsync()); Assert.Equal(expectedBytes ?? Bytes, Scanner.Bytes);
    }
    internal Task<BsonDocument> PreviewSubmitAsync() => Service.PreviewSubmitAsync(Actor, DraftId, 1, Reservation.ReservationId, Reference);
    internal Task<BsonDocument> SubmitAsync(BsonDocument preview, string key = "inspection-submit") => Evidence.SubmitAsync(Actor, DraftId, 1,
        Reservation.ReservationId, Reference, preview["binding"].AsString, Meta(key));
    internal async Task<string> OriginalPathAsync(ReservationResult? reservation = null) {
        reservation ??= Reservation; var row = await Rows("documentVersions").Find(new BsonDocument("_id", reservation.VersionRowId)).SingleAsync();
        var generation = row["generation"].AsString; Assert.Matches("^[a-f0-9]{64}$", generation);
        var path = Path.GetFullPath(Path.Combine(Files.Path, generation + ".original")); Assert.Equal(Files.Path, Path.GetDirectoryName(path)); return path;
    }
    internal async Task<BsonDocument> StateAsync() {
        var state = new BsonDocument(); foreach (var name in new[] { Drafts, Submissions, Heads }) {
            var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence"); state[name] = new BsonArray(rows);
        }
        foreach (var (name, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            state[name] = new BsonArray(await Rows(name).Find(new BsonDocument(field, new BsonDocument("$in", new BsonArray { DraftOperation, SubmitOperation }))).Sort(new BsonDocument("_id", 1)).ToListAsync());
        state["pins"] = new BsonArray(await Rows("evidencePins").Find(new BsonDocument("associationKind", "ProcurementInspection")).Sort(new BsonDocument("_id", 1)).ToListAsync()); return state;
    }
    private static string RequiredPath(string variable) {
        var value = Environment.GetEnvironmentVariable(variable);
        if (value is null || !Path.IsPathFullyQualified(value) || !File.Exists(value)) throw new InvalidOperationException("Configure actual existing local video fixture/runtime: " + variable);
        return Path.GetFullPath(value);
    }
    private static byte[] AddFreeBox(byte[] original) {
        var length = 10 * 1024 * 1024 + 1024; var padded = new byte[length]; original.CopyTo(padded, 0);
        BinaryPrimitives.WriteUInt32BigEndian(padded.AsSpan(original.Length, 4), checked((uint)(length - original.Length))); "free"u8.CopyTo(padded.AsSpan(original.Length + 4)); return padded;
    }
    public async ValueTask DisposeAsync() {
        try { if (Scanner is not null) await Scanner.DisposeAsync(); }
        finally { try { Files?.Dispose(); } finally { await Receiving.DisposeAsync(); } }
    }
}
