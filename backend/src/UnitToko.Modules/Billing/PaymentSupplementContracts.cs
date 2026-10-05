using System.Collections.Immutable;
using System.Text;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;

namespace UnitToko.Modules.Billing;

// Additive draft. The primary submission is an existing immutable IMAGE association.
public sealed record PaymentSupplementPrimary(long DeclarationId, long DeclarationRevision, long SubmissionId, EvidenceRef PrimaryEvidence)
{
    public PaymentSupplementPrimary Validate() {
        PaymentSupplementRules.Id(DeclarationId); PaymentSupplementRules.Id(DeclarationRevision); PaymentSupplementRules.Id(SubmissionId);
        PaymentSupplementRules.Reference(PrimaryEvidence); return this;
    }
}
public sealed record PaymentSupplementAttachInput(long SupplementId, long ExpectedRevision, long ReservationId, EvidenceRef Evidence)
{
    public PaymentSupplementAttachInput Validate() {
        PaymentSupplementRules.Id(SupplementId); PaymentSupplementRules.Id(ReservationId); PaymentSupplementRules.Reference(Evidence);
        if (ExpectedRevision != 1) throw PaymentSupplementRules.Invalid(); return this;
    }
}
public sealed record PaymentSupplementView(long DeclarationId, long DeclarationRevision, long SubmissionId, EvidenceRef PrimaryEvidence,
    long SupplementId, long SupplementRevision, string State, long? ReservationId, EvidenceRef? PdfEvidence, DateTimeOffset? AttachedAt, string Reason);
public sealed record PaymentSupplementResult(string Operation, PaymentSupplementView Result);
public sealed record PaymentSupplementPage(ImmutableArray<PaymentSupplementView> Items, long? NextSupplementId,
    DateTimeOffset ObservedAt, string Readiness);
public sealed record PaymentSupplementOcrView(long DeclarationId, long DeclarationRevision, long SubmissionId, EvidenceRef PrimaryEvidence,
    long SupplementId, long SupplementRevision, EvidenceRef PdfEvidence, PdfDocumentExtractionView Ocr);

public static class PaymentSupplementRules
{
    public const int PageSize = 25, MaximumSupplements = 64;
    public static void Id(long value) { if (value is <= 0 or >= long.MaxValue - 1) throw Invalid(); }
    public static void Reference(EvidenceRef? value) {
        if (value is null) throw Invalid(); Id(value.Id); if (value.Version != 1) throw Invalid();
    }
    public static void Text(string? value, int maximum) {
        if (value is null || maximum < 1 || value.Length is < 1 || value.Length > checked(maximum * 2) || value != value.Trim()) throw Invalid();
        var scalars = 0;
        for (var offset = 0; offset < value.Length;) {
            if (Rune.DecodeFromUtf16(value.AsSpan(offset), out var rune, out var consumed) != System.Buffers.OperationStatus.Done ||
                Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format ||
                ++scalars > maximum) throw Invalid();
            offset += consumed;
        }
        if (!value.IsNormalized(NormalizationForm.FormC)) throw Invalid();
    }
    public static DomainValidationException Invalid() => new("invalid_payment_supplement",
        "Lampiran PDF harus terhubung ke pengajuan dengan gambar utama yang sah; lampiran tidak menyatakan pembayaran telah disetujui.");
}
