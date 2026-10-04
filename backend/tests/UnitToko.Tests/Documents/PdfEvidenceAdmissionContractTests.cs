using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Documents;

public sealed class PdfEvidenceAdmissionContractTests
{
    [Fact]
    public void Explicit_pdf_admission_retains_actual_owner_revisions_without_turning_image_or_video_purposes_into_pdf() {
        Assert.Equal(3, new ReserveEvidencePdf(new(EvidenceTargetKind.PaymentDeclaration, 1), EvidencePurpose.PaymentPdf, 3).ExpectedTargetRevision);
        Assert.Equal(1, new ReserveEvidencePdf(new(EvidenceTargetKind.SupplierDocument, 1), EvidencePurpose.SupplierDocumentPdf, 1).ExpectedTargetRevision);
        foreach (var (kind, purpose, revision) in new[] {
            (EvidenceTargetKind.PaymentDeclaration, EvidencePurpose.PaymentImage, 1L), (EvidenceTargetKind.SupplierDocument, EvidencePurpose.SupplierDocumentImage, 1L),
            (EvidenceTargetKind.CatalogSku, EvidencePurpose.PaymentPdf, 1L), (EvidenceTargetKind.PaymentDeclaration, EvidencePurpose.SupplierDocumentPdf, 1L),
            (EvidenceTargetKind.SupplierDocument, EvidencePurpose.PaymentPdf, 1L), (EvidenceTargetKind.SupplierDocument, EvidencePurpose.SupplierDocumentPdf, 2L),
            (EvidenceTargetKind.PaymentDeclaration, EvidencePurpose.PaymentPdf, 0L), (EvidenceTargetKind.SupplierDocument, EvidencePurpose.SupplierDocumentPdf, 0L) })
            Assert.Throws<DomainValidationException>(() => new ReserveEvidencePdf(new(kind, 1), purpose, revision));
        Assert.Throws<DomainValidationException>(() => new ReserveEvidenceImage(new(EvidenceTargetKind.PaymentDeclaration, 1), EvidencePurpose.PaymentPdf, 1));
        Assert.Throws<DomainValidationException>(() => new ReserveEvidenceImage(new(EvidenceTargetKind.SupplierDocument, 1), EvidencePurpose.SupplierDocumentPdf, 1));
        Assert.Throws<DomainValidationException>(() => new ReserveEvidenceVideo(new(EvidenceTargetKind.PaymentDeclaration, 1), 1));
    }
    [Fact]
    public void Forward_pdf_schemas_preserve_exact_predecessor_branches_stronger_terms_and_format_specific_operation_identity() {
        foreach (var name in PdfEvidenceAdmissionSchemas.Collections) {
            var predecessor = PdfEvidenceAdmissionSchemas.PreviousValidator(name); var after = PdfEvidenceAdmissionSchemas.Validator(name);
            Assert.Equal(predecessor, after["$or"][0]);
            var term = new BsonDocument("_id", new BsonDocument("$exists", true)); var stronger = new BsonDocument("$and", new BsonArray { predecessor, term });
            var extended = PdfEvidenceAdmissionSchemas.Extend(name, stronger);
            Assert.Equal(term, extended["$and"][1]); Assert.Equal(after, extended["$and"][0]);
            Assert.True(SupplierDocumentEvidenceSchemas.Supports(name, extended, predecessor));
            Assert.Equal(extended, PdfEvidenceAdmissionSchemas.Extend(name, extended));
            Assert.Throws<InvalidOperationException>(() => PdfEvidenceAdmissionSchemas.Extend(name, new BsonDocument("purpose", "PaymentPdf")));
            Assert.False(SupplierDocumentEvidenceSchemas.Supports(name, new BsonDocument("$or", new BsonArray { predecessor, new BsonDocument("purpose", "PaymentPdf") }), after));
        }
        Assert.Contains("evidence.payment-pdf.reserve", PdfEvidenceAdmissionSchemas.Validator("idempotencyRecords").ToJson());
        Assert.Contains("evidence.supplier-document-pdf.complete", PdfEvidenceAdmissionSchemas.Validator("idempotencyRecords").ToJson());
        Assert.Equal(7, (int)EvidencePurpose.PaymentPdf); Assert.Equal(8, (int)EvidencePurpose.SupplierDocumentPdf);
        Assert.Equal(6, (int)EvidencePurpose.SupplierDocumentImage);
        // The payment OCR conditional is an independent conjunct of the installed predecessor.
        // The PDF extension replaces the exact media branch and retains that conjunct unchanged.
        var installedPredecessor = new BsonDocument("$and", new BsonArray { PdfEvidenceValidationSchemas.PreviousOutboxValidator(), DocumentProcessingSchemas.OutboxValidator() });
        var pdfOutbox = PdfEvidenceValidationSchemas.ExtendOutbox(installedPredecessor);
        var strongOutbox = new BsonDocument("$and", new BsonArray { pdfOutbox, new BsonDocument("_id", new BsonDocument("$exists", true)) });
        foreach (var required in new[] { DocumentProcessingSchemas.OutboxValidator(), SupplierDocumentProcessingSchemas.OutboxValidator(),
            VideoValidationSchemas.OutboxValidator(), EvidenceSchemas.Validator("outboxEvents") }) {
            Assert.True(SupplierDocumentEvidenceSchemas.Supports("outboxEvents", pdfOutbox, required));
            Assert.True(SupplierDocumentEvidenceSchemas.Supports("outboxEvents", strongOutbox, required));
            Assert.False(SupplierDocumentEvidenceSchemas.Supports("outboxEvents", new BsonDocument("$or", new BsonArray {
                pdfOutbox, new BsonDocument("kind", "unreviewed") }), required));
        }
    }
}
