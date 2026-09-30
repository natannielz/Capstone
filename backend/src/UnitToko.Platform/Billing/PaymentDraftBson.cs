using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Fulfillment;

namespace UnitToko.Platform.Billing;
internal static class PaymentDraftBson
{
    internal const string Heads = "paymentDeclarations", Versions = "paymentDeclarationVersions", Operation = "billing.payment.draft.save";
    internal const string ReplacementOperation = "billing.payment.replacement.create";
    internal static AuthorizationDeniedException Invalid() => new("payment_declaration_unavailable");
    internal static object Body(long? id, long expected, PaymentDraftInput input) => new { id, expected, input };
    internal static object ReplacementBody(long parentId, long expected, PaymentReplacementRequest input) => new { parentId, expected, input };
    internal static string CommandOperation(BsonDocument row) => row["schemaVersion"] == 2 ? ReplacementOperation : Operation;
    internal static object CommandBody(BsonDocument row) => row["schemaVersion"] == 2
        ? ReplacementBody(row["replacement"]["source"]["declarationId"].AsInt64, row["replacement"]["source"]["revision"].AsInt64, PaymentReplacementRequest.From(row["replacement"].AsBsonDocument))
        : Body(row["revision"] == 1 ? null : row["declarationId"].AsInt64, row["revision"].AsInt64 - 1, Input(row["input"].AsBsonDocument));
    internal static BsonDocument Input(PaymentDraftInput value) => new() {
        { "invoiceIds", new BsonArray(value.InvoiceIds.Select(id => new BsonInt64(id.Value))) }, { "declaredAmount", InvoiceBson.Money(value.DeclaredAmount) },
        { "transferDate", InvoiceBson.Date(value.TransferDate) }, { "payerName", value.PayerName },
        { "bankReference", value.BankReference is null ? BsonNull.Value : value.BankReference }, { "note", value.Note is null ? BsonNull.Value : value.Note } };
    internal static BsonDocument SubmittedSource(BsonDocument head, BsonDocument version, BsonDocument submission) =>
        new() { { "declarationId", head["_id"] }, { "revision", head["revision"] }, { "versionId", version["_id"] },
            { "versionHash", version["snapshotHash"] }, { "submissionId", submission["_id"] }, { "submissionHash", submission["snapshotHash"] },
            { "recordedBy", submission["recordedBy"] }, { "uploaderId", submission["uploaderId"] }, { "reservationId", submission["reservationId"] },
            { "evidence", submission["evidence"].DeepClone() }, { "input", version["input"].DeepClone() }, { "invoices", version["invoices"].DeepClone() },
            { "party", head["party"].DeepClone() }, { "scopes", head["scopes"].DeepClone() } };
    internal static PaymentDraftInput Input(BsonDocument value) => new([.. value["invoiceIds"].AsBsonArray.Select(id => new LongId(id.AsInt64))],
        InvoiceBson.Money(value["declaredAmount"]), InvoiceBson.Date(value["transferDate"]), value["payerName"].AsString,
        value["bankReference"].IsBsonNull ? null : value["bankReference"].AsString, value["note"].IsBsonNull ? null : value["note"].AsString);
    internal static BsonArray Scopes(BsonArray sources, long recorder) => new(sources.SelectMany(source => source["scopes"].AsBsonArray)
        .Select(scope => ShipmentPreparationBson.Scope(ShipmentPreparationBson.Scope(scope.AsBsonDocument) with { CreatedBy = recorder })).Distinct());
    internal static ResourceScope[] Scopes(BsonArray values) => [.. values.Select(value => ShipmentPreparationBson.Scope(value.AsBsonDocument))];
    internal static BsonDocument Party(BsonArray sources) {
        if (sources.Count is < 1 or > PaymentDraftPolicy.MaximumInvoices) throw Invalid();
        var scopes = sources.SelectMany(source => source["scopes"].AsBsonArray).Select(scope => ShipmentPreparationBson.Scope(scope.AsBsonDocument)).ToArray();
        if (scopes.Length == 0 || scopes.Any(scope => scope.BuyingOrganizationId is not > 0 || scope.FulfillmentOrganizationId is not > 0 || scope.ShopId is not > 0 || scope.ChannelId is not > 0)) throw Invalid();
        var first = scopes[0];
        if (scopes.Any(scope => scope.BuyingOrganizationId != first.BuyingOrganizationId || scope.FulfillmentOrganizationId != first.FulfillmentOrganizationId || scope.ShopId != first.ShopId || scope.ChannelId != first.ChannelId))
            throw PaymentDraftPolicy.Invalid("payment_parties_differ");
        return new() { { "buyingOrganizationId", first.BuyingOrganizationId!.Value }, { "fulfillmentOrganizationId", first.FulfillmentOrganizationId!.Value },
            { "shopId", first.ShopId!.Value }, { "channelId", first.ChannelId!.Value } };
    }
    internal static string Hash(BsonDocument value) => CatalogRepository.Digest(new BsonDocument(value.Where(field => field.Name != "snapshotHash")));
    internal static BsonDocument Result(BsonDocument value) {
        var result = new BsonDocument { { "declarationId", value["declarationId"] }, { "versionId", value["_id"] }, { "revision", value["revision"] }, { "recordedAt", value["recordedAt"] } };
        if (value["schemaVersion"] == 2) { result.Add("parentDeclarationId", value["replacement"]["source"]["declarationId"]); result.Add("parentDeclarationRevision", value["replacement"]["source"]["revision"]); }
        return result;
    }
    internal static BsonDocument Audit(BsonDocument value) => new() { { "_id", value["auditId"] }, { "schemaVersion", 1 }, { "kind", CommandOperation(value) }, { "operation", CommandOperation(value) },
        { "actorId", value["recordedBy"] }, { "actorGrantVersion", value["actorGrantVersion"] }, { "requestKey", value["requestKey"] }, { "bodyHash", value["bodyHash"] },
        { "snapshotHash", value["snapshotHash"] }, { "reason", value["reason"] }, { "correlationId", value["correlationId"] }, { "createdAt", value["recordedAt"] }, { "result", Result(value) } };
}
