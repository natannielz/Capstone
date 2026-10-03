using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal static class ProcurementReceivingBson
{
    internal const string Notices = "procurementArrivalNotices", Heads = "procurementContainerHeads", Intakes = "procurementContainerIntakes";
    internal const string NoticeOperation = "procurement.receiving.notice", IntakeOperation = "procurement.receiving.intake", Prefix = "procurement.receiving.";
    internal static string Hash(BsonDocument value) => BankReceiptBson.Hash(value);
    internal static BsonValue Optional(string? value) => value is null ? BsonNull.Value : value;
    internal static string? Text(BsonValue value) => value.IsBsonNull ? null : value.AsString;
    internal static string Permission(string operation) => operation switch {
        NoticeOperation => Permissions.ProcurementPrepare, IntakeOperation => Permissions.ProcurementContainerReceive, _ => throw ProcurementReceivingRules.Invalid() };
    internal static BsonDocument Input(ProcurementArrivalNoticeInput input) { input = ProcurementReceivingRules.Copy(input); return new() {
        { "location", R.Location(input.Location) }, { "orderId", input.OrderId }, { "orderVersion", input.OrderVersion }, { "orderApprovalId", input.OrderApprovalId },
        { "noticeDate", InvoiceBson.Date(input.NoticeDate) }, { "supplierDocumentReference", input.SupplierDocumentReference },
        { "ddoRequestReference", Optional(input.DdoRequestReference) }, { "ddoTelephoneReference", Optional(input.DdoTelephoneReference) },
        { "declaredContainers", input.DeclaredContainers }, { "note", input.Note } }; }
    internal static ProcurementArrivalNoticeInput NoticeInput(BsonValue row) => ProcurementReceivingRules.Copy(new ProcurementArrivalNoticeInput(R.Location(row["location"]), row["orderId"].AsInt64,
        row["orderVersion"].AsInt64, row["orderApprovalId"].AsInt64, InvoiceBson.Date(row["noticeDate"]), row["supplierDocumentReference"].AsString,
        Text(row["ddoRequestReference"]), Text(row["ddoTelephoneReference"]), row["declaredContainers"].AsInt32, row["note"].AsString));
    internal static BsonDocument Input(ProcurementContainerIntakeInput input) { input = ProcurementReceivingRules.Copy(input); return new() {
        { "receivedDate", InvoiceBson.Date(input.ReceivedDate) }, { "intakeReference", input.IntakeReference }, { "matchedContainers", input.MatchedContainers },
        { "excessContainers", input.ExcessContainers }, { "heldContainers", input.HeldContainers }, { "condition", input.Condition }, { "discrepancy", Optional(input.Discrepancy) } }; }
    internal static ProcurementContainerIntakeInput IntakeInput(BsonValue row) => ProcurementReceivingRules.Copy(new ProcurementContainerIntakeInput(InvoiceBson.Date(row["receivedDate"]), row["intakeReference"].AsString,
        row["matchedContainers"].AsInt32, row["excessContainers"].AsInt32, row["heldContainers"].AsInt32, row["condition"].AsString, Text(row["discrepancy"])));
    internal static string NoticeKey(ProcurementArrivalNoticeInput input, long supplierId) => Hash(new() { { "location", R.Location(input.Location) },
        { "supplierId", supplierId }, { "orderId", input.OrderId }, { "reference", input.SupplierDocumentReference } });
    internal static object NoticeBody(ProcurementArrivalNoticeInput input, string binding) => new { input = ProcurementReceivingRules.Copy(input), binding };
    internal static object IntakeBody(long noticeId, long revision, ProcurementContainerIntakeInput input, string binding) => new { noticeId, expectedRevision = revision, input = ProcurementReceivingRules.Copy(input), binding };
    internal static string NoticeBinding(long actor, long grants, ProcurementArrivalNoticeInput input, BsonDocument source, BsonDocument profiles) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grants }, { "input", Input(input) }, { "source", source }, { "sources", profiles } });
    internal static string IntakeBinding(long actor, long grants, BsonDocument notice, BsonDocument capacity, ProcurementContainerIntakeInput input) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grants }, { "noticeHash", notice["snapshotHash"] }, { "capacity", capacity }, { "input", Input(input) } });
    internal static BsonDocument Capacity(int declared, long version = 0, int matched = 0, long excess = 0, long held = 0) => new() {
        { "version", version }, { "declaredContainers", declared }, { "matchedContainers", matched }, { "remainingContainers", declared - matched },
        { "excessContainers", excess }, { "heldContainers", held }, { "receivedContainers", checked((long)matched + excess) } };
    internal static BsonDocument After(BsonDocument before, ProcurementContainerIntakeInput input) {
        input = ProcurementReceivingRules.Copy(input);
        if (input.MatchedContainers > before["remainingContainers"].AsInt32) throw Changed("procurement_receiving_capacity_exceeded");
        try {
            var result = Capacity(before["declaredContainers"].AsInt32, checked(before["version"].AsInt64 + 1),
                checked(before["matchedContainers"].AsInt32 + input.MatchedContainers), checked(before["excessContainers"].AsInt64 + input.ExcessContainers),
                checked(before["heldContainers"].AsInt64 + input.HeldContainers));
            if (new[] { "version", "excessContainers", "heldContainers", "receivedContainers" }.Any(field => result[field].AsInt64 >= long.MaxValue - 1)) throw Changed();
            return result;
        } catch (OverflowException) { throw Changed(); }
    }
    internal static BsonDocument Result(BsonDocument row) { var notice = row["operation"] == NoticeOperation; return new() {
        { "operation", row["operation"] }, { "noticeId", notice ? row["_id"] : row["noticeId"] }, { "receiptId", notice ? BsonNull.Value : row["_id"] },
        { "version", notice ? (BsonValue)0L : row["version"] }, { "state", notice ? "notice_recorded" : "pending_inspection" }, { "recordedAt", row["recordedAt"] } }; }
    internal static BsonDocument NoticeView(BsonDocument row) => new() { { "id", row["_id"] }, { "version", row["version"] }, { "input", row["input"] },
        { "order", PurchaseOrderService.FundingOrderView(row["source"].AsBsonDocument) }, { "source", R.SourceView(row["sources"].AsBsonDocument) },
        { "recordedAt", row["recordedAt"] }, { "reason", row["reason"] }, { "state", "notice_recorded" } };
    internal static BsonDocument IntakeView(BsonDocument row) => new() { { "id", row["_id"] }, { "noticeId", row["noticeId"] }, { "version", row["version"] },
        { "input", row["input"] }, { "recordedAt", row["recordedAt"] }, { "reason", row["reason"] }, { "state", "pending_inspection" } };
    internal static string EffectKey(BsonDocument row) => row["operation"].AsString + ":" + row["_id"].AsInt64;
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", row["operation"] },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] },
        { "snapshotHash", row["snapshotHash"] }, { "location", row["location"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] },
        { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static AuthorizationDeniedException Broken() => new("procurement_receiving_integrity_unavailable");
    internal static PersistenceConflictException Changed(string code = "procurement_receiving_changed") => new(code, "Reload the actual purchase order, notice and container receipt history");
}
