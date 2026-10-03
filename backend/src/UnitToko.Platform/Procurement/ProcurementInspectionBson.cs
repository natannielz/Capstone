using MongoDB.Bson;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using System.Collections.Immutable;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;
using C = UnitToko.Platform.Procurement.ProcurementReceivingBson;

namespace UnitToko.Platform.Procurement;

internal static class ProcurementInspectionBson
{
    internal const string Drafts = "procurementInspectionDrafts", Submissions = "procurementInspectionSubmissions", Heads = "procurementInspectionHeads";
    internal const string DraftOperation = "procurement.inspection.draft", SubmitOperation = "procurement.inspection.submit";
    internal static string Hash(BsonDocument value) => BankReceiptBson.Hash(value);
    internal static string DraftHash(BsonDocument value) { var row = value.DeepClone().AsBsonDocument; row.Remove("fence"); row.Remove("submittedInspectionId"); return Hash(row); }
    internal static BsonDocument Line(ProcurementInspectionLineInput line) => new() {
        { "requestSkuId", line.RequestSkuId }, { "definitionId", line.DefinitionId }, { "definitionVersion", line.DefinitionVersion },
        { "declaredPackages", line.DeclaredPackages }, { "receivedPackages", line.ReceivedPackages }, { "damagedPackages", line.DamagedPackages },
        { "expiredPackages", line.ExpiredPackages }, { "openedPackages", line.OpenedPackages }, { "lotCode", C.Optional(line.LotCode) },
        { "expiryDate", line.ExpiryDate is { } day ? InvoiceBson.Date(day) : BsonNull.Value }, { "putawayReference", C.Optional(line.PutawayReference) }, { "discrepancy", C.Optional(line.Discrepancy) } };
    internal static BsonDocument Input(ProcurementInspectionInput input) { input = ProcurementInspectionRules.Copy(input); return new() {
        { "location", R.Location(input.Location) }, { "noticeId", input.NoticeId }, { "intakeId", input.IntakeId }, { "inspectionDate", InvoiceBson.Date(input.InspectionDate) },
        { "inspectionReference", input.InspectionReference }, { "inspectedContainers", input.InspectedContainers }, { "lines", new BsonArray(input.Lines.Select(Line)) }, { "discrepancy", C.Optional(input.Discrepancy) } }; }
    internal static ProcurementInspectionInput Input(BsonValue row) => ProcurementInspectionRules.Copy(new ProcurementInspectionInput(R.Location(row["location"]), row["noticeId"].AsInt64,
        row["intakeId"].AsInt64, InvoiceBson.Date(row["inspectionDate"]), row["inspectionReference"].AsString, row["inspectedContainers"].AsInt32,
        row["lines"].AsBsonArray.Select(line => new ProcurementInspectionLineInput(line["requestSkuId"].AsInt64, line["definitionId"].AsInt64, line["definitionVersion"].AsInt64,
            line["declaredPackages"].AsInt32, line["receivedPackages"].AsInt32, line["damagedPackages"].AsInt32, line["expiredPackages"].AsInt32, line["openedPackages"].AsInt32,
            C.Text(line["lotCode"]), line["expiryDate"].IsBsonNull ? null : InvoiceBson.Date(line["expiryDate"]), C.Text(line["putawayReference"]), C.Text(line["discrepancy"]))).ToImmutableArray(), C.Text(row["discrepancy"])));
    internal static BsonDocument Capacity(long received, long version = 0, long inspected = 0) => new() {
        { "version", version }, { "receivedContainers", received }, { "inspectedContainers", inspected }, { "remainingContainers", checked(received - inspected) } };
    internal static BsonDocument After(BsonDocument capacity, int inspected) {
        if (inspected <= 0 || inspected > capacity["remainingContainers"].AsInt64 || capacity["version"].AsInt64 >= long.MaxValue - 2) throw Changed("procurement_inspection_capacity_exceeded");
        return Capacity(capacity["receivedContainers"].AsInt64, capacity["version"].AsInt64 + 1, checked(capacity["inspectedContainers"].AsInt64 + inspected));
    }
    internal static object DraftBody(ProcurementInspectionInput input, string binding) => new { input = ProcurementInspectionRules.Copy(input), binding };
    internal static object SubmitBody(long draftId, long revision, long reservationId, EvidenceRef reference, string binding) => new { draftId, expectedRevision = revision, reservationId, reference, binding };
    internal static string Binding(long actor, long grants, ProcurementInspectionInput input, BsonDocument source, BsonDocument capacity) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grants }, { "input", Input(input) }, { "source", source }, { "capacity", capacity } });
    internal static string SubmitBinding(long actor, long grants, string draftHash, long reservationId, EvidenceRef reference, BsonDocument capacity) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grants }, { "draftHash", draftHash }, { "reservationId", reservationId }, { "evidence", PaymentSubmissionBson.Reference(reference) }, { "capacity", capacity } });
    internal static BsonDocument Result(BsonDocument row) => new() { { "operation", row["operation"] }, { "draftId", row["draftId"] },
        { "inspectionId", row["operation"] == DraftOperation ? BsonNull.Value : row["_id"] }, { "version", 1L },
        { "state", row["operation"] == DraftOperation ? "draft" : "submitted_quarantine" }, { "recordedAt", row["recordedAt"] } };
    internal static string EffectKey(BsonDocument row) => row["operation"].AsString + ":" + row["_id"].AsInt64;
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", row["operation"] },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] },
        { "snapshotHash", row["snapshotHash"] }, { "location", row["location"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] },
        { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static BsonDocument SourceView(BsonDocument source) => new() { { "notice", C.NoticeView(source["notice"].AsBsonDocument) }, { "intake", C.IntakeView(source["intake"].AsBsonDocument) } };
    internal static BsonDocument DraftView(BsonDocument row) { var view = SourceView(row["source"].AsBsonDocument); view.AddRange(new BsonDocument {
        { "id", row["_id"] }, { "version", 1L }, { "input", row["input"] }, { "capacity", row["capacity"] }, { "reason", row["reason"] }, { "recordedAt", row["recordedAt"] },
        { "state", row["submittedInspectionId"].IsBsonNull ? "draft" : "submitted_quarantine" } }); return view; }
    internal static BsonDocument SubmissionView(BsonDocument row, bool evidence) => new() { { "id", row["_id"] }, { "draftId", row["draftId"] }, { "version", 1L },
        { "input", row["input"] }, { "before", row["before"] }, { "after", row["after"] }, { "reason", row["reason"] }, { "recordedAt", row["recordedAt"] }, { "state", "submitted_quarantine" },
        { "reservationId", evidence ? row["reservationId"] : BsonNull.Value }, { "evidence", evidence ? row["evidence"] : BsonNull.Value } };
    internal static AuthorizationDeniedException Broken() => new("procurement_inspection_integrity_unavailable");
    internal static PersistenceConflictException Changed(string code = "procurement_inspection_changed") => new(code, "Reload the actual intake, inspection capacity and private video review");
}
