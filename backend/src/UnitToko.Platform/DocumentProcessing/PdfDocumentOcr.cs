using System.Diagnostics;
using System.Runtime.Versioning;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.DocumentProcessing;

/// <summary>One pinned OCR engine over a complete private PDF. The caller still owns source/permission/lease admission.</summary>
[SupportedOSPlatform("windows")]
internal sealed class PdfDocumentOcr
{
    private readonly PdfRasterOptions rasterOptions;
    private readonly LocalOcrOptions ocrOptions;
    private readonly WindowsPdfRaster raster;
    private readonly ExtractionPurpose purpose;
    internal ExtractionProvenance OcrProvenance { get; }
    internal PdfDocumentOcr(PdfRasterOptions rasterOptions, LocalOcrOptions ocrOptions, ExtractionPurpose purpose)
    {
        if (purpose is not (ExtractionPurpose.Payment or ExtractionPurpose.SupplierInvoice)) throw new ArgumentException("Unsupported PDF extraction purpose", nameof(purpose));
        ArgumentNullException.ThrowIfNull(rasterOptions); ArgumentNullException.ThrowIfNull(ocrOptions);
        if (ocrOptions.Files.Count is < 5 or > 256 || ocrOptions.Timeout < TimeSpan.FromMilliseconds(100) || ocrOptions.Timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentException("PDF OCR needs the existing bounded engine configuration", nameof(ocrOptions));
        this.rasterOptions = rasterOptions with { Files = Array.AsReadOnly(rasterOptions.Files.ToArray()) };
        this.ocrOptions = ocrOptions with { Files = Array.AsReadOnly(ocrOptions.Files.ToArray()) };
        this.purpose = purpose; raster = new(this.rasterOptions);
        OcrProvenance = new WindowsLocalOcrEngine(this.ocrOptions, purpose).Provenance;
    }

    internal async Task<PdfDocumentOcrResult> ExtractAsync(string jobId, EvidenceRef originalEvidence,
        VerifiedNativeOriginalHandle original, ExpectedPublication publication, DateTimeOffset capturedDeadline, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(originalEvidence); ArgumentNullException.ThrowIfNull(publication);
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 96 || jobId != jobId.Trim() || jobId.Any(char.IsControl)) throw new ArgumentException("Invalid PDF job identity", nameof(jobId));
        ct.ThrowIfCancellationRequested();
        int? pageIndex = null;
        var elapsed = Stopwatch.StartNew(); var window = capturedDeadline - DateTimeOffset.UtcNow;
        if (window <= TimeSpan.Zero) return Failed("pdf_document_deadline");
        if (window > TimeSpan.FromMinutes(1)) return Failed("pdf_document_deadline_invalid");
        if (original is null || original.Publication != publication || originalEvidence.Sha256 != publication.Content.Sha256) return Failed("pdf_document_source_mismatch");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(window);
        try
        {
            using var retained = original.AcquireInput(publication, 10 * 1024 * 1024); retained.RequireUnchanged();
            await using var pins = await PdfDocumentOcrPins.AcquireAsync(rasterOptions, ocrOptions, () => { _ = Remaining(); }, deadline.Token);
            var rendered = await raster.RasterizeAsync(original, publication, capturedDeadline, deadline.Token);
            Remaining(); retained.RequireUnchanged();
            if (rendered.Status != "rasterized") return Failed(rendered.FailureCode ?? "pdf_document_invalid_output");
            if (rendered.Original != publication || rendered.Provenance != raster.Provenance || rendered.Pages.Count is < 1 or > 10) return Failed("pdf_document_invalid_output");
            var pages = new List<PdfDocumentOcrPage>();
            foreach (var page in rendered.Pages)
            {
                pageIndex = page.PageIndex; var remaining = Remaining(); retained.RequireUnchanged();
                if (page.PageIndex != pages.Count) return Failed("pdf_document_invalid_output");
                if (!ocrOptions.Limits.AcceptsImage(page.Length, page.Width, page.Height, 1, 1)) return Failed("pdf_document_page_limit");
                var pageBudget = remaining < ocrOptions.Timeout ? remaining : ocrOptions.Timeout;
                if (pageBudget < TimeSpan.FromMilliseconds(100)) return Failed("pdf_document_deadline");
                var engine = new WindowsLocalOcrEngine(ocrOptions with { Timeout = pageBudget }, purpose);
                // This is only the existing image engine's internal integrity tuple. It is not a new persisted EvidenceRef.
                var pageEvidence = new EvidenceRef(originalEvidence.Id, originalEvidence.Version, page.Sha256);
                var pageJob = jobId + ":pdf-page:" + page.PageIndex;
                using var png = page.OpenPng();
                var extracted = await engine.ExtractAsync(pageJob, pageEvidence, purpose, png, deadline.Token);
                Remaining(); retained.RequireUnchanged();
                if (extracted.Status != "succeeded") return Failed(extracted.FailureCode ?? "pdf_document_invalid_output");
                if (extracted.JobId != pageJob || extracted.Evidence != pageEvidence || extracted.Purpose != purpose || extracted.Provenance != OcrProvenance ||
                    engine.LastPreprocessing is not { } preprocessing) return Failed("pdf_document_invalid_output");
                var lines = extracted.Lines.Select(line => new PdfDocumentOcrLine(line.Text, line.Confidence, line.Box, PdfDocumentOcrCoordinates.Map(page, line.Box))).ToArray();
                IReadOnlyList<PdfDocumentOcrField> fields; IReadOnlyList<string> quality;
                if (purpose == ExtractionPurpose.Payment)
                {
                    fields = Array.AsReadOnly(extracted.Fields.Select(field => Field(page, field.Field, field.Value, field.Confidence, field.BoundingBox, field.Flags)).ToArray());
                    quality = extracted.QualityFlags;
                }
                else
                {
                    var parsed = SupplierDocumentFieldExtractor.Extract(extracted.Lines);
                    fields = Array.AsReadOnly(parsed.Fields.Select(field => Field(page, field.Field, field.Value, field.Confidence, field.BoundingBox, field.Flags)).ToArray());
                    quality = parsed.QualityFlags;
                }
                pages.Add(new(page, extracted.Provenance, preprocessing, Array.AsReadOnly(lines), fields,
                    Array.AsReadOnly(page.ReviewReasons.Concat(preprocessing.ReviewReasons).Concat(quality).Distinct(StringComparer.Ordinal).ToArray())));
            }
            var reviewed = Review(pages, purpose); Remaining(); retained.RequireUnchanged();
            return new(jobId, originalEvidence, publication, purpose, rendered.Provenance, OcrProvenance, "succeeded", null, null, pages.AsReadOnly(), reviewed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failed("pdf_document_deadline"); }
        catch (TimeoutException) { return Failed("pdf_document_deadline"); }
        catch (ObjectDisposedException) { return Failed("pdf_document_source_mismatch"); }
        catch (StorageUnavailableException) { return Failed("pdf_document_source_mismatch"); }
        catch { return Failed("pdf_document_unavailable"); }

        PdfDocumentOcrResult Failed(string code) => new(jobId, originalEvidence, publication, purpose, raster.Provenance, OcrProvenance,
            "failed", code, pageIndex, Array.Empty<PdfDocumentOcrPage>(), Array.Empty<PdfDocumentOcrFieldReview>());
        TimeSpan Remaining()
        {
            deadline.Token.ThrowIfCancellationRequested(); var remaining = window - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero || DateTimeOffset.UtcNow >= capturedDeadline) throw new TimeoutException();
            return remaining;
        }
    }
    private static PdfDocumentOcrField Field(PdfRasterPage page, string name, string? value, decimal? confidence, IReadOnlyList<decimal>? values, IReadOnlyList<string> flags)
    {
        if (values is not null && values.Count != 4) throw new InvalidDataException();
        var box = values is null ? null : new NormalizedBox(values[0], values[1], values[2], values[3]);
        return new(name, value, confidence, box, box is null ? null : PdfDocumentOcrCoordinates.Map(page, box), Array.AsReadOnly(flags.ToArray()));
    }
    private static IReadOnlyList<PdfDocumentOcrFieldReview> Review(IReadOnlyList<PdfDocumentOcrPage> pages, ExtractionPurpose purpose)
    {
        var names = purpose == ExtractionPurpose.Payment ? PaymentFieldExtractor.Fields : SupplierDocumentFieldExtractor.Fields;
        return Array.AsReadOnly(names.Select(name => {
            var observations = pages.Select(page => new PdfDocumentOcrObservation(page.Raster.PageIndex, page.Fields.Single(field => field.Field == name))).ToArray();
            var reasons = new List<string> { "human_review_required" };
            if (observations.Any(value => value.Candidate.Value is null)) reasons.Add("unknown_on_page");
            if (observations.Any(value => value.Candidate.Flags.Contains("ambiguous"))) reasons.Add("ambiguous_on_page");
            var known = observations.Select(value => value.Candidate.Value).OfType<string>().ToArray();
            if (known.Distinct(StringComparer.Ordinal).Count() > 1) reasons.Add("contradictory_pages");
            else if (known.Length > 1) reasons.Add("repeated_across_pages");
            return new PdfDocumentOcrFieldReview(name, Array.AsReadOnly(observations), reasons.AsReadOnly());
        }).ToArray());
    }
}
