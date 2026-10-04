using System.Globalization;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Procurement;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PdfDocumentOcrManualMatchingTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Consistent_supplier_PDF_supports_real_manual_matching_with_exact_original_lineage_and_idempotent_nonfinancial_receipt() {
        await using var data = await SupplierDocumentTestData.CreateAsync(fixture);
        await using var scanner = await PdfEvidenceAdmissionTestRuntime.CreateAsync(data.Database, data.Unit, data.Clock, data.Files.Path, ProcurementInspectionTestData.StoreId);
        await PdfDocumentOcrProcessingTestData.ReadPolicyAsync(data.Database, ProcurementInspectionTestData.StoreId);
        var draft = await data.Rows("supplierDocumentDrafts").Find(new BsonDocument("_id", data.DraftId)).SingleAsync();
        var input = SupplierDocumentBson.Input(draft["input"]); var money = CultureInfo.GetCultureInfo("id-ID");
        var text = new[] {
            "Nomor invoice : " + input.DocumentReference,
            "Tanggal invoice : " + input.DocumentDate!.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            "Nama pemasok : " + draft["source"]["proposal"]["supplier"]["displayName"].AsString,
            "Total tagihan : Rp " + input.DocumentTotal!.Value.Value.ToString("N2", money),
            "Nomor PO : " + input.AnchorOrderId.ToString(CultureInfo.InvariantCulture),
            "Kode barang pemasok : " + input.Lines[0].SourceCode,
            "Jumlah kemasan : " + input.Lines[0].Packages!.Value.ToString(CultureInfo.InvariantCulture),
            "Harga per kemasan : Rp " + input.Lines[0].NetPricePerPackage!.Value.Value.ToString("N2", money),
            "Pajak : Rp " + input.Lines[0].TaxAmount!.Value.Value.ToString("N2", money),
            "Biaya pemasok : Rp " + input.Lines[0].ChargeAmount!.Value.Value.ToString("N2", money) };
        var bytes = PdfDocumentOcrProcessingTestData.Pdf((text, 0), (text, 90));
        data.Reservation = await data.Evidence.ReservePdfAsync(data.Actor, data.DraftId, 1, SupplierDocumentTestData.Meta("manual-match-real-pdf"));
        using var content = new MemoryStream(bytes);
        var uploaded = await data.Evidence.UploadPdfAsync(data.Actor, data.DraftId, data.Reservation.ReservationId, data.Reservation.Revision,
            content, SupplierDocumentTestData.Meta("manual-match-real-pdf-upload"));
        data.Reference = uploaded.Evidence!.Reference; Assert.Equal("completed", await scanner.Runtime.RunOnceAsync());
        var runtime = PdfDocumentOcrRuntime.CreateSupplierInvoice(data.Database, data.Unit, new(data.Database), data.Clock,
            PdfDocumentOcrProcessingTestData.Options(data.Files.Path, ProcurementInspectionTestData.StoreId, PdfDocumentOcrProcessingTestData.SupplierConsumer));
        await PdfDocumentOcrProcessingTestData.PolicyAsync(data.Database, data.Clock, ProcurementInspectionTestData.StoreId, runtime);
        data.Submission = await data.Evidence.SubmitAsync(data.Actor, data.DraftId, 1, data.Reservation.ReservationId, data.Reference,
            (await data.Service.PreviewSubmitAsync(data.Actor, data.DraftId, 1, data.Reservation.ReservationId, data.Reference))["binding"].AsString,
            SupplierDocumentTestData.Meta("manual-match-real-pdf-submit"));
        Assert.Equal("completed", await runtime.RunOnceAsync()); Assert.Equal(bytes, scanner.Scanner.Bytes);
        var job = await data.Rows("outboxEvents").Find(new BsonDocument("kind", PdfDocumentExtractionContract.SupplierJobKind)).SingleAsync();
        var extraction = await data.Unit.ExecuteAsync((tx, ct) => new PdfDocumentExtractionRecords(data.Database).ResultAsync(tx, job, ct)); Assert.NotNull(extraction);
        Assert.Equal(2, extraction["pageCount"]); Assert.Equal(PdfDocumentExtractionRecords.Receipt(extraction), job["processingReceipt"]);
        Assert.Equal(data.Reference.Sha256, extraction["binding"]["reference"]["sha256"]);

        // Reading exact bytes is independently authorized. This test does not claim that
        // the matching command persists page candidates or an original-view attestation.
        await using (var original = await data.Evidence.OpenPdfReadAsync(data.Matcher, data.DraftId, data.Reservation.ReservationId, 1,
            data.Reference, SupplierDocumentTestData.ReadMetadata)) {
            using var copy = new MemoryStream(); await original.Content.CopyToAsync(copy); Assert.Equal(bytes, copy.ToArray());
        }
        var receiving = await data.Inspection.Receiving.StateAsync(); var inspection = await data.Inspection.StateAsync();
        var orders = await data.Inspection.Receiving.Orders.StateAsync(); var financial = await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database);
        financial.Remove("supplierDocumentMatches");
        var reviewed = data.MatchInput(reviewed: input); Assert.Null(reviewed.OcrResultId);
        const string key = "supplier-pdf-manual-match";
        var matched = await data.MatchAsync(key, reviewed); Assert.Equal("match_recorded", matched["state"]);
        var row = await data.Rows("supplierDocumentMatches").Find(new BsonDocument("_id", matched["matchId"])).SingleAsync();
        Assert.True(row["input"]["ocrResultId"].IsBsonNull);
        Assert.Equal(matched, await data.Service.MatchAsync(data.Matcher, reviewed, row["binding"].AsString, SupplierDocumentTestData.Meta(key)));
        Assert.Equal(matched, await data.Service.LookupAsync(data.Matcher, SupplierDocumentTestData.MatchOperation, key));
        Assert.Equal(1, await data.Rows("supplierDocumentMatches").CountDocumentsAsync(new BsonDocument()));
        var audit = await data.Rows("auditEvents").Find(new BsonDocument("_id", row["auditId"])).SingleAsync();
        Assert.Equal(SupplierDocumentBson.Audit(row), audit);
        var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument { { "actorId", data.Matcher.UserId },
            { "operation", SupplierDocumentTestData.MatchOperation }, { "key", key } }).SingleAsync();
        Assert.Equal(row["bodyHash"], receipt["bodyHash"]); Assert.Equal(matched, receipt["result"]);
        var effect = await data.Rows("outboxEvents").Find(new BsonDocument("sourceKey", SupplierDocumentBson.EffectKey(row))).SingleAsync();
        Assert.Equal(SupplierDocumentTestData.MatchOperation, effect["kind"]); Assert.Equal(matched, effect["payload"]);
        var submission = await data.Rows("supplierDocumentSubmissions").Find(new BsonDocument("_id", row["documentId"])).SingleAsync();
        Assert.Equal(submission["snapshotHash"], row["documentHash"]); Assert.Equal(submission["_id"], extraction["binding"]["submissionId"]);
        Assert.Equal(submission["snapshotHash"], extraction["binding"]["submissionHash"]); Assert.Equal(data.Reference.Sha256, submission["evidence"]["sha256"]);
        var pin = await data.Rows("evidencePins").Find(new BsonDocument("_id", submission["pinId"])).SingleAsync();
        Assert.Equal("SupplierDocument", pin["associationKind"]); Assert.Equal(submission["_id"], pin["associationId"]);
        Assert.Equal(data.Reference.Sha256, pin["reference"]["sha256"]); Assert.Equal(data.Reference.Sha256, job["payload"]["original"]["sha256"]);
        var link = Assert.Single(row["links"].AsBsonArray); Assert.Equal(data.InspectionId, link["id"]);
        Assert.Equal(input.AnchorOrderId, link["orderId"]); Assert.Equal(draft["source"]["approval"]["snapshotHash"], link["orderHash"]);
        var observed = Assert.Single(row["lines"].AsBsonArray)["inspectionLine"];
        Assert.Equal(0, observed["damagedPackages"]); Assert.Equal(0, observed["expiredPackages"]); Assert.Equal(0, observed["openedPackages"]);
        Assert.Equal(observed["declaredPackages"], observed["receivedPackages"]); Assert.True(observed["discrepancy"].IsBsonNull);
        Assert.Contains(row["claims"].AsBsonArray, claim => claim["kind"] == "inspection" && claim["sourceId"] == data.InspectionId);
        Assert.Contains(row["claims"].AsBsonArray, claim => claim["kind"] == "order" && claim["sourceId"] == input.AnchorOrderId);
        Assert.Equal(1L, row["after"]["lines"][0]["remainingPackages"]); Assert.Equal("20000.00", row["after"]["lines"][0]["matchedNetAmount"]);
        Assert.Equal(receiving, await data.Inspection.Receiving.StateAsync()); Assert.Equal(inspection, await data.Inspection.StateAsync());
        Assert.Equal(orders, await data.Inspection.Receiving.Orders.StateAsync());
        var after = await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database); after.Remove("supplierDocumentMatches"); Assert.Equal(financial, after);
        Assert.Equal(0, await data.Rows("supplierDocumentExtractionResults").CountDocumentsAsync(new BsonDocument()));
        await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }
}
