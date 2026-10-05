using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Storage;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;
using C = UnitToko.Platform.Procurement.ProcurementReceivingBson;

namespace UnitToko.Platform.Procurement;

internal static class SupplierDocumentBson
{
    internal const string Drafts = "supplierDocumentDrafts", Submissions = "supplierDocumentSubmissions", Matches = "supplierDocumentMatches",
        Identities = "supplierDocumentIdentities", Heads = "supplierDocumentHeads", InspectionHeads = "supplierDocumentInspectionHeads", OrderHeads = "supplierDocumentOrderHeads";
    internal const string DraftOperation = "procurement.supplierdocument.draft", SubmitOperation = "procurement.supplierdocument.submit", MatchOperation = "procurement.supplierdocument.match";
    internal static string Hash(BsonDocument row) => BankReceiptBson.Hash(row);
    internal static string DraftHash(BsonDocument value) { var row = value.DeepClone().AsBsonDocument; row.Remove("fence"); row.Remove("submittedDocumentId"); return Hash(row); }
    internal static string Kind(SupplierDocumentKind kind) => kind == SupplierDocumentKind.Invoice ? "invoice" : "sph";
    internal static BsonDocument Line(SupplierDocumentLineInput line) => new() { { "sourceLineReference", line.SourceLineReference }, { "description", line.Description },
        { "sourceSystem", C.Optional(line.SourceSystem) }, { "sourceAccount", C.Optional(line.SourceAccount) }, { "sourceCode", C.Optional(line.SourceCode) }, { "packages", line.Packages is { } count ? count : BsonNull.Value },
        { "netPricePerPackage", Amount(line.NetPricePerPackage) }, { "taxAmount", Amount(line.TaxAmount) }, { "chargeAmount", Amount(line.ChargeAmount) }, { "relatedInvoiceReference", C.Optional(line.RelatedInvoiceReference) }, { "statementAmount", Amount(line.StatementAmount) } };
    internal static BsonDocument Input(SupplierDocumentInput value) { value = SupplierDocumentRules.Copy(value); return new() { { "location", R.Location(value.Location) },
        { "anchorOrderId", value.AnchorOrderId }, { "anchorOrderVersion", value.AnchorOrderVersion }, { "anchorOrderApprovalId", value.AnchorOrderApprovalId }, { "kind", Kind(value.Kind) },
        { "documentReference", value.DocumentReference }, { "documentDate", value.DocumentDate is { } day ? InvoiceBson.Date(day) : BsonNull.Value }, { "currency", value.Currency }, { "documentTotal", Amount(value.DocumentTotal) }, { "lines", new BsonArray(value.Lines.Select(Line)) }, { "discrepancy", C.Optional(value.Discrepancy) } }; }
    internal static SupplierDocumentInput Input(BsonValue row) => SupplierDocumentRules.Copy(new SupplierDocumentInput(R.Location(row["location"]), row["anchorOrderId"].AsInt64,
        row["anchorOrderVersion"].AsInt64, row["anchorOrderApprovalId"].AsInt64, row["kind"] == "invoice" ? SupplierDocumentKind.Invoice : row["kind"] == "sph" ? SupplierDocumentKind.Sph : throw Broken(),
        row["documentReference"].AsString, row["documentDate"].IsBsonNull ? null : InvoiceBson.Date(row["documentDate"]), row["currency"].AsString, Amount(row["documentTotal"]),
        row["lines"].AsBsonArray.Select(line => new SupplierDocumentLineInput(line["sourceLineReference"].AsString, line["description"].AsString, C.Text(line["sourceSystem"]), C.Text(line["sourceAccount"]), C.Text(line["sourceCode"]),
            line["packages"].IsBsonNull ? null : line["packages"].AsInt32, Amount(line["netPricePerPackage"]), Amount(line["taxAmount"]), Amount(line["chargeAmount"]), C.Text(line["relatedInvoiceReference"]), Amount(line["statementAmount"]))).ToImmutableArray(), C.Text(row["discrepancy"])));
    internal static BsonValue Amount(Money? value) => value is { } money ? money.ToString() : BsonNull.Value;
    internal static Money? Amount(BsonValue value) => value.IsBsonNull ? null : Money.Parse(value.AsString);
    internal static BsonDocument MatchLine(SupplierDocumentMatchLineInput line) { var row = new BsonDocument { { "documentLineNumber", line.DocumentLineNumber }, { "packages", line.Packages is { } packages ? packages : BsonNull.Value },
        { "inspectionId", line.InspectionId is { } inspection ? inspection : BsonNull.Value }, { "inspectionLineNumber", line.InspectionLineNumber is { } number ? number : BsonNull.Value },
        { "invoiceMatchId", line.InvoiceMatchId is { } invoice ? invoice : BsonNull.Value }, { "invoiceMatchLineNumber", line.InvoiceMatchLineNumber is { } index ? index : BsonNull.Value },
        { "netAmount", Amount(line.NetAmount) }, { "taxAmount", Amount(line.TaxAmount) }, { "chargeAmount", Amount(line.ChargeAmount) }, { "statementAmount", Amount(line.StatementAmount) }, { "discrepancy", C.Optional(line.Discrepancy) } };
        if (line.QualityAllocationId is { } quality) row.Add("qualityAllocationId", quality);
        return row;
    }
    internal static BsonDocument PublicQuality(BsonDocument quality) => new() {
        { "allocationId", quality["allocationId"] }, { "proposalId", quality["proposalId"] }, { "decisionId", quality["decisionId"] },
        { "receiptId", quality["receiptId"] }, { "receiptReference", quality["receiptReference"] },
        { "acceptedPackages", (long)quality["acceptedPackages"].AsInt32 }, { "lotReference", quality["lotReference"] },
        { "expiresOn", quality["expiresOn"] }, { "acceptedCondition", quality["acceptedCondition"] },
        { "putawayReference", quality["putawayReference"] }, { "segregationReference", quality["segregationReference"] },
        { "recordedAt", quality["recordedAt"] } };
    internal static BsonArray PdfObservations(ImmutableArray<SupplierDocumentPdfMatchObservation> observations) =>
        new(SupplierDocumentRules.CopyPdfObservations(observations)!.Value.Select(value => new BsonDocument {
            { "field", value.Field }, { "candidate", PdfDocumentExtractionRecords.CandidateReference(value.Candidate) }, { "originalReviewed", true } }));
    internal static ImmutableArray<SupplierDocumentPdfMatchObservation>? PdfObservations(BsonDocument input) =>
        !input.TryGetValue("pdfObservations", out var values) ? null : SupplierDocumentRules.CopyPdfObservations(
            values.AsBsonArray.Select(value => new SupplierDocumentPdfMatchObservation(value["field"].AsString,
                new PdfDocumentCandidateReference(value["candidate"]["resultId"].AsInt64, value["candidate"]["resultHash"].AsString,
                    PaymentSubmissionBson.Reference(value["candidate"]["evidence"].AsBsonDocument), value["candidate"]["pageIndex"].AsInt32,
                    value["candidate"]["field"].AsString), value["originalReviewed"].AsBoolean)).ToImmutableArray());
    internal static BsonDocument Input(SupplierDocumentMatchInput input) {
        input = SupplierDocumentRules.Copy(input);
        var row = new BsonDocument { { "documentId", input.DocumentId }, { "documentRevision", input.DocumentRevision }, { "reviewedInput", Input(input.ReviewedInput) },
            { "ocrResultId", input.OcrResultId is { } ocr ? ocr : BsonNull.Value }, { "lines", new BsonArray(input.Lines.Select(MatchLine)) } };
        if (input.PdfObservations is not null) row.Add("pdfObservations", PdfObservations(input.PdfObservations.Value));
        return row;
    }
    internal static SupplierDocumentMatchInput MatchInput(BsonValue row) => SupplierDocumentRules.Copy(new SupplierDocumentMatchInput(row["documentId"].AsInt64, row["documentRevision"].AsInt64, Input(row["reviewedInput"]), row["ocrResultId"].IsBsonNull ? null : row["ocrResultId"].AsInt64,
        row["lines"].AsBsonArray.Select(line => new SupplierDocumentMatchLineInput(line["documentLineNumber"].AsInt32, line["packages"].IsBsonNull ? null : line["packages"].AsInt32,
            line["inspectionId"].IsBsonNull ? null : line["inspectionId"].AsInt64, line["inspectionLineNumber"].IsBsonNull ? null : line["inspectionLineNumber"].AsInt32,
            line["invoiceMatchId"].IsBsonNull ? null : line["invoiceMatchId"].AsInt64, line["invoiceMatchLineNumber"].IsBsonNull ? null : line["invoiceMatchLineNumber"].AsInt32,
            Amount(line["netAmount"]), Amount(line["taxAmount"]), Amount(line["chargeAmount"]), Amount(line["statementAmount"]), C.Text(line["discrepancy"]),
            line.AsBsonDocument.TryGetValue("qualityAllocationId", out var quality) ? quality.AsInt64 : null)).ToImmutableArray(), PdfObservations(row.AsBsonDocument)));
    internal static string IdentityKey(SupplierDocumentInput input, long supplier) => Hash(new() { { "organizationId", input.Location.OrganizationId }, { "shopId", input.Location.ShopId },
        { "channelId", input.Location.ChannelId }, { "supplierId", supplier }, { "kind", Kind(input.Kind) }, { "reference", input.DocumentReference } });
    internal static BsonDocument Capacity(BsonArray lines, long version = 0, IReadOnlyList<long>? matched = null) => new() { { "version", version },
        { "lines", new BsonArray(lines.Select((line, index) => new BsonDocument { { "lineNumber", index + 1 }, { "packages", line["packages"] },
            { "matchedPackages", matched?[index] ?? 0L }, { "remainingPackages", line["packages"].IsBsonNull ? BsonNull.Value : checked(line["packages"].AsInt32 - (matched?[index] ?? 0L)) },
            { "matchedNetAmount", "0.00" }, { "matchedTaxAmount", "0.00" }, { "matchedChargeAmount", "0.00" }, { "matchedStatementAmount", "0.00" } })) } };
    internal static BsonDocument After(BsonDocument before, SupplierDocumentMatchInput input) {
        var after = before.DeepClone().AsBsonDocument; after["version"] = checked(before["version"].AsInt64 + 1); if (after["version"].AsInt64 >= long.MaxValue - 1) throw Changed();
        foreach (var line in input.Lines.Where(line => line.Discrepancy is null)) {
            if (line.DocumentLineNumber > after["lines"].AsBsonArray.Count) throw Changed(); var capacity = after["lines"][line.DocumentLineNumber - 1].AsBsonDocument;
            var declared = input.ReviewedInput.Lines[line.DocumentLineNumber - 1];
            if (line.Packages is { } packages) {
                capacity["packages"] = declared.Packages!.Value; capacity["matchedPackages"] = checked(capacity["matchedPackages"].AsInt64 + packages);
                capacity["remainingPackages"] = checked(declared.Packages.Value - capacity["matchedPackages"].AsInt64); if (capacity["remainingPackages"].AsInt64 < 0) throw Changed("supplier_document_capacity_exceeded");
            }
            foreach (var (key, amount, maximum) in new[] { ("matchedNetAmount", line.NetAmount, declared.Packages is { } count && declared.NetPricePerPackage is { } price ? (Money?)Money.Multiply(price, count) : null),
                ("matchedTaxAmount", line.TaxAmount, declared.TaxAmount), ("matchedChargeAmount", line.ChargeAmount, declared.ChargeAmount), ("matchedStatementAmount", line.StatementAmount, declared.StatementAmount) }) {
                if (amount is not { } add) continue; var sum = Money.Add(Money.Parse(capacity[key].AsString), add); if (maximum is not { } limit || sum.Value > limit.Value) throw Changed("supplier_document_capacity_exceeded"); capacity[key] = sum.ToString();
            }
        }
        return after;
    }
    internal static object DraftBody(SupplierDocumentInput input, string binding) => new { input = SupplierDocumentRules.Copy(input), binding };
    internal static object SubmitBody(long draftId, long revision, long reservationId, EvidenceRef reference, string binding) => new { draftId, expectedRevision = revision, reservationId, reference, binding };
    internal static object MatchBody(SupplierDocumentMatchInput input, string binding) => new { input = SupplierDocumentRules.Copy(input), binding };
    internal static string Binding(long actor, long grants, BsonDocument input, BsonDocument source, BsonDocument state) => Hash(new() { { "actorId", actor }, { "actorGrantVersion", grants }, { "input", input }, { "source", source }, { "state", state } });
    internal static string SubmitBinding(long actor, long grants, string hash, long reservation, EvidenceRef reference) => Hash(new() { { "actorId", actor }, { "actorGrantVersion", grants }, { "draftHash", hash }, { "reservationId", reservation }, { "evidence", PaymentSubmissionBson.Reference(reference) } });
    internal static BsonDocument Result(BsonDocument row) => new() { { "operation", row["operation"] }, { "draftId", row["draftId"] },
        { "documentId", row["operation"] == DraftOperation ? BsonNull.Value : row["operation"] == SubmitOperation ? row["_id"] : row["documentId"] },
        { "matchId", row["operation"] == MatchOperation ? row["_id"] : BsonNull.Value }, { "version", 1L },
        { "state", row["operation"] == DraftOperation ? "draft" : row["operation"] == SubmitOperation ? "submitted_unmatched" : row["input"]["lines"].AsBsonArray.Any(line => !line["discrepancy"].IsBsonNull) ? "discrepancy_pending" : "match_recorded" }, { "recordedAt", row["recordedAt"] } };
    internal static string EffectKey(BsonDocument row) => row["operation"].AsString + ":" + row["_id"].AsInt64;
    internal static BsonDocument Audit(BsonDocument row) {
        var audit = new BsonDocument { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", row["operation"] }, { "actorId", row["actorId"] },
        { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] },
        { "location", row["location"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
        if (row["operation"] == MatchOperation && row["input"].AsBsonDocument.TryGetValue("pdfObservations", out var observations))
            audit.Add("pdfObservations", observations.DeepClone());
        return audit;
    }
    internal static string OcrKey(BsonDocument row) => "supplierdocument.ocr:" + row["_id"].AsInt64 + ":1";
    internal static BsonDocument OcrPayload(BsonDocument row) => new() { { "submissionId", row["_id"] }, { "draftId", row["draftId"] }, { "draftRevision", 1L }, { "evidence", row["evidence"] },
        { "pinId", row["pinId"] }, { "submissionHash", row["snapshotHash"] }, { "draftHash", row["draftHash"] } };
    internal static string DocumentOcrKey(BsonDocument row) => PdfDocumentExtractionRecords.SourceKey("SupplierInvoice", row["_id"].AsInt64);
    internal static BsonDocument DocumentOcrPayload(BsonDocument row, ExpectedPublication original) => PdfDocumentExtractionRecords.Payload("SupplierInvoice", new() {
        { "submissionId", row["_id"] }, { "draftId", row["draftId"] }, { "draftRevision", 1L }, { "uploaderId", row["actorId"] }, { "reservationId", row["reservationId"] },
        { "reference", row["evidence"] }, { "pinId", row["pinId"] }, { "submissionHash", row["snapshotHash"] }, { "draftHash", row["draftHash"] } }, original);
    internal static BsonDocument DraftView(BsonDocument row) => new() { { "id", row["_id"] }, { "version", 1L }, { "input", row["input"] }, { "supplierId", row["supplierId"] },
        { "order", PurchaseOrderService.FundingOrderView(row["source"].AsBsonDocument) }, { "recordedAt", row["recordedAt"] }, { "reason", row["reason"] }, { "state", row["submittedDocumentId"].IsBsonNull ? "draft" : "submitted_unmatched" } };
    internal static BsonDocument SubmissionView(BsonDocument row, bool evidence) => new() { { "id", row["_id"] }, { "draftId", row["draftId"] }, { "version", 1L }, { "state", "submitted_unmatched" },
        { "recordedAt", row["recordedAt"] }, { "reason", row["reason"] }, { "reservationId", evidence ? row["reservationId"] : BsonNull.Value }, { "evidence", evidence ? row["evidence"] : BsonNull.Value } };
    internal static BsonDocument MatchView(BsonDocument row, bool includeEvidence = true) {
        var input = row["input"].DeepClone().AsBsonDocument;
        if (!includeEvidence) input.Remove("pdfObservations");
        return new() { { "id", row["_id"] }, { "draftId", row["draftId"] }, { "documentId", row["documentId"] }, { "version", 1L },
            { "input", input }, { "before", row["before"] }, { "after", row["after"] }, { "lines", SupplierDocumentService.PublicLines(row["lines"].AsBsonArray) },
            { "state", Result(row)["state"] }, { "recordedAt", row["recordedAt"] }, { "reason", row["reason"] } };
    }
    internal static AuthorizationDeniedException Broken() => new("supplier_document_integrity_unavailable");
    internal static PersistenceConflictException Changed(string code = "supplier_document_changed") => new(code, "Reload the original supplier document and remaining inspected package quantities");
}
