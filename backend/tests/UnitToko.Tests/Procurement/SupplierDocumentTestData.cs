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
using UnitToko.Tests.Ordering;
using StorageRoot = UnitToko.Tests.Documents.PrivateEvidenceStorageTests.StorageRoot;

namespace UnitToko.Tests.Procurement;

// Real receipt, private video inspection, image publication, local decoder and pinned source.
// Loopback scanner signatures and operational approvals are synthetic test inputs only.
[SupportedOSPlatform("windows")]
internal sealed class SupplierDocumentTestData(ProcurementInspectionTestData inspection) : IAsyncDisposable
{
    internal const string DraftOperation = "procurement.supplierdocument.draft", SubmitOperation = "procurement.supplierdocument.submit", MatchOperation = "procurement.supplierdocument.match";
    internal const string Consumer = "synthetic-supplier-document-image-worker";
    internal ProcurementInspectionTestData Inspection { get; } = inspection;
    internal StoreTestData Base => Inspection.Base;
    internal IMongoDatabase Database => Inspection.Database;
    internal MongoUnitOfWork Unit => Inspection.Unit;
    internal MutableAuthorizationClock Clock => Inspection.Clock;
    internal StorageRoot Files => Inspection.Files;
    internal EvidenceStoreRegistration Registration => Inspection.Registration;
    internal LocalScannerTests.Server Scanner = null!;
    internal EvidenceValidationRuntime Runtime = null!;
    internal ActorContext Actor = null!, Reader = null!, EvidenceReader = null!, Matcher = null!, Outsider = null!;
    internal BsonDocument InspectionResult = null!, Draft = null!, Submission = null!;
    internal long InspectionId => InspectionResult["inspectionId"].AsInt64;
    internal long DraftId => Draft["draftId"].AsInt64;
    internal long DocumentId => Submission["documentId"].AsInt64;
    internal ReservationResult Reservation = null!;
    internal EvidenceRef Reference = null!;
    internal byte[] Bytes = ImageDecoderTests.Png(13, 7);
    internal SupplierDocumentService Service => new(Database, Unit, new(Database), Clock, Inspection.Receiving.Procurement.Catalog,
        Inspection.Receiving.Orders.Service, Inspection.Receiving.Service);
    internal SupplierDocumentEvidenceService Evidence => new(Database, Unit, new(Database), Clock, Service, Registration);
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic original supplier document and exact human review; no stock or money approval", "supplier-document-test");
    internal static AuthorizationReadMetadata ReadMetadata => new("supplier-document-evidence-view", "synthetic-supplier-original-review");
    internal SupplierDocumentInput Input(string reference = "SYNTHETIC-INVOICE-0001") => new(ProcurementRequestTestData.Location,
        Inspection.Receiving.OrderId, 1, Inspection.Receiving.ApprovalId, SupplierDocumentKind.Invoice, reference,
        AccountingCalendar.BusinessDate(Clock.UtcNow), "IDR", new Money(34000),
        [new("001", "Actual synthetic three sealed pencil packages", "supplier", "000001", "000012", 3, new Money(10000), new Money(3300), new Money(700), null, null)], null);
    internal SupplierDocumentMatchInput MatchInput(int packages = 2, Money? tax = null, Money? charge = null, SupplierDocumentInput? reviewed = null) => new(DocumentId, 1, reviewed ?? Input(), null,
        [new(1, packages, InspectionId, 1, null, null, new Money(10000 * packages), tax ?? new Money(2000), charge ?? new Money(500), null, null)]);
    internal static async Task<SupplierDocumentTestData> CreateAsync(MongoFixture fixture, ProcurementRoute route = ProcurementRoute.Ddo, bool createDraft = true, decimal requiredAdvance = 0) {
        var inspection = await ProcurementInspectionTestData.CreateAsync(fixture, route, createDraft: false, requiredAdvance: requiredAdvance); var data = new SupplierDocumentTestData(inspection);
        try {
            var observed = inspection.Input(); observed = observed with { Discrepancy = null, Lines = [observed.Lines[0] with {
                DamagedPackages = 0, ExpiredPackages = 0, OpenedPackages = 0, ExpiryDate = AccountingCalendar.BusinessDate(data.Clock.UtcNow).AddYears(1), Discrepancy = null }] };
            inspection.Draft = await inspection.DraftAsync("supplier-document-inspection-draft", observed);
            await inspection.UploadAsync(); await inspection.ValidateAsync(); data.InspectionResult = await inspection.SubmitAsync(await inspection.PreviewSubmitAsync());
            var scope = ProcurementRequestTestData.Scope;
            data.Actor = await data.Base.AddActorAsync(700, scope, Permissions.ProcurementRead, Permissions.ProcurementSupplierDocumentRecord, Permissions.ProcurementSupplierDocumentEvidenceRead, Permissions.ProcurementSupplierDocumentMatch);
            data.Reader = await data.Base.AddActorAsync(701, scope, Permissions.ProcurementRead);
            data.EvidenceReader = await data.Base.AddActorAsync(702, scope, Permissions.ProcurementRead, Permissions.ProcurementSupplierDocumentEvidenceRead);
            data.Matcher = await data.Base.AddActorAsync(703, scope, Permissions.ProcurementRead, Permissions.ProcurementSupplierDocumentMatch, Permissions.ProcurementSupplierDocumentEvidenceRead);
            data.Outsider = await data.Base.AddActorAsync(704, new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: inspection.Receiving.Procurement.ForeignShopId,
                WarehouseId: inspection.Receiving.Procurement.ForeignWarehouseId, ChannelId: 103), Permissions.ProcurementRead, Permissions.ProcurementSupplierDocumentRecord,
                Permissions.ProcurementSupplierDocumentEvidenceRead, Permissions.ProcurementSupplierDocumentMatch);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Actor.UserId, data.Reader.UserId, data.EvidenceReader.UserId, data.Matcher.UserId, data.Outsider.UserId);
            await PaymentEvidenceReadTests.ReadData.PolicyAsync(data.Database, ProcurementInspectionTestData.StoreId, data.Clock.UtcNow);
            var version = "ClamAV 1.5.1/30000/" + data.Clock.UtcNow.AddHours(-1).UtcDateTime.ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture);
            data.Scanner = new(version);
            data.Runtime = EvidenceValidationRuntime.Create(data.Database, data.Unit, new(data.Database), data.Clock,
                new(data.Files.Path, ProcurementInspectionTestData.StoreId, StorageRoot.TrustedOperators, Consumer, "synthetic-image-host-not-production-acceptance",
                    new(1024, 100, 100, 10000), ImageDecoderTests.Options(), new(data.Scanner.Port, "1.5.1", 30000, TimeSpan.FromDays(2), 1024, TimeSpan.FromSeconds(3))));
            await data.Rows("evidenceValidationPolicies").InsertOneAsync(new BsonDocument {
                { "_id", (await new IdAllocator(data.Database).NextAsync("evidenceValidationPolicies")).Value }, { "schemaVersion", 1 }, { "consumerId", Consumer },
                { "storeId", ProcurementInspectionTestData.StoreId }, { "pipelineId", data.Runtime.PipelineId }, { "active", true }, { "revision", 1L }, { "fence", 0L },
                { "approvedAt", data.Clock.UtcNow.AddMinutes(-1).UtcDateTime }, { "expiresAt", data.Clock.UtcNow.AddDays(1).UtcDateTime }, { "hostAcceptanceRef", "synthetic-image-host-not-production-acceptance" } });
            if (createDraft) data.Draft = await data.DraftAsync("supplier-document-draft"); return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    internal async Task<BsonDocument> DraftAsync(string key, SupplierDocumentInput? input = null) {
        input ??= Input(); var preview = await Service.PreviewAsync(Actor, input); return await Service.DraftAsync(Actor, input, preview["binding"].AsString, Meta(key));
    }
    internal async Task<(ReservationResult Reservation, EvidenceRef Reference)> UploadForAsync(long draftId, string key, byte[]? bytes = null) {
        var reservation = await Evidence.ReserveAsync(Actor, draftId, 1, Meta(key + "-reserve"));
        using var content = new MemoryStream(bytes ?? Bytes); var uploaded = await Evidence.UploadAsync(Actor, draftId, reservation.ReservationId, reservation.Revision, content, Meta(key + "-upload"));
        Assert.Equal("quarantined", uploaded.State); return (reservation, uploaded.Evidence!.Reference);
    }
    internal async Task UploadAsync() => (Reservation, Reference) = await UploadForAsync(DraftId, "supplier-document-image-" + DraftId);
    internal async Task ValidateAsync() { Assert.Equal("completed", await Runtime.RunOnceAsync()); Assert.Equal(Bytes, Scanner.Bytes); }
    internal Task<BsonDocument> PreviewSubmitAsync() => Service.PreviewSubmitAsync(Actor, DraftId, 1, Reservation.ReservationId, Reference);
    internal async Task<BsonDocument> SubmitAsync(BsonDocument preview, string key = "supplier-document-submit") => Submission = await Evidence.SubmitAsync(Actor, DraftId, 1,
        Reservation.ReservationId, Reference, preview["binding"].AsString, Meta(key));
    internal async Task SubmitOriginalAsync() { await UploadAsync(); await ValidateAsync(); await SubmitAsync(await PreviewSubmitAsync()); }
    internal async Task<BsonDocument> MatchAsync(string key, SupplierDocumentMatchInput? input = null, ActorContext? actor = null) {
        input ??= MatchInput(); actor ??= Matcher; var preview = await Service.PreviewMatchAsync(actor, input);
        return await Service.MatchAsync(actor, input, preview["binding"].AsString, Meta(key));
    }
    internal async Task<string> OriginalPathAsync() {
        var row = await Rows("documentVersions").Find(new BsonDocument("_id", Reservation.VersionRowId)).SingleAsync();
        var generation = row["generation"].AsString; Assert.Matches("^[a-f0-9]{64}$", generation);
        var path = Path.GetFullPath(Path.Combine(Files.Path, generation + ".original")); Assert.Equal(Files.Path, Path.GetDirectoryName(path)); return path;
    }
    internal async Task<BsonDocument> StateAsync() {
        var state = new BsonDocument(); foreach (var name in UnitToko.Platform.Mongo.Migrations.SupplierDocumentSchemas.Collections) {
            var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence"); state[name] = new BsonArray(rows);
        }
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            state[table] = new BsonArray(await Rows(table).Find(new BsonDocument(field, new BsonDocument("$in", new BsonArray { DraftOperation, SubmitOperation, MatchOperation }))).Sort(new BsonDocument("_id", 1)).ToListAsync());
        state["pins"] = new BsonArray(await Rows("evidencePins").Find(new BsonDocument("associationKind", "SupplierDocument")).Sort(new BsonDocument("_id", 1)).ToListAsync()); return state;
    }
    public async ValueTask DisposeAsync() { try { if (Scanner is not null) await Scanner.DisposeAsync(); } finally { await Inspection.DisposeAsync(); } }
}
