using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using Q = UnitToko.Platform.Inventory.ReturnQuarantineLedger;
using I = UnitToko.Platform.Inventory.ReturnInspectionRecords;
using E = UnitToko.Platform.Billing.ReturnEntitlementBson;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;
using P = UnitToko.Platform.Billing.RefundPayoutBson;
using S = UnitToko.Platform.Billing.RefundSettlementBson;

namespace UnitToko.Platform.Fulfillment;

public sealed partial class AfterSalesCaseService
{
    internal Func<IClientSessionHandle, Task>? TimelineBeforeFence { get; set; }
    public async Task<CaseTimelineOutcome> ReadTimelineOutcomeAsync(ActorContext actor, long orderId, long caseId, CancellationToken ct = default) {
        if (orderId <= 0 || caseId <= 0) throw AfterSalesCaseRecords.Broken();
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var root = await AuthorizedAsync(actor, authority, tx, caseId, Permissions.CaseRead, token);
            // A grouped invoice is not sufficient: one of this case's actually reported
            // receipt/SKU lines must belong to the requested order.
            bool Belongs(BsonValue line) => root["source"]["lines"].AsBsonArray.Any(source => source["receiptId"] == line["receiptId"] &&
                source["skuId"] == line["skuId"] && source["orderId"] == orderId);
            if (!root["input"]["lines"].AsBsonArray.Any(Belongs)) throw AfterSalesCaseRecords.Broken();
            var proof = new OrderTimelineHistory(database, tx);
            var decision = await new AfterSalesDecisionRecords(database).ReadAsync(tx, root, token);
            if (decision is not null) {
                proof.Hold(AfterSalesDecisionRecords.Collection, decision);
                await proof.EffectsAsync(decision, AfterSalesDecisionRecords.Operation, "actorId", token);
            }
            var rows = await proof.CaptureAsync(Q.Collection, new("caseId", caseId), token);
            await proof.RequireEffectIdsAsync(Q.Operation, "caseId", caseId, "returnId", rows.Select(row => row["_id"].AsInt64), token);
            if (rows.Count > 0 && decision?["state"] != "return_authorized") throw OrderTimelineHistory.Broken();
            if (decision?["state"] == "return_authorized") {
                var source = await ReturnSourceAsync(actor, authority, tx, root, Permissions.CaseRead, token);
                _ = await new Q(database, clock).ReadAsync(tx, source, token);
            }
            var returns = ImmutableArray.CreateBuilder<CaseTimelineReturn>();
            var financialFences = new List<FinancialEntitlementRequestSource>();
            foreach (var receipt in rows) {
                var returnId = receipt["_id"].AsInt64;
                var events = ImmutableArray.CreateBuilder<CaseTimelineEvent>();
                void Add(BsonDocument row, string kind, string time) {
                    var at = new DateTimeOffset(row[time].ToUniversalTime());
                    if (at > Now || at < new DateTimeOffset(receipt["receivedAt"].ToUniversalTime())) throw OrderTimelineHistory.Broken();
                    events.Add(new(new(row["_id"].AsInt64), kind, at, new(row["actorId"].AsInt64)));
                }
                proof.Hold(Q.Collection, receipt); await proof.EffectsAsync(receipt, Q.Operation, "actorId", token);
                Add(receipt, "received", "receivedAt");
                var inspection = await new I(database, clock).ReadAsync(tx, receipt, token);
                BsonDocument? resolution = null;
                if (inspection is not null) {
                    proof.Hold(I.Collection, inspection); await proof.EffectsAsync(inspection, I.Operation, "actorId", token);
                    Add(inspection, "inspected", "inspectedAt");
                    resolution = await new ReturnResolutionRecords(database, clock).ReadAsync(tx, inspection, token);
                    if (resolution is not null) {
                        proof.Hold(ReturnResolutionRecords.Collection, resolution);
                        await proof.EffectsAsync(resolution, ReturnResolutionRecords.Operation, "actorId", token);
                        Add(resolution, resolution["input"]["action"].AsString + "_requested", "requestedAt");
                    }
                } else if (await records.Rows(ReturnResolutionRecords.Collection).Find(tx, new BsonDocument("_id", returnId)).AnyAsync(token)) throw OrderTimelineHistory.Broken();
                var request = (await proof.CaptureAsync(E.Requests, new("_id", returnId), token)).SingleOrDefault();
                await proof.RequireEffectIdsAsync(E.Operation, "returnId", returnId, "returnId", request is null ? [] : [returnId], token);
                FinancialEntitlementRequestSource? financial = null;
                if (request is not null) {
                    if (resolution is null || resolution["input"]["action"].AsString is not ("credit" or "refund") || database.Client is not MongoClient client) throw OrderTimelineHistory.Broken();
                    var owner = new ReturnEntitlementService(database, new MongoUnitOfWork(client), ids, clock, this, invoices);
                    financial = await owner.ApprovalSourceAsync(actor, authority, tx, caseId, returnId, resolution["input"]["action"].AsString,
                        false, token, deferReadCaptureFences: true);
                    OrderTimelineHistory.Equal(request, financial.Request); financialFences.Add(financial);
                    proof.Hold(E.Requests, request); await proof.EffectsAsync(request, E.Operation, "actorId", token);
                    Add(request, "entitlement_requested", "requestedAt");
                }
                var approvals = await proof.CaptureAsync(A.Events, new("returnId", returnId), token);
                if (approvals.Count > 1 || approvals.Count > 0 && financial is null) throw OrderTimelineHistory.Broken();
                await proof.RequireEffectIdsAsync(A.Operation, "returnId", returnId, "financialApprovalId", approvals.Select(row => row["_id"].AsInt64), token);
                foreach (var candidate in approvals) {
                    var approved = await new PaymentAllocationRecords(database, clock).RawApprovalAsync(tx, candidate["_id"].AsInt64, token);
                    OrderTimelineHistory.Equal(approved["request"], financial!.Request);
                    OrderTimelineHistory.Equal(approved["invoice"], financial.Invoice.MoneySource);
                    if (approved["caseId"] != caseId || approved["invoiceId"] != root["source"]["invoiceId"]) throw OrderTimelineHistory.Broken();
                    proof.Hold(A.Events, approved); await proof.EffectsAsync(approved, A.Operation, "actorId", token);
                    Add(approved, approved["action"].AsString + "_approved", "recordedAt");
                    await TimelinePayoutsAsync(tx, approved, proof, events, token);
                }
                // A return on another constituent order is authenticated above but is not
                // projected onto this order's timeline merely because the case is grouped.
                if (receipt["input"]["lines"].AsBsonArray.Any(Belongs)) returns.Add(new(new(returnId),
                    [.. events.OrderBy(item => item.RecordedAt).ThenBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.EventId.Value)]));
            }
            // A removed parent must not hide committed downstream history as an earlier
            // stage. Authenticate complete membership, including returns from another
            // constituent order which are deliberately not projected above.
            foreach (var (collection, path, operation, resultId) in new[] {
                (I.Collection, "source.caseId", I.Operation, "returnId"),
                (ReturnResolutionRecords.Collection, "source.caseId", ReturnResolutionRecords.Operation, "returnId"),
                (E.Requests, "caseId", E.Operation, "returnId"),
                (A.Events, "caseId", A.Operation, "financialApprovalId"),
                (S.Events, "caseId", S.Operation, "settlementId") }) {
                var members = await proof.RequireMembersAsync(collection, new(path, caseId), token);
                await proof.RequireEffectIdsAsync(operation, "caseId", caseId, resultId, members, token);
            }
            _ = await proof.RequireMembersAsync(P.Payouts, new("caseId", caseId), token);
            var result = new CaseTimelineOutcome(new(orderId), new(caseId), new(root["source"]["invoiceId"].AsInt64), Now,
                decision?["state"].AsString ?? "requested", decision is null ? null : new LongId(decision["_id"].AsInt64),
                decision is null ? null : new DateTimeOffset(decision["decidedAt"].ToUniversalTime()), decision is null ? null : new LongId(decision["actorId"].AsInt64),
                decision?["input"]["publicExplanation"].AsString, returns.ToImmutable());
            if (TimelineBeforeFence is { } beforeFence) await beforeFence(tx);
            foreach (var source in financialFences) await source.FenceReadAsync(token);
            await proof.FenceAsync(token);
            root = await AuthorizedAsync(actor, authority, tx, caseId, Permissions.CaseRead, token);
            await FenceAsync(actor, authority, tx, root, Permissions.CaseRead, token);
            foreach (var source in financialFences) source.RequireCurrent();
            if (Now < result.ObservedAt) throw OrderTimelineHistory.Broken();
            return result;
        }, ct);
    }

    private async Task TimelinePayoutsAsync(IClientSessionHandle tx, BsonDocument approval, OrderTimelineHistory proof,
        ImmutableArray<CaseTimelineEvent>.Builder events, CancellationToken ct) {
        if (database.Client is not MongoClient client) throw OrderTimelineHistory.Broken();
        var store = new RefundPayoutRecords(database, clock, new MongoUnitOfWork(client));
        var payouts = await proof.CaptureAsync(P.Payouts, new("financialApprovalId", approval["_id"]), ct);
        if (payouts.Count > 1 || payouts.Count > 0 && approval["action"] != "refund") throw OrderTimelineHistory.Broken();
        await proof.RequireEffectIdsAsync(P.Operation, "approvalId", approval["_id"], "payoutId", payouts.Select(row => row["_id"].AsInt64), ct);
        foreach (var candidate in payouts) {
            var payout = await store.PayoutAsync(tx, candidate["_id"].AsInt64, false, ct);
            OrderTimelineHistory.Equal(payout["source"]["approval"], P.ApprovalSnapshot(approval, new() {
                { "accountId", approval["policy"]["refundLiabilityAccountId"] }, { "accountCode", approval["policy"]["refundLiabilityAccountCode"] } }));
            var draft = await store.DraftAsync(tx, payout["draftId"].AsInt64, ct);
            _ = await BankReceiptService.ReadPayoutBankHistoryAsync(database, tx, payout["source"]["payingBank"].AsBsonDocument,
                new(draft["createdAt"].ToUniversalTime()), ct);
            await proof.CapturePayoutBankAsync(payout["source"]["payingBank"].AsBsonDocument, ct);
            foreach (var field in new[] { "caseId", "returnId", "invoiceId" }) OrderTimelineHistory.Equal(payout[field], approval[field]);
            proof.Hold(P.Payouts, payout); proof.Hold(P.Drafts, draft);
            await proof.EffectsAsync(payout, P.Operation, "actorId", ct); await proof.EffectsAsync(draft, P.DraftOperation, "userId", ct);
            events.Add(new(new(payout["_id"].AsInt64), "payout_recorded", new(payout["recordedAt"].ToUniversalTime()), new(payout["actorId"].AsInt64)));
            var settlements = await proof.CaptureAsync(S.Events, new("payoutId", payout["_id"]), ct);
            if (settlements.Count > 1) throw OrderTimelineHistory.Broken();
            await proof.RequireEffectIdsAsync(S.Operation, "payoutId", payout["_id"], "settlementId", settlements.Select(row => row["_id"].AsInt64), ct);
            foreach (var item in settlements) {
                var settled = await RefundSettlementRecords.ReadAsync(database, tx, item["_id"].AsInt64, ct, clock);
                OrderTimelineHistory.Equal(settled["payoutHash"], payout["snapshotHash"]); OrderTimelineHistory.Equal(settled["approvalHash"], approval["snapshotHash"]);
                proof.Hold(S.Events, settled); await proof.EffectsAsync(settled, S.Operation, "actorId", ct);
                await proof.HoldAsync(BankTransactionClaims.Collection, new("_id", settled["canonicalIdentity"]), ct);
                await proof.CaptureSettlementBankAsync(settled, ct);
                events.Add(new(new(settled["_id"].AsInt64), "refund_settled", new(settled["recordedAt"].ToUniversalTime()), new(settled["actorId"].AsInt64)));
            }
        }
    }
}
