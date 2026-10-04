using System.Collections.Immutable;
using System.Globalization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Mongo;

namespace UnitToko.Api.Billing;

// Page-aware wire contract. Private paths, stage capabilities, claim tokens and worker policy
// records remain in their owning service and are never returned by this mapping.
public sealed record PdfOcrCandidateDto(string Field, string? Value, string? Confidence,
    ImmutableArray<string>? BoundingBox, ImmutableArray<PdfDocumentPoint>? PdfCanvasCorners,
    ImmutableArray<string> Flags);
public sealed record PdfOcrLineDto(string Text, string Confidence, ImmutableArray<string> BoundingBox,
    ImmutableArray<PdfDocumentPoint> PdfCanvasCorners);
public sealed record PdfOcrPageDto(PdfDocumentRasterPage Raster, ExtractionProvenance OcrProvenance,
    PdfDocumentPreprocessing Preprocessing, ImmutableArray<PdfOcrLineDto> Lines,
    ImmutableArray<PdfOcrCandidateDto> Fields, ImmutableArray<string> ReviewReasons);
public sealed record PdfOcrObservationDto(int PageIndex, PdfOcrCandidateDto Candidate);
public sealed record PdfOcrFieldReviewDto(string Field, ImmutableArray<PdfOcrObservationDto> Observations,
    ImmutableArray<string> ReviewReasons);
public sealed record PdfOcrResultDto(LongId Id, string ResultHash, DateTimeOffset ExtractedAt,
    string Purpose, PdfDocumentRasterProvenance RasterProvenance, ExtractionProvenance OcrProvenance,
    ImmutableArray<PdfOcrPageDto> Pages, ImmutableArray<PdfOcrFieldReviewDto> Fields,
    bool RequiresHumanReview);
public sealed record PdfOcrViewDto(LongId SourceId, string SourceRevision, LongId SubmissionId,
    string SourceKind, PaymentEvidenceReferenceDto Evidence, string State, PdfOcrResultDto? Result,
    string? FailureCode);

internal static class PdfDocumentOcrDtos
{
    internal static PdfOcrViewDto View(PdfDocumentExtractionView value)
    {
        PdfOcrResultDto? result = null;
        if (value.Extraction is { } extraction) {
            if (value.ResultId is not { } resultId || value.ResultHash is not { } resultHash ||
                value.ExtractedAt is not { } extractedAt || extraction.Evidence != value.Evidence ||
                value.FailureCode is not null || !extraction.RequiresHumanReview) throw Invalid();
            result = new(new(resultId), resultHash, extractedAt, extraction.Purpose.ToString(),
                extraction.RasterProvenance, extraction.OcrProvenance,
                [.. extraction.Pages.Select(Page)],
                [.. extraction.Fields.Select(field => new PdfOcrFieldReviewDto(field.Field,
                    [.. field.Observations.Select(item => new PdfOcrObservationDto(item.PageIndex, Candidate(item.Candidate)))],
                    [.. field.ReviewReasons]))], true);
        } else if (value.ResultId is not null || value.ResultHash is not null || value.ExtractedAt is not null) {
            throw Invalid();
        }
        return new(new(value.SourceId), OrderDraftInputDto.Number(value.SourceRevision), new(value.SubmissionId),
            value.SourceKind, new(new(value.Evidence.Id), OrderDraftInputDto.Number(value.Evidence.Version),
                value.Evidence.Sha256), value.State, result, value.FailureCode);
    }
    private static PdfOcrPageDto Page(PdfDocumentPageExtraction value) => new(value.Raster,
        value.OcrProvenance, value.Preprocessing,
        [.. value.Lines.Select(line => new PdfOcrLineDto(line.Text, Number(line.Confidence),
            Box(line.ImageBox), [.. line.PdfCanvasCorners]))],
        [.. value.Fields.Select(Candidate)], [.. value.ReviewReasons]);
    private static PdfOcrCandidateDto Candidate(PdfDocumentFieldCandidate value) => new(value.Field,
        value.Value, value.Confidence is { } confidence ? Number(confidence) : null,
        value.ImageBox is { } box ? Box(box) : null,
        value.PdfCanvasCorners is { } corners ? [.. corners] : null, [.. value.Flags]);
    private static ImmutableArray<string> Box(NormalizedBox value) =>
        [Number(value.X), Number(value.Y), Number(value.Width), Number(value.Height)];
    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static PersistenceConflictException Invalid() => new("pdf_document_extraction_integrity",
        "Kelengkapan hasil OCR seluruh halaman belum dapat dipastikan");
}
