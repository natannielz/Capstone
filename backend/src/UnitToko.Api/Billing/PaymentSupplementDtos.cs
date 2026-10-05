using System.Collections.Immutable;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;

namespace UnitToko.Api.Billing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentSupplementCreateDto([property: JsonRequired] LongId SubmissionId,
    [property: JsonRequired] PaymentEvidenceReferenceDto PrimaryEvidence, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentSupplementAttachDto([property: JsonRequired] LongId ReservationId,
    [property: JsonRequired] PaymentEvidenceReferenceDto Evidence, [property: JsonRequired] string Reason);
public sealed record PaymentSupplementViewDto(LongId DeclarationId, string DeclarationRevision, LongId SubmissionId,
    PaymentEvidenceReferenceDto PrimaryEvidence, LongId SupplementId, string SupplementRevision, string State,
    LongId? ReservationId, PaymentEvidenceReferenceDto? PdfEvidence, DateTimeOffset? AttachedAt, string Reason);
public sealed record PaymentSupplementResultDto(string Operation, PaymentSupplementViewDto Result);
public sealed record PaymentSupplementRecoveryDto(string Status, PaymentSupplementViewDto? Result);
public sealed record PaymentSupplementPageDto(ImmutableArray<PaymentSupplementViewDto> Items, LongId? NextSupplementId,
    DateTimeOffset ObservedAt, string Readiness);
public sealed record PaymentSupplementOcrDto(LongId DeclarationId, string DeclarationRevision, LongId SubmissionId,
    PaymentEvidenceReferenceDto PrimaryEvidence, LongId SupplementId, string SupplementRevision,
    PaymentEvidenceReferenceDto PdfEvidence, PdfOcrViewDto Ocr);

internal static class PaymentSupplementDtos
{
    internal const string CreateOperation = "billing.payment.supplement.create", AttachOperation = "billing.payment.supplement.attach";
    internal static EvidenceRef Reference(PaymentEvidenceReferenceDto? value) {
        if (value is null) throw PaymentSupplementRules.Invalid();
        var reference = new EvidenceRef(value.Id.Value, LongId.Parse(value.Version).Value, value.Sha256);
        PaymentSupplementRules.Reference(reference); return reference;
    }
    internal static PaymentEvidenceReferenceDto Reference(EvidenceRef value) => new(new(value.Id), OrderDraftInputDto.Number(value.Version), value.Sha256);
    internal static PaymentSupplementViewDto View(PaymentSupplementView value) {
        new PaymentSupplementPrimary(value.DeclarationId, value.DeclarationRevision, value.SubmissionId, value.PrimaryEvidence).Validate();
        PaymentSupplementRules.Id(value.SupplementId); PaymentSupplementRules.Text(value.Reason, 1000);
        var draft = value.State == "draft" && value.SupplementRevision == 1 && value.ReservationId is null && value.PdfEvidence is null && value.AttachedAt is null;
        var attached = value.State == "attached" && value.SupplementRevision == 2 && value.ReservationId is not null && value.PdfEvidence is not null && value.AttachedAt is not null;
        if (!draft && !attached) throw PaymentSupplementRules.Invalid();
        if (value.ReservationId is { } reserved) PaymentSupplementRules.Id(reserved);
        if (value.PdfEvidence is { } pdf) {PaymentSupplementRules.Reference(pdf); if (pdf.Id == value.PrimaryEvidence.Id || pdf.Sha256 == value.PrimaryEvidence.Sha256) throw PaymentSupplementRules.Invalid();}
        return new(new(value.DeclarationId), OrderDraftInputDto.Number(value.DeclarationRevision), new(value.SubmissionId), Reference(value.PrimaryEvidence),
            new(value.SupplementId), OrderDraftInputDto.Number(value.SupplementRevision), value.State,
            value.ReservationId is { } reservation ? new LongId(reservation) : null, value.PdfEvidence is { } evidence ? Reference(evidence) : null,
            value.AttachedAt, value.Reason);
    }
    internal static PaymentSupplementViewDto View(BsonDocument row) {
        var reference = row["primaryEvidence"].AsBsonDocument;
        var pdf = row["pdfEvidence"].IsBsonNull ? null : row["pdfEvidence"].AsBsonDocument;
        return View(new PaymentSupplementView(row["declarationId"].AsInt64, row["declarationRevision"].AsInt64, row["submissionId"].AsInt64,
            new(reference["id"].AsInt64, reference["version"].AsInt64, reference["sha256"].AsString),
            row["supplementId"].AsInt64, row["supplementRevision"].AsInt64, row["state"].AsString,
            row["reservationId"].IsBsonNull ? null : row["reservationId"].AsInt64,
            pdf is null ? null : new EvidenceRef(pdf["id"].AsInt64, pdf["version"].AsInt64, pdf["sha256"].AsString),
            row["attachedAt"].IsBsonNull ? null : new DateTimeOffset(row["attachedAt"].ToUniversalTime()), row["reason"].AsString));
    }
    internal static PaymentSupplementOcrDto Ocr(PaymentSupplementOcrView value) {
        new PaymentSupplementPrimary(value.DeclarationId, value.DeclarationRevision, value.SubmissionId, value.PrimaryEvidence).Validate();
        PaymentSupplementRules.Id(value.SupplementId); PaymentSupplementRules.Reference(value.PdfEvidence);
        if (value.SupplementRevision != 2 || value.Ocr.SourceKind != "PaymentSupplement" || value.Ocr.SourceId != value.SupplementId ||
            value.Ocr.SourceRevision != 2 || value.Ocr.SubmissionId != value.SupplementId || value.Ocr.Evidence != value.PdfEvidence ||
            value.PdfEvidence.Id == value.PrimaryEvidence.Id || value.PdfEvidence.Sha256 == value.PrimaryEvidence.Sha256) throw PaymentSupplementRules.Invalid();
        return new(new(value.DeclarationId), OrderDraftInputDto.Number(value.DeclarationRevision), new(value.SubmissionId), Reference(value.PrimaryEvidence),
            new(value.SupplementId), "2", Reference(value.PdfEvidence), PdfDocumentOcrDtos.View(value.Ocr));
    }
}
