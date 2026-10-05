using System.Collections.Immutable;
using System.Globalization;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;

namespace UnitToko.Modules.Billing;

// Human observations of the image, kept separately from immutable OCR and buyer declarations.
public sealed record PaymentReviewField(string Field, string? Value);
public sealed record PaymentCorrectionBinding(long DeclarationRevision, long SubmissionId, EvidenceRef Evidence, long ExtractionId);
// Separate provenance for an explicitly human-reviewed supplementary original.
// It never substitutes the mandatory primary image or its immutable OCR identity.
public sealed record PaymentSupplementReviewObservation(string Field, long SupplementId, long SupplementRevision,
    PdfDocumentCandidateReference Candidate, bool OriginalReviewed);
public static class PaymentReviewCorrectionPolicy
{
    public static ImmutableArray<PaymentSupplementReviewObservation>? CopySupplementaryObservations(
        ImmutableArray<PaymentSupplementReviewObservation>? observations) {
        if (observations is null) return null;
        var values = observations.Value;
        if (values.IsDefaultOrEmpty || values.Length > PaymentFieldExtractor.Fields.Count || values.Any(value => value is null || value.Candidate is null ||
            !value.OriginalReviewed || value.SupplementRevision != 2 || value.Field != value.Candidate.Field ||
            !PaymentFieldExtractor.Fields.Contains(value.Field, StringComparer.Ordinal)) ||
            values.Select(value => value.Field).Distinct(StringComparer.Ordinal).Count() != values.Length) throw Invalid();
        foreach (var value in values) {
            PaymentSupplementRules.Id(value.SupplementId); PaymentSupplementRules.Id(value.Candidate.ResultId);
            PaymentSupplementRules.Reference(value.Candidate.Evidence);
        }
        return PaymentFieldExtractor.Fields.Where(name => values.Any(value => value.Field == name)).Select(name => {
            var value = values.Single(value => value.Field == name);
            return value with { Candidate = new(value.Candidate.ResultId, value.Candidate.ResultHash, value.Candidate.Evidence,
                value.Candidate.PageIndex, value.Candidate.Field) };
        }).ToImmutableArray();
    }
    public static ImmutableArray<PaymentReviewField> Copy(ImmutableArray<PaymentReviewField> fields) {
        if (fields.IsDefault || fields.Length != PaymentFieldExtractor.Fields.Count || fields.Any(field => field is null) ||
            fields.Select(field => field.Field).Distinct(StringComparer.Ordinal).Count() != fields.Length ||
            fields.Any(field => !PaymentFieldExtractor.Fields.Contains(field.Field, StringComparer.Ordinal))) throw Invalid();
        return [.. PaymentFieldExtractor.Fields.Select(name => {
            var field = fields.Single(field => field.Field == name); var value = field.Value;
            if (value is not null) {
                if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value != value.Trim() || value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format)) throw Invalid();
                if (name == "amount") value = Money.Parse(value).ToString();
                if (name == "currency" && value != "IDR") throw Invalid();
                if (name == "transfer_date" && !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw Invalid();
                if (name == "transfer_time" && !TimeOnly.TryParseExact(value, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw Invalid();
            }
            return new PaymentReviewField(name, value);
        })];
    }
    public static void Validate(PaymentCorrectionBinding binding, long revision) {
        if (binding is null || binding.DeclarationRevision <= 0 || binding.SubmissionId <= 0 || binding.ExtractionId <= 0 || binding.Evidence is null ||
            binding.Evidence.Id <= 0 || binding.Evidence.Version != 1 || binding.Evidence.Sha256 is null || binding.Evidence.Sha256.Length != 64 ||
            binding.Evidence.Sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) || revision is < 0 or >= long.MaxValue - 1) throw Invalid();
    }
    public static DomainValidationException Invalid() => new("invalid_payment_correction", "Use the current payment review and bounded observed fields");
}
