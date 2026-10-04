using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.DocumentProcessing;

// Portable failure vocabulary; executing the private original/raster/OCR pipeline remains Windows-only.
internal static class PdfDocumentOcrFailures
{
    internal static bool Allowed(string code) => PdfDocumentExtractionSchemas.SafeFailure()["enum"].AsBsonArray.Contains(code);
    internal static bool Retryable(string code) => code is "ocr_unavailable" or "ocr_timeout" or "ocr_original_unavailable" or "pdf_document_deadline" or
        "pdf_document_unavailable" or "pdf_raster_deadline" or "pdf_raster_unavailable" or "pdf_raster_configuration" or "pdf_raster_runtime_pin" or "pdf_raster_process_failed";
    internal static string QueueFailure(string code) => code switch {
        "ocr_unavailable" or "ocr_timeout" or "ocr_invalid_output" or "ocr_integrity_mismatch" or "ocr_invalid_media" or "ocr_original_unavailable" => code,
        "pdf_document_deadline" or "pdf_raster_deadline" => "ocr_timeout",
        "pdf_document_source_mismatch" or "pdf_raster_source_mismatch" => "ocr_integrity_mismatch",
        "pdf_document_invalid_output" or "pdf_raster_invalid_output" or "pdf_document_deadline_invalid" => "ocr_invalid_output",
        "pdf_document_page_limit" or "pdf_raster_source_limit" or "pdf_raster_malformed_pdf" or "pdf_raster_encrypted_pdf" or "pdf_raster_unsupported_forms" or
        "pdf_raster_unsupported_attachments" or "pdf_raster_unsupported_javascript" or "pdf_raster_page_limit" or "pdf_raster_geometry_limit" or
        "pdf_raster_total_pixel_limit" or "pdf_raster_output_limit" => "ocr_invalid_media",
        "pdf_document_unavailable" or "pdf_raster_render_failed" or "pdf_raster_unavailable" or "pdf_raster_configuration" or "pdf_raster_runtime_pin" or "pdf_raster_process_failed" => "ocr_unavailable",
        _ => throw PdfDocumentExtractionRecords.Broken()
    };
}
