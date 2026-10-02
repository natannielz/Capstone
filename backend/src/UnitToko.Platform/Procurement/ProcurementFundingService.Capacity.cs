using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Procurement;
using B = UnitToko.Platform.Procurement.ProcurementFundingBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementFundingService
{
    private sealed class FundingCapacityState(BsonDocument source, BsonDocument? head, BsonDocument? previous, Money authorized) {
        internal BsonDocument Source { get; } = source;
        internal BsonDocument? Head { get; set; } = head;
        internal BsonDocument? Previous { get; set; } = previous;
        internal Money Authorized { get; set; } = authorized;
        internal BsonDocument View() {
            var limit = Money.Parse(Source["proposal"]["input"]["paymentTerms"]["requiredAdvance"].AsString);
            if (Authorized.Value > limit.Value) throw B.Broken();
            return new() { { "orderId", Source["order"]["_id"] }, { "orderVersion", Source["order"]["version"] }, { "requiredAdvance", limit.ToString() },
                { "authorized", Authorized.ToString() }, { "remaining", new Money(limit.Value - Authorized.Value).ToString() }, { "version", Head?["version"] ?? new BsonInt64(0) } };
        }
    }
    private async Task TouchAsync(Context context, string name, BsonDocument row, CancellationToken ct) {
        if (row["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(name).UpdateOneAsync(context.Tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        row["fence"] = row["fence"].AsInt64 + 1;
    }
    private static void CapacityLink(BsonDocument row, BsonDocument source) {
        ProcurementFundingRecords.Equal(row["orderId"], source["order"]["_id"]); ProcurementFundingRecords.Equal(row["orderVersion"], source["order"]["version"]);
        ProcurementFundingRecords.Equal(row["orderHash"], source["order"]["snapshotHash"]); ProcurementFundingRecords.Equal(row["orderApprovalId"], source["order"]["approvalId"]);
    }
    private async Task<FundingCapacityState> CapacityAsync(IClientSessionHandle tx, BsonDocument source, CancellationToken ct) {
        var filter = new BsonDocument("orderId", source["order"]["_id"]); var rawHead = await Rows(B.Heads).Find(tx, filter).SingleOrDefaultAsync(ct);
        var head = rawHead is null ? null : await records.RowAsync(tx, B.Heads, rawHead["_id"].AsInt64, ct);
        var rawRows = await Rows(B.Allocations).Find(tx, filter).Sort(new BsonDocument("sequence", 1)).ToListAsync(ct);
        var claims = await records.ClaimsAsync(tx, source["order"]["_id"].AsInt64, ct);
        if (claims.Count != rawRows.Count || (head is null) != (rawRows.Count == 0)) throw B.Broken();
        BsonDocument? previous = null; long sequence = 0; var authorized = new Money(0); var limit = Money.Parse(source["proposal"]["input"]["paymentTerms"]["requiredAdvance"].AsString);
        foreach (var raw in rawRows) {
            var row = await records.RowAsync(tx, B.Allocations, raw["_id"].AsInt64, ct); CapacityLink(row, source);
            ProcurementFundingRecords.Equal(row["snapshotHash"], B.Hash(row)); ProcurementFundingRecords.Equal(row["sequence"], ++sequence);
            ProcurementFundingRecords.Equal(row["previousAllocationId"], previous?["_id"] ?? BsonNull.Value); ProcurementFundingRecords.Equal(row["previousHash"], previous?["snapshotHash"] ?? BsonNull.Value);
            var claim = claims.Where(value => value["authorizationId"] == row["_id"] && value["decisionId"] == row["decisionId"]).ToArray();
            if (claim.Length != 1 || claim[0]["decisionHash"] != row["decisionHash"] || claim[0]["amount"] != row["amount"] || claim[0]["recordedAt"] != row["recordedAt"] ||
                row["recordedAt"].ToUniversalTime() < source["order"]["recordedAt"].ToUniversalTime() || previous is not null && row["recordedAt"].ToUniversalTime() < previous["recordedAt"].ToUniversalTime()) throw B.Broken();
            var reviewed = claim[0]["reviewCapacity"];
            ProcurementFundingRecords.Equal(reviewed["version"], sequence - 1);
            ProcurementFundingRecords.Equal(reviewed["authorized"], authorized.ToString());
            ProcurementFundingRecords.Equal(reviewed["remaining"], new Money(limit.Value - authorized.Value).ToString());
            authorized = Money.Add(authorized, Money.Parse(row["amount"].AsString)); if (authorized.Value > limit.Value) throw B.Broken(); previous = row;
        }
        if (head is not null) {
            CapacityLink(head, source); ProcurementFundingRecords.Equal(head["requiredAdvance"], limit.ToString()); ProcurementFundingRecords.Equal(head["authorized"], authorized.ToString());
            ProcurementFundingRecords.Equal(head["version"], sequence); ProcurementFundingRecords.Equal(head["createdAt"], rawRows[0]["recordedAt"]); ProcurementFundingRecords.Equal(head["updatedAt"], previous!["recordedAt"]);
        }
        return new(source, head, previous, authorized);
    }
    private async Task ReserveAsync(Context context, BsonDocument decision, CancellationToken ct) {
        var state = context.Capacity ?? throw B.Broken(); var request = context.Proposal ?? throw B.Broken(); var input = B.Input(request["input"]);
        ProcurementFundingRecords.CapacityInput(input, context.Policy!, state.View()); Current(context); var at = decision["recordedAt"];
        if (state.Head is { } current && current["updatedAt"].ToUniversalTime() > at.ToUniversalTime()) throw B.Changed();
        var allocation = new BsonDocument { { "_id", decision["authorizationId"] }, { "schemaVersion", 1 }, { "orderId", input.OrderId }, { "orderVersion", input.OrderVersion },
            { "orderHash", state.Source["order"]["snapshotHash"] }, { "orderApprovalId", input.OrderApprovalId }, { "requestId", request["_id"] }, { "requestHash", request["snapshotHash"] },
            { "decisionId", decision["_id"] }, { "decisionHash", decision["snapshotHash"] }, { "amount", input.Amount.ToString() }, { "sequence", state.Head is null ? 1L : checked(state.Head["version"].AsInt64 + 1) },
            { "previousAllocationId", state.Previous?["_id"] ?? BsonNull.Value }, { "previousHash", state.Previous?["snapshotHash"] ?? BsonNull.Value }, { "recordedAt", at } }; allocation.Add("snapshotHash", B.Hash(allocation));
        var total = Money.Add(state.Authorized, input.Amount); BsonDocument head;
        if (state.Head is null) {
            head = new BsonDocument { { "_id", (await ids.NextAsync(B.Heads, ct)).Value }, { "schemaVersion", 1 }, { "orderId", input.OrderId }, { "orderVersion", input.OrderVersion },
                { "orderHash", state.Source["order"]["snapshotHash"] }, { "orderApprovalId", input.OrderApprovalId }, { "requiredAdvance", state.View()["requiredAdvance"] },
                { "authorized", total.ToString() }, { "version", 1L }, { "fence", 0L }, { "createdAt", at }, { "updatedAt", at } };
            await Rows(B.Heads).InsertOneAsync(context.Tx, head, cancellationToken: ct);
        } else {
            head = state.Head.DeepClone().AsBsonDocument; head["authorized"] = total.ToString(); head["version"] = allocation["sequence"]; head["updatedAt"] = at;
            if (head["fence"].AsInt64 >= long.MaxValue - 1) throw B.Broken(); head["fence"] = head["fence"].AsInt64 + 1;
            if ((await Rows(B.Heads).ReplaceOneAsync(context.Tx, state.Head, head, cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        }
        await Rows(B.Allocations).InsertOneAsync(context.Tx, allocation, cancellationToken: ct); state.Head = head; state.Previous = allocation; state.Authorized = total;
    }
}
