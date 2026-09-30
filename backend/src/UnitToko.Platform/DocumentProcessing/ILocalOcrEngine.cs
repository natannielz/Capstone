using UnitToko.Modules.Documents;

namespace UnitToko.Platform.DocumentProcessing;

internal interface ILocalOcrEngine
{
    ExtractionProvenance Provenance { get; }
    Task<ExtractionResult> ExtractAsync(string jobId, EvidenceRef evidence, ExtractionPurpose purpose, Stream validatedBytes, CancellationToken cancellationToken);
}
