namespace UnitToko.Modules.Documents;

/// <summary>Security validation is independent of OCR suggestions, financial review and payment approval.</summary>
public static class PdfEvidenceValidationContract
{
    public const string JobKind = "evidence.pdf-validation.requested";
    public const string Mime = "application/pdf";
    public const long MaximumOriginalBytes = 10L * 1024 * 1024;
    public const int MaximumPages = 10;
}
