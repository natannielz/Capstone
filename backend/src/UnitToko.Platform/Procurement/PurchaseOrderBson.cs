using System.Globalization;
using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal static class PurchaseOrderBson
{
    internal const string Proposals = "purchaseOrderProposals", Decisions = "purchaseOrderDecisions", Orders = "purchaseOrders", Prefix = "procurement.purchase.order.";
    internal static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    internal static DateOnly Date(BsonValue value) => DateOnly.ParseExact(value.AsString, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    internal static BsonDocument Input(PurchaseOrderInput value) { value = PurchaseOrderRules.Copy(value); return new() {
        { "location", R.Location(value.Location) }, { "requestId", value.RequestId }, { "requestVersion", value.RequestVersion }, { "requestDecisionId", value.RequestDecisionId },
        { "supplierId", value.SupplierId }, { "supplierVersionId", value.SupplierVersionId }, { "supplierVersion", value.SupplierVersion }, { "businessDate", Date(value.BusinessDate) }, { "currency", value.Currency },
        { "commercialReference", new BsonDocument { { "kind", value.CommercialReference.Kind }, { "reference", value.CommercialReference.Reference }, { "issuedDate", Date(value.CommercialReference.IssuedDate) }, { "validUntil", value.CommercialReference.ValidUntil.UtcDateTime } } },
        { "externalOrderReference", value.ExternalOrderReference is null ? BsonNull.Value : value.ExternalOrderReference },
        { "paymentTerms", new BsonDocument { { "kind", value.PaymentTerms.Kind }, { "description", value.PaymentTerms.Description }, { "requiredAdvance", value.PaymentTerms.RequiredAdvance.ToString() },
            { "balanceTrigger", value.PaymentTerms.BalanceTrigger }, { "dueDate", value.PaymentTerms.DueDate is { } due ? Date(due) : BsonNull.Value } } },
        { "lines", new BsonArray(value.Lines.Select(line => new BsonDocument { { "requestSkuId", line.RequestSkuId }, { "packages", line.Packages }, { "sourceSystem", line.SourceSystem },
            { "sourceAccountReference", line.SourceAccountReference }, { "externalItemCode", line.ExternalItemCode }, { "definitionId", line.DefinitionId }, { "definitionVersion", line.DefinitionVersion },
            { "netPricePerPackage", line.NetPricePerPackage.ToString() }, { "taxAmount", line.TaxAmount.ToString() }, { "supplierChargeAmount", line.SupplierChargeAmount.ToString() } })) } }; }
    internal static PurchaseOrderInput Input(BsonValue value) => PurchaseOrderRules.Copy(new(R.Location(value["location"]), value["requestId"].AsInt64, value["requestVersion"].AsInt64, value["requestDecisionId"].AsInt64,
        value["supplierId"].AsInt64, value["supplierVersionId"].AsInt64, value["supplierVersion"].AsInt64, Date(value["businessDate"]), value["currency"].AsString,
        new(value["commercialReference"]["kind"].AsString, value["commercialReference"]["reference"].AsString, Date(value["commercialReference"]["issuedDate"]), new(value["commercialReference"]["validUntil"].ToUniversalTime())),
        value["externalOrderReference"].IsBsonNull ? null : value["externalOrderReference"].AsString,
        new(value["paymentTerms"]["kind"].AsString, value["paymentTerms"]["description"].AsString, Money.Parse(value["paymentTerms"]["requiredAdvance"].AsString),
            value["paymentTerms"]["balanceTrigger"].AsString, value["paymentTerms"]["dueDate"].IsBsonNull ? null : Date(value["paymentTerms"]["dueDate"])),
        [.. value["lines"].AsBsonArray.Select(line => new PurchaseOrderLineInput(line["requestSkuId"].AsInt64, line["packages"].AsInt32, line["sourceSystem"].AsString,
            line["sourceAccountReference"].AsString, line["externalItemCode"].AsString, line["definitionId"].AsInt64, line["definitionVersion"].AsInt64,
            Money.Parse(line["netPricePerPackage"].AsString), Money.Parse(line["taxAmount"].AsString), Money.Parse(line["supplierChargeAmount"].AsString)))]));
    internal static string Decision(PurchaseOrderDecision value) => value switch { PurchaseOrderDecision.Approve => "approve", PurchaseOrderDecision.Reject => "reject", _ => throw PurchaseOrderRules.Invalid() };
    internal static PurchaseOrderDecision Decision(BsonValue value) => value.AsString switch { "approve" => PurchaseOrderDecision.Approve, "reject" => PurchaseOrderDecision.Reject, _ => throw Broken() };
    internal static string Permission(string operation) => operation switch { Prefix + "propose" => Permissions.ProcurementPrepare, Prefix + "approve" or Prefix + "reject" => Permissions.ProcurementApprove, _ => throw PurchaseOrderRules.Invalid() };
    internal static object ProposeBody(PurchaseOrderInput input, string binding) => new { input = PurchaseOrderRules.Copy(input), binding };
    internal static object DecideBody(long proposalId, long expectedVersion, PurchaseOrderDecision decision, string binding) => new { proposalId, expectedVersion, decision = Decision(decision), binding };
    internal static string Hash(BsonDocument value) => R.Hash(value);
    internal static BsonDocument Person(PersonEvidence value) => R.Person(value);
    internal static PersonEvidence Person(BsonValue value) => R.Person(value);
    internal static void Metadata(BsonDocument row, string operation, long auditId, ActorContext actor, AuthorizationCommandMetadata metadata, object body, DateTimeOffset now) => R.Metadata(row, operation, auditId, actor, metadata, body, now);
    internal static string Binding(long actor, long grantVersion, PurchaseOrderInput input, BsonDocument request, BsonDocument decision, BsonDocument sources, BsonDocument supplier, BsonArray capacity) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grantVersion }, { "input", Input(input) }, { "requestHash", request["snapshotHash"] }, { "requestDecisionHash", decision["snapshotHash"] },
        { "sources", sources }, { "supplier", supplier }, { "capacity", capacity } });
    internal static string DecisionBinding(long actor, long grantVersion, BsonDocument proposal, PurchaseOrderDecision decision, BsonDocument sources, BsonDocument? supplier, BsonArray capacity) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grantVersion }, { "proposalHash", proposal["snapshotHash"] }, { "decision", Decision(decision) }, { "sources", sources },
        { "supplier", supplier is null ? BsonNull.Value : supplier }, { "capacity", capacity } });
    internal static BsonDocument Totals(PurchaseOrderInput input) { var totals = PurchaseOrderRules.Totals(input); return new() {
        { "net", totals.Net.ToString() }, { "tax", totals.Tax.ToString() }, { "supplierCharges", totals.SupplierCharges.ToString() }, { "total", totals.Total.ToString() } }; }
    internal static BsonDocument SupplierView(BsonDocument source) => new() { { "id", source["supplierId"] }, { "versionId", source["versionId"] }, { "version", source["version"] },
        { "subjectReference", source["subjectReference"] }, { "displayName", source["displayName"] } };
    internal static BsonArray Lines(PurchaseOrderInput input, BsonDocument request) {
        var original = R.Input(request["input"]); return new(input.Lines.Select(line => {
            var index = Array.FindIndex(original.Lines.ToArray(), value => value.SkuId == line.RequestSkuId); if (index < 0) throw Broken(); var package = R.Package(request["catalog"][index]);
            var net = Money.Multiply(line.NetPricePerPackage, line.Packages); package.AddRange(new BsonDocument { { "packages", line.Packages },
                { "specification", original.Lines[index].Specification is { } specification ? specification : BsonNull.Value }, { "sourceSystem", line.SourceSystem },
                { "sourceAccountReference", line.SourceAccountReference }, { "externalItemCode", line.ExternalItemCode }, { "netPricePerPackage", line.NetPricePerPackage.ToString() },
                { "taxAmount", line.TaxAmount.ToString() }, { "supplierChargeAmount", line.SupplierChargeAmount.ToString() }, { "netAmount", net.ToString() },
                { "totalAmount", Money.Add(Money.Add(net, line.TaxAmount), line.SupplierChargeAmount).ToString() }, { "baseUnits", checked((long)line.Packages * package["baseUnitsPerPackage"].AsInt32) } }); return package;
        }));
    }
    internal static BsonArray CapacityView(BsonArray rows) => new(rows.Select(row => new BsonDocument(row.AsBsonDocument.Where(field => field.Name != "headId"))));
    internal static BsonDocument Result(BsonDocument row) => new() { { "proposalId", row["proposalId"] }, { "proposalVersion", 1L }, { "decisionId", row.Contains("decision") ? row["_id"] : BsonNull.Value },
        { "orderId", row.GetValue("orderId", BsonNull.Value) }, { "orderVersion", row.GetValue("orderVersion", BsonNull.Value) }, { "status", !row.Contains("decision") ? "proposed" : row["decision"] == "approve" ? "approved" : "rejected" } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", row["operation"] }, { "actorId", row["actorId"] },
        { "actorGrantVersion", row["actorGrantVersion"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] }, { "requestKey", row["requestKey"] },
        { "location", row["location"] }, { "requestId", row["requestId"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => Prefix + "command:" + row["auditId"].AsInt64;
    internal static void RequireBinding(string binding) { if (binding is not { Length: 64 } || binding.Any(c => !char.IsAsciiHexDigitLower(c))) throw PurchaseOrderRules.Invalid(); }
    internal static PersistenceConflictException Broken() => new("purchase_order_integrity_unavailable", "The complete purchase order history is unavailable");
    internal static PersistenceConflictException Changed() => new("purchase_order_changed", "Reload the current request capacity, supplier and commercial review");
}
