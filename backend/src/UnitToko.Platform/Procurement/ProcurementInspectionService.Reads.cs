using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using B = UnitToko.Platform.Procurement.ProcurementInspectionBson;
using C = UnitToko.Platform.Procurement.ProcurementReceivingBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementInspectionService
{
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, CancellationToken ct = default) {
        await StorageAsync(ct); return await receiving.TargetsAsync(actor, ct);
    }
    private BsonDocument ReadView(Context context) {
        context.IncludeEvidence = ScopePolicy.Authorize(context.Actor, context.Authority, Permissions.ProcurementInspectionEvidenceRead, context.Resource, clock.UtcNow).Allowed;
        return new() { { "draft", B.DraftView(context.Draft!) }, { "capacity", context.History!.Capacity },
            { "inspection", context.Submission is null ? BsonNull.Value : B.SubmissionView(context.Submission, context.IncludeEvidence) } };
    }
    public async Task<BsonDocument> IntakesAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw ProcurementInspectionRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var scope = ContextFor(actor, authority, tx, location, Permissions.ProcurementRead); await PeopleAsync(scope, null, token);
            var filter = new BsonDocument("location", R.Location(location)); if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(C.Intakes).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray(); var contexts = new List<Context>();
            foreach (var row in rows.Take(25)) {
                var context = await IntakeContextAsync(actor, authority, tx, row["noticeId"].AsInt64, row["_id"].AsInt64, Permissions.ProcurementRead, token);
                ProcurementInspectionRecords.Equal(R.Location(location), context.History!.Source["notice"]["location"]);
                var item = B.SourceView(context.History.Source); item.Add("capacity", context.History.Capacity); items.Add(item);
                await FenceAsync(context, token); contexts.Add(context);
            }
            await records.OrphansAsync(tx, token); await FenceAsync(scope, token); foreach (var context in contexts) Current(context); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "location", R.Location(location) }, { "items", items }, { "nextIntakeId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, long draftId, CancellationToken ct = default) {
        if (draftId <= 0) throw ProcurementInspectionRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await DraftContextAsync(actor, authority, tx, draftId, Permissions.ProcurementRead, false, token); var result = ReadView(context);
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> ListAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw ProcurementInspectionRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var scope = ContextFor(actor, authority, tx, location, Permissions.ProcurementRead); await PeopleAsync(scope, null, token);
            // ponytail: verify complete scoped history before a bounded page; do not replace missing-head/effect checks with a count-only shortcut.
            var rows = await Rows(B.Drafts).Find(tx, new BsonDocument("location", R.Location(location))).Sort(new BsonDocument("_id", -1)).ToListAsync(token);
            var selected = rows.Where(row => beforeId is null || row["_id"].AsInt64 < beforeId).Take(26).ToArray(); var items = new BsonArray(); var contexts = new List<Context>();
            foreach (var row in rows) {
                var context = await DraftContextAsync(actor, authority, tx, row["_id"].AsInt64, Permissions.ProcurementRead, false, token);
                if (selected.Take(25).Any(candidate => candidate["_id"] == row["_id"])) items.Add(ReadView(context));
                await FenceAsync(context, token); contexts.Add(context);
            }
            await records.OrphansAsync(tx, token); await FenceAsync(scope, token); foreach (var context in contexts) Current(context); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "location", R.Location(location) }, { "items", items }, { "nextDraftId", selected.Length > 25 ? selected[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument?> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        if (operation is not (B.DraftOperation or B.SubmitOperation)) throw ProcurementInspectionRules.Invalid(); Key(key); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var row = await ExistingAsync(actor, authority, tx, operation, key, token);
            if (row is null) { await FenceQueryAsync(actor, authority, tx, Permissions.ProcurementInspect, token); return null; }
            var context = await DraftContextAsync(actor, authority, tx, row["draftId"].AsInt64, Permissions.ProcurementInspect, false, token);
            var actual = operation == B.DraftOperation ? context.Draft! : context.Submission ?? throw B.Broken(); ProcurementInspectionRecords.Equal(actual["_id"], row["_id"]);
            await FenceAsync(context, token); return B.Result(actual);
        }, ct);
    }
}
