using MongoDB.Bson;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;

namespace UnitToko.Platform.Ordering;

internal static class OrderSubstitutionBson
{
    internal const string Proposals = "orderSubstitutionProposals", Decisions = "orderSubstitutionDecisions";
    internal const string ProposeOperation = "ordering.substitution.propose", AcceptOperation = "ordering.substitution.accept", DeclineOperation = "ordering.substitution.decline";
    internal static string Operation(OrderSubstitutionDecision decision) => decision switch {
        OrderSubstitutionDecision.Accept => AcceptOperation, OrderSubstitutionDecision.Decline => DeclineOperation,
        _ => throw Unavailable() };
    internal static BsonDocument Selection(OrderSubstitutionSelection value) => new() {
        { "originalSkuId", value.OriginalSkuId.Value }, { "originalLineRevision", value.OriginalLineRevision }, { "originalPackages", value.OriginalPackages },
        { "replacement", new BsonDocument { { "skuId", value.Replacement.SkuId.Value }, { "definitionId", value.Replacement.DefinitionId.Value },
            { "definitionVersion", value.Replacement.DefinitionVersion }, { "priceId", value.Replacement.PriceId.Value },
            { "priceVersion", value.Replacement.PriceVersion }, { "packages", value.Replacement.Packages } } } };
    internal static OrderSubstitutionSelection Selection(BsonDocument value) {
        var replacement = value["replacement"];
        return new(new(value["originalSkuId"].AsInt64), value["originalPackages"].AsInt32,
            new(new(replacement["skuId"].AsInt64), new(replacement["definitionId"].AsInt64), replacement["definitionVersion"].AsInt64,
                new(replacement["priceId"].AsInt64), replacement["priceVersion"].AsInt64, replacement["packages"].AsInt32), value["originalLineRevision"].AsInt64);
    }
    internal static BsonDocument Input(OrderSubstitutionInput value) => new() { { "orderId", value.OrderId.Value }, { "suborderId", value.SuborderId.Value },
        { "expectedOrderRevision", value.ExpectedOrderRevision }, { "expectedDispatchSequence", value.ExpectedDispatchSequence }, { "selection", Selection(value.Selection) } };
    internal static OrderSubstitutionInput Input(BsonDocument value) => new(new(value["orderId"].AsInt64), new(value["suborderId"].AsInt64),
        value["expectedOrderRevision"].AsInt64, value["expectedDispatchSequence"].AsInt64, Selection(value["selection"].AsBsonDocument));
    internal static object ProposalBody(OrderSubstitutionInput input, string review) => new { input, review };
    internal static object DecisionBody(long proposalId, long expectedRevision, OrderSubstitutionDecision decision, string review) => new { proposalId, expectedRevision, decision, review };
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name != "recordHash")));
    internal static BsonDocument ProposalResult(BsonDocument row) => new() { { "proposalId", row["_id"] }, { "orderId", row["orderId"] },
        { "suborderId", row["suborderId"] }, { "status", "proposed" }, { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument DecisionResult(BsonDocument row) => new() { { "proposalId", row["proposalId"] }, { "decisionId", row["_id"] },
        { "orderId", row["orderId"] }, { "suborderId", row["suborderId"] }, { "revision", row["revision"] },
        { "status", row["decision"] == "Accept" ? "accepted" : "declined" }, { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument Audit(BsonDocument row, bool proposal) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 },
        { "kind", proposal ? "ordering.substitution.proposed" : "ordering.substitution.decided" }, { "actorId", row["actorId"] },
        { "actorGrantVersion", row["actorGrantVersion"] }, { "operation", proposal ? ProposeOperation : Operation(Enum.Parse<OrderSubstitutionDecision>(row["decision"].AsString)) },
        { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["recordHash"] },
        { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] },
        { "result", proposal ? ProposalResult(row) : DecisionResult(row) } };
    internal static AuthorizationDeniedException Unavailable() => new("order_substitution_unavailable");
}
