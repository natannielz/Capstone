namespace UnitToko.Modules.Documents;

// Advisory text only. There is deliberately no verified amount, receipt, allocation or payment decision.
public sealed record PaymentExtractionView(long DeclarationId, long SubmissionId, EvidenceRef Evidence, string State,
    long? ResultId, DateTimeOffset? ExtractedAt, ExtractionResult? Extraction);
