namespace UnitToko.Modules.Documents;

public sealed record EvidenceSubmissionFormatView(long SourceId, long SourceRevision, EvidenceRef Evidence, string Kind);
