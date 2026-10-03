namespace UnitToko.Modules.Documents;

public sealed record SupplierDocumentExtractionView(long DraftId, long SubmissionId, EvidenceRef Evidence, string State,
    long? ResultId, DateTimeOffset? ExtractedAt, ExtractionProvenance? Provenance, IReadOnlyList<RecognizedLine> Lines,
    IReadOnlyList<SupplierDocumentFieldCandidate> Fields, IReadOnlyList<string> QualityFlags, string? FailureCode);
