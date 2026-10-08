using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using R = UnitToko.Platform.Authorization.TemporaryDelegationRecords;
using W = UnitToko.Platform.Billing.PaymentReviewActionRecords;
using H = UnitToko.Platform.Billing.PaymentReviewHandoverRecords;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Platform.Billing;

public sealed record PaymentHandoverChoice(long DelegationId, long DelegationRevision, long DelegationEventId,
    string State, DateTimeOffset ExpiresAt);
public sealed record PaymentHandoverPage(long DeclarationId, long DeclarationRevision, long SubmissionId, string Binding,
    long ReviewRevision, long? AssigneeId, bool Open, bool CanClaim, ImmutableArray<PaymentHandoverChoice> Items,
    long? NextId, DateTimeOffset ObservedAt);

public sealed partial class PaymentVerificationService
{
    public async Task<PaymentHandoverPage> ReadHandoversAsync(ActorContext actor, long id, long? beforeId, CancellationToken ct = default) {
        if (id <= 0 || beforeId is <= 0) throw PaymentReviewHandoverPolicy.Invalid();
        await StorageAsync(ct); await H.StorageAsync(database, ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await SourceAsync(new(actor, authority, tx, false), id, token); var source = context.Source!;
            var latest = await new W(database).LatestAsync(tx, id, source, token); var revision = latest?["revision"].AsInt64 ?? 0;
            var verified = await records.Rows(B.Decisions).Find(tx, new BsonDocument("declarationId", id)).SingleOrDefaultAsync(token);
            if (verified is not null) _ = await SavedAsync(context, verified, token); else await records.RequireUnverifiedAsync(tx, id, token);
            var assignee = latest is null || latest["assigneeId"].IsBsonNull ? (long?)null : latest["assigneeId"].AsInt64;
            var open = verified is null && latest?["state"] == "assigned";
            var resources = PaymentDraftBson.Scopes(source["scopes"].AsBsonArray);
            var canClaim = open && assignee != actor.UserId && resources.All(resource => ScopePolicy.Authorize(actor, authority,
                Permissions.PaymentReviewManage, resource, clock.UtcNow, new(source["revision"].AsInt64, source["revision"].AsInt64,
                    RecordedBy: source["recordedBy"].AsInt64)).Allowed);
            if (canClaim) {
                try { await PeopleAsync(context, token); }
                catch (AuthorizationDeniedException failure) when (failure.Code is "independent_person_required" or "person_authority_unavailable") { canClaim = false; }
            }
            var heads = new List<BsonDocument>(); var more = false;
            if (open && assignee is { } assigned) {
                var filters = new List<BsonDocument> { new("$or", new BsonArray { new BsonDocument("sourceUserId", assigned), new BsonDocument("request.beneficiaryUserId", assigned) }),
                    new("approvedBy", new BsonDocument("$gt", 0L)), new("request.permissions", Permissions.PaymentReviewManage) };
                filters.AddRange(resources.Select(HandoverScopeFilter));
                if (beforeId is { } before) filters.Add(new("_id", new BsonDocument("$lt", before)));
                var rows = await records.Rows(R.Heads).Find(tx, new BsonDocument("$and", new BsonArray(filters)))
                    .Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); more = rows.Count > 25;
                foreach (var row in rows.Take(25)) {
                    var head = await R.HeadAsync(database, tx, row["_id"].AsInt64, token);
                    if (head != row || !H.Matches(head, source, assigned)) throw B.Broken();
                    await R.ExactAsync(database, tx, R.Heads, head, token); heads.Add(head);
                }
            }
            await AuditReadAsync(context, "workflow.handover", id, token); await FenceAsync(context, token);
            var at = clock.UtcNow;
            canClaim = canClaim && resources.All(resource => ScopePolicy.Authorize(actor, authority, Permissions.PaymentReviewManage,
                resource, at, new(source["revision"].AsInt64, source["revision"].AsInt64, RecordedBy: source["recordedBy"].AsInt64)).Allowed);
            return new PaymentHandoverPage(id, source["revision"].AsInt64, source["submissionId"].AsInt64, W.Binding(source, revision), revision, assignee,
                open, canClaim, heads.Select(head => new PaymentHandoverChoice(head["_id"].AsInt64, head["revision"].AsInt64, head["eventId"].AsInt64,
                    H.State(head, at), R.Time(head["request"]["expiresAt"]))).ToImmutableArray(), more ? heads[^1]["_id"].AsInt64 : null, at);
        }, ct);
    }
    // Exact ancestor scopes are selected before pagination. A source-bound choice never enumerates another branch's delegations.
    private static BsonDocument HandoverScopeFilter(ResourceScope resource) {
        var allowed = new BsonArray();
        void Add(string kind, params (string Key, long? Value)[] values) {
            if (values.Any(value => value.Value is null)) return;
            var row = new BsonDocument("request.scope.kind", kind); foreach (var (key, value) in values) row.Add("request.scope." + key, value!.Value); allowed.Add(row);
        }
        Add("buyingOrganization", ("organizationId", resource.BuyingOrganizationId));
        Add("buyingBranch", ("organizationId", resource.BuyingOrganizationId), ("branchId", resource.BranchId));
        Add("buyingDivision", ("organizationId", resource.BuyingOrganizationId), ("branchId", resource.BranchId), ("divisionId", resource.DivisionId));
        Add("fulfillmentShop", ("organizationId", resource.FulfillmentOrganizationId), ("shopId", resource.ShopId), ("channelId", resource.ChannelId));
        Add("fulfillmentWarehouse", ("organizationId", resource.FulfillmentOrganizationId), ("shopId", resource.ShopId), ("channelId", resource.ChannelId), ("warehouseId", resource.WarehouseId));
        return allowed.Count == 0 ? new("_id", -1L) : new("$or", allowed);
    }
    public async Task<BsonDocument> ClaimHandoverAsync(ActorContext actor, long id, long revision, string binding,
        PaymentReviewHandoverInput handover, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        await RecordReviewActionAsync(actor, id, revision, new(binding, "claim"), metadata, ct, handover);
        return await RecoverHandoverAsync(actor, id, metadata.IdempotencyKey, ct) ?? throw B.Broken();
    }
    public async Task<BsonDocument?> RecoverHandoverAsync(ActorContext actor, long id, string key, CancellationToken ct = default) {
        if (id <= 0) throw PaymentReviewHandoverPolicy.Invalid();
        Metadata(new(key, "Recover original payment handover", "payment-handover-recovery")); await StorageAsync(ct); await H.StorageAsync(database, ct);
        var action = await RecoverReviewActionCoreAsync(actor, id, key, null, ct, includeHandover: true);
        if (action is null) return null;
        var proof = action["handoverProof"].AsBsonDocument; var result = W.Result(action);
        result.Add("handover", new BsonDocument { { "delegationId", proof["delegationId"] }, { "delegationRevision", proof["delegationRevision"] },
            { "delegationEventId", proof["delegationEventId"] }, { "originalAssigneeId", proof["originalAssigneeId"] },
            { "declarationRevision", proof["declarationRevision"] } }); return result;
    }
}
