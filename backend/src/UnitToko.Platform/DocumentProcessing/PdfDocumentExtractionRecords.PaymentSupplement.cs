using MongoDB.Bson;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Billing;

namespace UnitToko.Platform.DocumentProcessing;

// Separate whole-PDF lineage; the primary payment image remains its own owner.
internal sealed partial class PdfDocumentExtractionRecords
{
    internal static ExtractionPurpose PurposeForSource(string sourceKind) => sourceKind switch {
        "SupplierInvoice" => ExtractionPurpose.SupplierInvoice,
        "Payment" or PaymentSupplementBson.SourceKind => ExtractionPurpose.Payment,
        _ => throw Broken()
    };

    private static BsonDocument SupplementPayload(BsonDocument binding, BsonDocument original) => new() {
        { "sourceKind", PaymentSupplementBson.SourceKind }, { "supplementId", binding["supplementId"] },
        { "supplementRevision", binding["supplementRevision"] }, { "primary", binding["primary"] },
        { "primarySubmissionHash", binding["primarySubmissionHash"] }, { "evidence", binding["reference"] },
        { "reservationId", binding["reservationId"] }, { "uploaderId", binding["uploaderId"] },
        { "pinId", binding["pinId"] }, { "attachmentHash", binding["submissionHash"] }, { "original", original }
    };

    private static bool AllowsReadPurpose(string sourceKind, string purpose) => sourceKind switch {
        "SupplierInvoice" => purpose == "supplier-document-pdf-ocr-view",
        "Payment" => purpose is "payment-pdf-ocr-view" or "payment-review-pdf-ocr-view",
        PaymentSupplementBson.SourceKind => purpose is "payment-supplement-pdf-ocr-view" or "payment-review-supplement-pdf-ocr-view",
        _ => false
    };

    private static (long Id, long Revision) ReadSourceIdentity(string sourceKind, BsonDocument binding) => sourceKind switch {
        "SupplierInvoice" => (binding["draftId"].AsInt64, binding["draftRevision"].AsInt64),
        "Payment" => (binding["declarationId"].AsInt64, binding["declarationRevision"].AsInt64),
        PaymentSupplementBson.SourceKind => (binding["supplementId"].AsInt64, binding["supplementRevision"].AsInt64),
        _ => throw Broken()
    };

    private static string OriginalPurpose(string sourceKind) => sourceKind switch {
        "SupplierInvoice" => "SupplierDocumentPdf",
        "Payment" => "PaymentPdf",
        PaymentSupplementBson.SourceKind => "PaymentSupplementPdf",
        _ => throw Broken()
    };
}
