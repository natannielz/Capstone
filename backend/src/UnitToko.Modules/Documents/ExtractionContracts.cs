using System.Collections.ObjectModel;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Documents;

public enum ExtractionPurpose { Payment = 1, SupplierInvoice = 2, DeliveryNote = 3 }

/// <summary>Top-left x/y and width/height, relative to the unchanged image, each in [0,1].</summary>
public sealed record NormalizedBox
{
    public decimal X { get; }
    public decimal Y { get; }
    public decimal Width { get; }
    public decimal Height { get; }
    public NormalizedBox(decimal x, decimal y, decimal width, decimal height) {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > 1 || y + height > 1) throw ExtractionBounds.Invalid();
        X = x; Y = y; Width = width; Height = height;
    }
    public IReadOnlyList<decimal> Values => Array.AsReadOnly(new[] { X, Y, Width, Height });
}

public sealed record RecognizedLine
{
    public string Text { get; }
    public decimal Confidence { get; }
    public NormalizedBox Box { get; }
    public RecognizedLine(string text, decimal confidence, NormalizedBox box) {
        ExtractionBounds.Text(text, 512); ExtractionBounds.Confidence(confidence);
        Text = text; Confidence = confidence; Box = box ?? throw ExtractionBounds.Invalid();
    }
}

/// <summary>A suggestion, never a bank receipt or authority to post money. Unknown values remain null.</summary>
public sealed record FieldCandidate
{
    public string Field { get; }
    public string? Value { get; }
    public decimal? Confidence { get; }
    public IReadOnlyList<decimal>? BoundingBox { get; }
    public IReadOnlyList<string> Flags { get; }
    public FieldCandidate(string field, string? value, decimal? confidence, NormalizedBox? box, IEnumerable<string> flags) {
        if (!PaymentFieldExtractor.Fields.Contains(field, StringComparer.Ordinal)) throw ExtractionBounds.Invalid();
        if (value is not null) ExtractionBounds.Text(value, 256);
        if (confidence is { } c) ExtractionBounds.Confidence(c);
        Field = field; Value = value; Confidence = confidence; BoundingBox = box?.Values; Flags = ExtractionBounds.Flags(flags);
    }
}

public sealed record PaymentExtraction(IReadOnlyList<FieldCandidate> Fields, IReadOnlyList<string> QualityFlags);

public sealed record ExtractionProvenance
{
    public string EngineVersion { get; }
    public string ModelVersion { get; }
    public string ModelSha256 { get; }
    public string RuntimeSha256 { get; }
    public string PreprocessingVersion { get; }
    public ExtractionProvenance(string engineVersion, string modelVersion, string modelSha256, string runtimeSha256, string preprocessingVersion) {
        ExtractionBounds.Text(engineVersion, 128); ExtractionBounds.Text(modelVersion, 128); ExtractionBounds.Text(preprocessingVersion, 128);
        ExtractionBounds.Hash(modelSha256); ExtractionBounds.Hash(runtimeSha256);
        EngineVersion = engineVersion; ModelVersion = modelVersion; ModelSha256 = modelSha256; RuntimeSha256 = runtimeSha256; PreprocessingVersion = preprocessingVersion;
    }
}

/// <summary>Bounded immutable engine output. Caller must bind job/reference/provenance to its current lease.</summary>
public sealed record ExtractionResult
{
    public string JobId { get; }
    public EvidenceRef Evidence { get; }
    public ExtractionPurpose Purpose { get; }
    public ExtractionProvenance Provenance { get; }
    public string Status { get; }
    public string? FailureCode { get; }
    public IReadOnlyList<RecognizedLine> Lines { get; }
    public IReadOnlyList<FieldCandidate> Fields { get; }
    public IReadOnlyList<string> QualityFlags { get; }
    public ExtractionResult(string jobId, EvidenceRef evidence, ExtractionPurpose purpose, ExtractionProvenance provenance,
        string status, string? failureCode, IEnumerable<RecognizedLine> lines) {
        ExtractionBounds.Text(jobId, 128);
        if (!Enum.IsDefined(purpose) || status is not ("succeeded" or "failed") ||
            (status == "succeeded" ? failureCode is not null || purpose is not (ExtractionPurpose.Payment or ExtractionPurpose.SupplierInvoice) : failureCode is not ("ocr_unavailable" or "ocr_timeout" or "ocr_invalid_output" or "ocr_integrity_mismatch" or "ocr_invalid_media" or "ocr_unsupported_purpose"))) throw ExtractionBounds.Invalid();
        Lines = ExtractionBounds.Lines(lines);
        if (status == "failed" && Lines.Count != 0) throw ExtractionBounds.Invalid();
        JobId = jobId; Evidence = evidence ?? throw ExtractionBounds.Invalid(); Purpose = purpose; Provenance = provenance ?? throw ExtractionBounds.Invalid();
        Status = status; FailureCode = failureCode;
        var parsed = status == "succeeded" && purpose == ExtractionPurpose.Payment ? PaymentFieldExtractor.Extract(Lines) : new PaymentExtraction(Array.Empty<FieldCandidate>(), Array.Empty<string>());
        Fields = parsed.Fields; QualityFlags = parsed.QualityFlags;
    }
}

internal static class ExtractionBounds
{
    internal static void Text(string value, int maximum) {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value != value.Trim() || value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)) throw Invalid();
    }
    internal static void Confidence(decimal value) { if (value is < 0 or > 1) throw Invalid(); }
    internal static void Hash(string value) {
        if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw Invalid();
    }
    internal static ReadOnlyCollection<RecognizedLine> Lines(IEnumerable<RecognizedLine> values) {
        ArgumentNullException.ThrowIfNull(values);
        var rows = values.Take(129).ToArray();
        if (rows.Length > 128 || rows.Any(r => r is null) || rows.Sum(r => r.Text.Length) > 16384) throw Invalid();
        return Array.AsReadOnly(rows);
    }
    internal static ReadOnlyCollection<string> Flags(IEnumerable<string> values) {
        ArgumentNullException.ThrowIfNull(values);
        var rows = values.Take(17).ToArray();
        if (rows.Length > 16 || rows.Distinct(StringComparer.Ordinal).Count() != rows.Length || rows.Any(v => string.IsNullOrEmpty(v) || v.Length > 64 || v.Any(c => c is not (>= 'a' and <= 'z') and not '_'))) throw Invalid();
        return Array.AsReadOnly(rows);
    }
    internal static DomainValidationException Invalid() => new("extraction_invalid", "Document extraction is invalid or outside its resource bounds");
}
