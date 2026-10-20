using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Organizations;

namespace UnitToko.Platform.Billing;

internal static class InvoiceReleaseBson
{
    internal const string Heads = "invoiceReleaseHeads", Events = "invoiceReleaseEvents", Bindings = "invoiceReleaseBindings", Anchors = "invoiceReleaseMigrationAnchors";
    internal const string Prefix = "billing.invoice.release.", ReadPermission = "invoice.release.read",
        ProposePermission = "invoice.release.propose", ApprovePermission = "invoice.release.approve";
    // The issuer continues to use the existing immutable InvoiceRenderer. This named
    // component is approved explicitly; it is not a replacement for a tax document.
    internal const string TemplateComponent = "unit-toko-invoice-html-v1", NumberingComponent = "entity-channel-year-sequence-v1";
    internal static DateTimeOffset Time(BsonValue value) => new(value.ToUniversalTime());
    internal static string Hash(BsonDocument value) => CatalogRepository.Digest(new BsonDocument(value.Where(field => field.Name is not ("recordHash" or "fence"))));
    internal static BsonArray Selected(IEnumerable<InvoiceReceiptSelection> values) => new(InvoicePolicy.Copy(values).Select(value => new BsonDocument {
        { "shipmentId", value.ShipmentId.Value }, { "receiptId", value.ReceiptId.Value } }));
    internal static ImmutableArray<InvoiceReceiptSelection> Selected(BsonValue value) => InvoicePolicy.Copy(value.AsBsonArray.Select(row =>
        new InvoiceReceiptSelection(new(row["shipmentId"].AsInt64), new(row["receiptId"].AsInt64))));
    internal static string SelectionKey(IEnumerable<InvoiceReceiptSelection> values) => Hash(new("receipts", Selected(values)));
    internal static BsonDocument Mappings(InvoiceReleaseMappings value) => new() {
        { "receivable", value.Receivable }, { "revenue", value.Revenue }, { "tax", value.Tax }, { "charges", value.Charges } };
    internal static InvoiceReleaseMappings Mappings(BsonValue value) => new(value["receivable"].AsString, value["revenue"].AsString,
        value["tax"].AsString, value["charges"].AsString);
    internal static BsonArray Allocations(ImmutableArray<InvoiceReleaseAllocation> values) => new(values.Select(line => new BsonDocument {
        { "receiptId", line.ReceiptId.Value }, { "skuId", line.SkuId.Value }, { "lineRevision", line.LineRevision },
        { "tax", InvoiceBson.Money(line.Tax) }, { "charges", InvoiceBson.Money(line.Charges) } }));
    internal static BsonDocument Input(InvoiceReleaseInput value) {
        value = InvoiceReleaseRules.Copy(value); return new() {
            { "receipts", Selected(value.Receipts) }, { "expectedReleaseRevision", value.ExpectedReleaseRevision },
            { "periodFrom", InvoiceBson.Date(value.PeriodFrom) }, { "periodTo", InvoiceBson.Date(value.PeriodTo) },
            { "validFrom", value.ValidFrom.UtcDateTime }, { "validUntil", value.ValidUntil.UtcDateTime },
            { "templateApprovalReference", value.TemplateApprovalReference }, { "numberingApprovalReference", value.NumberingApprovalReference },
            { "eligibilityReviewReference", value.EligibilityReviewReference }, { "mappings", Mappings(value.Mappings) }, { "allocations", Allocations(value.Allocations) } };
    }
    internal static InvoiceReleaseInput Input(BsonValue row) => InvoiceReleaseRules.Copy(new(Selected(row["receipts"]), row["expectedReleaseRevision"].AsInt64,
        InvoiceBson.Date(row["periodFrom"]), InvoiceBson.Date(row["periodTo"]), Time(row["validFrom"]), Time(row["validUntil"]),
        row["templateApprovalReference"].AsString, row["numberingApprovalReference"].AsString, row["eligibilityReviewReference"].AsString,
        Mappings(row["mappings"]), [.. row["allocations"].AsBsonArray.Select(line => new InvoiceReleaseAllocation(new(line["receiptId"].AsInt64),
            new(line["skuId"].AsInt64), line["lineRevision"].AsInt64, InvoiceBson.Money(line["tax"]), InvoiceBson.Money(line["charges"])))]));
    internal static BsonDocument Person(PersonEvidence value) => OrganizationAddressBson.Person(value);
    internal static PersonEvidence Person(BsonValue value) => OrganizationAddressBson.Person(value);
    internal static string Permission(string action) => action switch {
        "propose" => ProposePermission, "approve" or "reject" => ApprovePermission, _ => throw InvoiceReleaseRules.Invalid() };
    internal static string Decision(InvoiceReleaseDecision decision) => InvoiceReleaseRules.Decision(decision);
    internal static object ProposeBody(InvoiceReleaseInput input, string binding) => new { input = InvoiceReleaseRules.Copy(input), binding };
    internal static object DecideBody(long proposalId, long expectedRevision, InvoiceReleaseDecision decision, string binding) =>
        new { proposalId, expectedRevision, decision = Decision(decision), binding };
    internal static object Body(BsonDocument row) => row["action"] == "propose"
        ? ProposeBody(Input(row["input"]), row["binding"].AsString)
        : DecideBody(row["proposalId"].AsInt64, row["previousRevision"].AsInt64,
            row["action"] == "approve" ? InvoiceReleaseDecision.Approve : InvoiceReleaseDecision.Reject, row["binding"].AsString);
    internal static string ProposalBinding(ActorContext actor, InvoiceReleaseInput input, BsonDocument source) => ProposalBinding(actor.UserId, actor.GrantVersion, input, source);
    internal static string ProposalBinding(long actorId, long grantVersion, InvoiceReleaseInput input, BsonDocument source) => Hash(new() {
        { "actorId", actorId }, { "actorGrantVersion", grantVersion }, { "input", Input(input) }, { "source", source },
        { "templateComponent", TemplateComponent }, { "numberingComponent", NumberingComponent } });
    internal static string DecisionBinding(ActorContext actor, BsonDocument proposal, InvoiceReleaseDecision decision, BsonDocument source) => DecisionBinding(actor.UserId, actor.GrantVersion, proposal, decision, source);
    internal static string DecisionBinding(long actorId, long grantVersion, BsonDocument proposal, InvoiceReleaseDecision decision, BsonDocument source) => Hash(new() {
        { "actorId", actorId }, { "actorGrantVersion", grantVersion }, { "proposalId", proposal["_id"] },
        { "proposalHash", proposal["recordHash"] }, { "decision", Decision(decision) }, { "source", source } });
    internal static void RequireHash(string value) {
        if (value is not { Length: 64 } || value.Any(character => !char.IsAsciiHexDigitLower(character))) throw InvoiceReleaseRules.Invalid();
    }
    internal static void Metadata(BsonDocument row, ActorContext actor, AuthorizationCommandMetadata metadata, long auditId, DateTimeOffset now, object body) {
        row.Add("operation", Prefix + row["action"].AsString); row.Add("actorId", actor.UserId); row.Add("actorGrantVersion", actor.GrantVersion);
        row.Add("recordedAt", now.UtcDateTime); row.Add("auditId", auditId); row.Add("requestKey", metadata.IdempotencyKey);
        row.Add("reason", metadata.Reason); row.Add("correlationId", metadata.CorrelationId); row.Add("bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason));
        row.Add("recordHash", Hash(row));
    }
    internal static BsonDocument Result(BsonDocument row) => new() { { "releaseId", row["releaseId"] }, { "recordId", row["_id"] },
        { "proposalId", row["proposalId"] }, { "revision", row["revision"] }, { "action", row["action"] }, { "selectionKey", row["selectionKey"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", row["operation"] },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] },
        { "bodyHash", row["bodyHash"] }, { "recordHash", row["recordHash"] }, { "correlationId", row["correlationId"] },
        { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => Prefix + "command:" + row["auditId"].AsInt64;
    internal static PersistenceConflictException Broken() => new("invoice_release_integrity_unavailable", "The recorded invoice release and its complete approval history are unavailable");
    internal static PersistenceConflictException Changed() => new("invoice_release_changed", "Reload the exact receipt, release, commercial and accounting sources before continuing");
}
