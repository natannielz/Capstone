using System.Collections.Immutable;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using V = UnitToko.Platform.Billing.PaymentVerificationBson;
using A = UnitToko.Platform.Billing.PaymentAllocationBson;
using C = UnitToko.Platform.Billing.PaymentAllocationCorrectionBson;

namespace UnitToko.Platform.Billing;

public sealed partial class PaymentDraftService
{
    internal Func<IClientSessionHandle, Task>? TimelineBeforeFence { get; set; }
    internal Func<Task>? TimelineAfterHistoryFence { get; set; }
    public async Task<PaymentTimelineOutcome> ReadTimelineOutcomeAsync(ActorContext actor, long orderId,
        long declarationId, CancellationToken ct = default) {
        if (orderId <= 0 || declarationId <= 0) throw PaymentDraftBson.Invalid();
        await CorrectionStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            // Same creator, live PaymentEvidence access, and every constituent invoice's
            // current InvoiceRead access are required before any outcome discovery.
            var head = await HeadAsync(actor, authority, tx, declarationId, token);
            var version = await records.VersionAsync(tx, head["versionId"].AsInt64, token);
            var proof = new OrderTimelineHistory(database, tx);
            var matching = new HashSet<long>();
            foreach (var invoice in version["invoices"].AsBsonArray) {
                var row = await records.Rows("invoices").Find(tx, new BsonDocument("_id", invoice["invoiceId"])).SingleOrDefaultAsync(token) ?? throw OrderTimelineHistory.Broken();
                OrderTimelineHistory.Equal(row["snapshotHash"], invoice["snapshotHash"]);
                if (row["orderIds"].AsBsonArray.Contains(new BsonInt64(orderId))) matching.Add(row["_id"].AsInt64);
                proof.Hold("invoices", row);
            }
            if (matching.Count == 0) throw PaymentDraftBson.Invalid();
            BsonDocument? submission = null, review = null, verification = null;
            var events = ImmutableArray.CreateBuilder<PaymentTimelineInvoiceEvent>();
            if (head["state"] == "submitted") {
                submission = await new PaymentSubmissionRecords(database).ReadAsync(tx, head["submissionId"].AsInt64, token);
                var source = PaymentDraftBson.SubmittedSource(head, version, submission);
                review = await new PaymentReviewActionRecords(database).LatestAsync(tx, declarationId, source, token);
                var reviewActions = await proof.CaptureAsync(PaymentReviewActionRecords.Versions, new("declarationId", declarationId), token);
                await proof.RequireEffectIdsAsync(PaymentReviewActionRecords.Operation, "declarationId", declarationId, "actionId",
                    reviewActions.Select(action => action["_id"].AsInt64), token);
                foreach (var action in reviewActions) {
                    if (action["recordedAt"].ToUniversalTime() > Now.UtcDateTime) throw OrderTimelineHistory.Broken();
                    proof.Hold(PaymentReviewActionRecords.Versions, action);
                    await proof.EffectsAsync(action, PaymentReviewActionRecords.Operation, "actorId", token);
                }
                verification = (await proof.CaptureAsync(V.Decisions, new("declarationId", declarationId), token)).SingleOrDefault();
                await proof.RequireEffectIdsAsync(V.Operation, "declarationId", declarationId, "verificationId",
                    verification is null ? [] : [verification["_id"].AsInt64], token);
                if (verification is not null) {
                    var verified = await TimelineVerificationAsync(actor, authority, tx, verification["_id"].AsInt64, proof, new(), token);
                    OrderTimelineHistory.Equal(verified["source"], source);
                    verification = verified;
                    var store = new PaymentAllocationRecords(database, clock);
                    foreach (var (collection, operation, correction) in new[] { (A.Events, A.Operation, false), (C.Events, C.Operation, true) }) {
                        var rows = await proof.CaptureAsync(collection, new("verificationId", verified["_id"]), token);
                        await proof.RequireEffectIdsAsync(operation, "verificationId", verified["_id"], correction ? "correctionId" : "allocationId",
                            rows.Select(row => row["_id"].AsInt64), token);
                        foreach (var candidate in rows) {
                            var row = await store.RawEventAsync(tx, correction, candidate["_id"].AsInt64, token);
                            foreach (var field in new[] { "receiptId", "receiptHash" }) OrderTimelineHistory.Equal(row[field], verified[field]);
                            OrderTimelineHistory.Equal(row["verificationHash"], verified["snapshotHash"]);
                            OrderTimelineHistory.Equal(row["declarationId"], verified["declarationId"]);
                            OrderTimelineHistory.Equal(row["declarationRevision"], verified["source"]["revision"]);
                            OrderTimelineHistory.Equal(row["recordedBy"], verified["source"]["recordedBy"]);
                            if (row["recordedAt"].CompareTo(verified["recordedAt"]) < 0 || row["recordedAt"].ToUniversalTime() > Now.UtcDateTime) throw OrderTimelineHistory.Broken();
                            foreach (var line in row["lines"].AsBsonArray) {
                                var invoiceId = line["invoiceId"].AsInt64;
                                var actual = await invoices.AllocationSourceAsync(actor, authority, tx, invoiceId, token);
                                OrderTimelineHistory.Equal(line["source"], actual);
                                if (matching.Contains(invoiceId)) events.Add(new(new(invoiceId), new(row["_id"].AsInt64), correction ? "reversal" : "allocation",
                                    new(row["recordedAt"].ToUniversalTime()), correction ? new LongId(row["originalAllocationId"].AsInt64) : null, new(row["actorId"].AsInt64)));
                            }
                            proof.Hold(collection, row); await proof.EffectsAsync(row, operation, "actorId", token);
                        }
                    }
                }
            } else {
                await new PaymentVerificationRecords(database).RequireUnverifiedAsync(tx, declarationId, token);
                if ((await proof.CaptureAsync(PaymentReviewActionRecords.Versions, new("declarationId", declarationId), token)).Count != 0 ||
                    (await proof.CaptureAsync(V.Decisions, new("declarationId", declarationId), token)).Count != 0) throw OrderTimelineHistory.Broken();
                await proof.RequireEffectIdsAsync(V.Operation, "declarationId", declarationId, "verificationId", [], token);
                await proof.RequireEffectIdsAsync(PaymentReviewActionRecords.Operation, "declarationId", declarationId, "actionId", [], token);
            }
            foreach (var collection in new[] { A.Events, C.Events })
                if ((await proof.CaptureAsync(collection, new BsonDocument { { "declarationId", declarationId },
                    { "verificationId", verification is null ? new BsonDocument("$exists", true) : new BsonDocument("$ne", verification["_id"]) } }, token)).Count != 0)
                    throw OrderTimelineHistory.Broken();
            var result = new PaymentTimelineOutcome(new(orderId), new(declarationId), head["revision"].AsInt64.ToString(CultureInfo.InvariantCulture), Now,
                head["state"].AsString, submission is null ? null : new LongId(submission["_id"].AsInt64), submission is null ? null : new DateTimeOffset(submission["submittedAt"].ToUniversalTime()),
                review is null ? null : new LongId(review["_id"].AsInt64), (review?["revision"].AsInt64 ?? 0).ToString(CultureInfo.InvariantCulture),
                review?["state"].AsString ?? (submission is null ? "not_submitted" : "pending"), review is null ? null : new DateTimeOffset(review["recordedAt"].ToUniversalTime()),
                review?.GetValue("publicExplanation", BsonNull.Value) is { IsString: true } explanation ? explanation.AsString : null,
                review?["state"] == "assigned" ? new LongId(review["assigneeId"].AsInt64) : null,
                review is null ? null : new LongId(review["actorId"].AsInt64),
                verification is null ? null : new LongId(verification["_id"].AsInt64), verification is null ? null : new DateTimeOffset(verification["recordedAt"].ToUniversalTime()),
                verification is null ? null : new LongId(verification["actorId"].AsInt64),
                [.. events.OrderBy(item => item.RecordedAt).ThenBy(item => item.EventId.Value).ThenBy(item => item.InvoiceId.Value)]);
            if (TimelineBeforeFence is { } beforeFence) await beforeFence(tx);
            OrderTimelineHistory.Equal(version["invoices"], await SourcesAsync(actor, authority, tx, PaymentDraftBson.Input(version["input"].AsBsonDocument), token));
            await proof.FenceAsync(token);
            if (TimelineAfterHistoryFence is { } afterHistoryFence) await afterHistoryFence();
            await records.TouchAsync(tx, head, token);
            var invoiceScopes = PaymentDraftBson.Scopes(new BsonArray(version["invoices"].AsBsonArray.SelectMany(invoice => invoice["scopes"].AsBsonArray)));
            AuthorizationQuery InvoiceQuery() {
                var query = ScopePolicy.Compile(actor, authority, Permissions.InvoiceRead, clock.UtcNow);
                AuthorizationStore.Require(query);
                if (invoiceScopes.Length == 0 || invoiceScopes.Any(scope => !query.Matches(scope))) throw PaymentDraftBson.Invalid();
                return query;
            }
            var invoiceClauses = InvoiceQuery().Clauses.Where(clause => invoiceScopes.Any(clause.Matches)).ToArray();
            await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
                invoiceClauses.SelectMany(clause => clause.RequiredNodes).Concat(invoiceScopes.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory))).Distinct(), [],
                authority.Grants.Where(grant => invoiceClauses.Any(clause => clause.GrantId == grant.Id)),
                authority.Memberships.Where(member => invoiceClauses.Any(clause => clause.MembershipId == member.Id)), tx, token);
            await FenceAsync(actor, authority, tx, head["scopes"].AsBsonArray, token);
            _ = InvoiceQuery();
            if (Now < result.ObservedAt) throw OrderTimelineHistory.Broken();
            return result;
        }, ct);
    }
}
