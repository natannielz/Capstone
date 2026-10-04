using MongoDB.Bson;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Procurement;

namespace UnitToko.Tests.Documents;

// Draft only: these tests become executable only with the coordinated M86 owners and evidence bridge.
public sealed class PdfSubmissionProtocolTests
{
    [Fact]
    public void Payment_primary_image_job_keeps_its_exact_frozen_payload_and_PDF_cannot_replace_required_image()
    {
        var evidence = new EvidenceRef(12, 3, new string('b', 64));
        var row = new BsonDocument { { "_id", 42L }, { "reviewRevision", 1L }, { "declarationId", 21L }, { "declarationRevision", 4L },
            { "declarationVersionId", 22L }, { "uploaderId", 8L }, { "reservationId", 24L },
            { "pinId", 23L }, { "evidence", PaymentSubmissionBson.Reference(evidence) }, { "snapshotHash", new string('a', 64) } };
        var frozenImagePayload = new BsonDocument { { "submissionId", 42L }, { "reviewRevision", 1L }, { "declarationVersionId", 22L },
            { "evidence", PaymentSubmissionBson.Reference(evidence) }, { "pinId", 23L }, { "submissionHash", new string('a', 64) } };
        Assert.Equal("payment.ocr.requested:42", PaymentSubmissionBson.OcrKey(row));
        Assert.Equal(frozenImagePayload.ToBson(), PaymentSubmissionBson.OcrPayload(row).ToBson());
        Assert.Equal(new[] { "submissionId", "reviewRevision", "declarationVersionId", "evidence", "pinId", "submissionHash" }, frozenImagePayload.Names);
        Assert.False(frozenImagePayload.Contains("original")); Assert.False(frozenImagePayload.Contains("uploaderId"));
        var refusal = PaymentSubmissionBson.ImageRequired(); Assert.Equal("payment_image_evidence_required", refusal.Code);
        Assert.Contains("bukti gambar", refusal.Message); Assert.Contains("PDF", refusal.Message);
    }

    [Fact]
    public void Supplier_document_job_preserves_the_frozen_draft_and_uploader_and_old_image_payload_is_unchanged()
    {
        var original = PdfRasterProtocolTests.Expected();
        var evidence = new EvidenceRef(12, 3, original.Content.Sha256);
        var row = new BsonDocument { { "_id", 43L }, { "draftId", 31L }, { "actorId", 9L }, { "reservationId", original.Attempt.ReservationId },
            { "pinId", 33L }, { "evidence", PaymentSubmissionBson.Reference(evidence) }, { "snapshotHash", new string('a', 64) }, { "draftHash", new string('b', 64) } };
        var frozenImagePayload = new BsonDocument { { "submissionId", 43L }, { "draftId", 31L }, { "draftRevision", 1L },
            { "evidence", PaymentSubmissionBson.Reference(evidence) }, { "pinId", 33L }, { "submissionHash", new string('a', 64) }, { "draftHash", new string('b', 64) } };
        Assert.Equal("supplierdocument.ocr:43:1", SupplierDocumentBson.OcrKey(row));
        Assert.Equal(frozenImagePayload.ToBson(), SupplierDocumentBson.OcrPayload(row).ToBson());
        var payload = SupplierDocumentBson.DocumentOcrPayload(row, original);
        Assert.Equal("supplierdocument.document-ocr.requested:43", SupplierDocumentBson.DocumentOcrKey(row));
        Assert.Equal("SupplierInvoice", payload["sourceKind"]);
        foreach (var key in new[] { "draftId", "draftHash", "reservationId", "pinId", "evidence" }) Assert.Equal(row[key], payload[key]);
        Assert.Equal(1L, payload["draftRevision"].AsInt64); Assert.Equal(row["actorId"], payload["uploaderId"]);
        Assert.Equal(row["_id"], payload["submissionId"]); Assert.Equal(row["snapshotHash"], payload["submissionHash"]);
        Assert.Equal(PdfDocumentExtractionRecords.Original(original), payload["original"]);
        Assert.Equal(new[] { "sourceKind", "submissionId", "evidence", "pinId", "submissionHash", "uploaderId", "reservationId", "original",
            "draftId", "draftRevision", "draftHash" }, payload.Names);
        Assert.False(frozenImagePayload.Contains("original")); Assert.False(frozenImagePayload.Contains("uploaderId"));
    }
}
