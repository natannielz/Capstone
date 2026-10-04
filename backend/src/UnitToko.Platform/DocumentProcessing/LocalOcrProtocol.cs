using UnitToko.Modules.Documents;

namespace UnitToko.Platform.DocumentProcessing;

// Persisted protocol identifiers are portable; executing the pinned engine remains Windows-only.
internal static class LocalOcrProtocol
{
    internal const string Engine = "tesseract-5.5.3/tesserocr-2.11.0";
    internal const string ImagePreprocessing = "bounded-gray-orient4-deskew10-contrast1-psm6-ind+eng-v2";
    internal const string PaymentPreprocessing = ImagePreprocessing + "/" + PaymentFieldExtractor.Version;
}
