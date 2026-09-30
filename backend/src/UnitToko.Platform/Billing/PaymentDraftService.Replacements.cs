using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.PaymentDraftBson;

namespace UnitToko.Platform.Billing;

public sealed record PaymentReplacementRequest(long ReviewRevision, long ActionId, string ActionHash, string Binding)
{
    internal static PaymentReplacementRequest Copy(PaymentReplacementRequest input) {
        if (input is null || input.ReviewRevision is <= 0 or >= long.MaxValue - 1 || input.ActionId <= 0 ||
            !Hash(input.ActionHash) || !Hash(input.Binding)) throw PaymentDraftBson.Invalid();
        return new(input.ReviewRevision, input.ActionId, input.ActionHash, input.Binding);
    }
    private static bool Hash(string value) => value is not null && value.Length == 64 && value.All(char.IsAsciiHexDigitLower);
    internal static PaymentReplacementRequest From(BsonDocument replacement) => Copy(new(replacement["reviewRevision"].AsInt64,
        replacement["actionId"].AsInt64, replacement["actionHash"].AsString, replacement["binding"].AsString));
}

public sealed record PaymentReplacementView(BsonDocument Source, PaymentReplacementRequest Request, BsonDocument? Existing)
{
    public UnitToko.Modules.Billing.PaymentDraftInput Input => B.Input(Source["input"].AsBsonDocument);
}
public sealed record PaymentReplacementOrigin(long ParentDeclarationId, long ParentDeclarationRevision);
public sealed partial class PaymentDraftService
{
    private sealed record ReplacementCreation(long ParentId, long Revision, PaymentReplacementRequest Input);
    private async Task ReplacementStorageAsync(CancellationToken ct) {
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", B.Versions) }, ct)).SingleOrDefaultAsync(ct) ?? throw B.Invalid();
        var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
            options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation) || options.GetValue("validationLevel", "strict") != "strict" || options.GetValue("validationAction", "error") != "error" ||
            !InventorySchemas.Contains(options.GetValue("validator", new BsonDocument()).AsBsonDocument, PaymentReplacementSchemas.Validator())) throw B.Invalid();
        var indexes = await (await records.Rows(B.Versions).Indexes.ListAsync(ct)).ToListAsync(ct); var ordinary = PaymentDraftSchemas.Key(B.Versions);
        bool Baseline(BsonDocument index) => index["key"].Equals(ordinary) && index.GetValue("unique", false).AsBoolean && !index.Contains("partialFilterExpression");
        bool Replacement(BsonDocument index) => index["key"].Equals(PaymentReplacementSchemas.Key()) && index.GetValue("unique", false).AsBoolean && index.GetValue("partialFilterExpression", BsonNull.Value).Equals(PaymentReplacementSchemas.Partial());
        if (!indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1))) || !indexes.Any(Baseline) || !indexes.Any(Replacement) ||
            indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") && !Replacement(index) || index.GetValue("unique", false).AsBoolean && index["name"] != "_id_" && !Baseline(index) && !Replacement(index))) throw B.Invalid();
    }
    public async Task<BsonDocument> CreateReplacementAsync(ActorContext actor, long parentId, long revision, PaymentReplacementRequest request,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (parentId <= 0 || revision <= 0) throw B.Invalid(); request = PaymentReplacementRequest.Copy(request); ReplacementKey(parentId, metadata.IdempotencyKey);
        return await SaveCoreAsync(actor, null, 0, null, metadata, new(parentId, revision, request), ct);
    }
    private async Task<BsonDocument> ReplacementSourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ReplacementCreation creation, CancellationToken ct) {
        var head = await HeadAsync(actor, authority, tx, creation.ParentId, ct);
        if (head["state"] != "submitted" || head["revision"] != creation.Revision) throw PaymentDraftRecords.Changed();
        var version = await records.VersionAsync(tx, head["versionId"].AsInt64, ct);
        var submission = await new PaymentSubmissionRecords(database).ReadAsync(tx, head["submissionId"].AsInt64, ct);
        var source = B.SubmittedSource(head, version, submission);
        var action = await new PaymentReviewActionRecords(database).LatestAsync(tx, creation.ParentId, source, ct) ?? throw B.Invalid();
        var request = creation.Input;
        if (action["state"] != "replacement_requested" || action["revision"] != request.ReviewRevision || action["_id"] != request.ActionId || action["snapshotHash"] != request.ActionHash ||
            request.Binding != PaymentReviewActionRecords.Binding(source, request.ReviewRevision) || action["recordedAt"].ToUniversalTime() > Now.UtcDateTime) throw PaymentDraftRecords.Changed();
        await new PaymentVerificationRecords(database).RequireUnverifiedAsync(tx, creation.ParentId, ct); await records.TouchAsync(tx, head, ct);
        return new() { { "source", source }, { "reviewRevision", request.ReviewRevision }, { "actionId", request.ActionId }, { "actionHash", request.ActionHash }, { "binding", request.Binding } };
    }
    public async Task<PaymentReplacementView> PreviewReplacementAsync(ActorContext actor, long parentId, long revision, CancellationToken ct = default) {
        if (parentId <= 0 || revision <= 0) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var head = await HeadAsync(actor, authority, tx, parentId, token);
            if (head["state"] != "submitted" || head["revision"] != revision) throw PaymentDraftRecords.Changed();
            var version = await records.VersionAsync(tx, head["versionId"].AsInt64, token);
            var submitted = await new PaymentSubmissionRecords(database).ReadAsync(tx, head["submissionId"].AsInt64, token);
            var source = B.SubmittedSource(head, version, submitted); var action = await new PaymentReviewActionRecords(database).LatestAsync(tx, parentId, source, token) ?? throw B.Invalid();
            if (action["state"] != "replacement_requested") throw PaymentDraftRecords.Changed();
            var request = new PaymentReplacementRequest(action["revision"].AsInt64, action["_id"].AsInt64, action["snapshotHash"].AsString, PaymentReviewActionRecords.Binding(source, action["revision"].AsInt64));
            _ = await ReplacementSourceAsync(actor, authority, tx, new(parentId, revision, request), token);
            var children = await records.Rows(B.Versions).Find(tx, new BsonDocument("replacement.source.declarationId", parentId)).Limit(2).ToListAsync(token);
            if (children.Count > 1) throw B.Invalid();
            BsonDocument? child = children.Count == 0 ? null : await records.VersionAsync(tx, children[0]["_id"].AsInt64, token);
            if (child is not null) { var childHead = await HeadAsync(actor, authority, tx, child["declarationId"].AsInt64, token); await records.TouchAsync(tx, childHead, token); }
            else foreach (var (name, field) in new[] { ("idempotencyRecords", "result.parentDeclarationId"), ("auditEvents", "result.parentDeclarationId"), ("outboxEvents", "payload.parentDeclarationId") })
                if (await records.Rows(name).Find(tx, new BsonDocument { { name == "idempotencyRecords" ? "operation" : "kind", B.ReplacementOperation }, { field, parentId } }).AnyAsync(token)) throw B.Invalid();
            await ReplacementReadAuditAsync(actor, tx, "preview", parentId, revision, token);
            await FenceAsync(actor, authority, tx, head["scopes"].AsBsonArray, token); return new PaymentReplacementView(source, request, child is null ? null : B.Result(child));
        }, ct);
    }
    public async Task<BsonDocument?> RecoverReplacementAsync(ActorContext actor, long parentId, string key, CancellationToken ct = default) {
        ReplacementKey(parentId, key); AuthorizationStore.ValidateMetadata(new(key, "Recover linked replacement", "payment-replacement-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            _ = Query(actor, authority); var row = await RecoveredVersionAsync(actor, tx, key, token, B.ReplacementOperation);
            if (row is null) {
                foreach (var name in new[] { "auditEvents", "outboxEvents" })
                    if (await records.Rows(name).Find(tx, new BsonDocument { { "kind", B.ReplacementOperation }, { "actorId", actor.UserId }, { "requestKey", key } }).AnyAsync(token)) throw B.Invalid();
                var parent = await HeadAsync(actor, authority, tx, parentId, token); await records.TouchAsync(tx, parent, token);
                await ReplacementReadAuditAsync(actor, tx, "recovery", parentId, parent["revision"].AsInt64, token);
                await FenceAsync(actor, authority, tx, parent["scopes"].AsBsonArray, token); return null;
            }
            if (row["replacement"]["source"]["declarationId"] != parentId) throw B.Invalid();
            var head = await HeadAsync(actor, authority, tx, row["declarationId"].AsInt64, token); await records.TouchAsync(tx, head, token);
            await ReplacementReadAuditAsync(actor, tx, "recovery", parentId, row["replacement"]["source"]["revision"].AsInt64, token);
            await FenceAsync(actor, authority, tx, head["scopes"].AsBsonArray, token); return B.Result(row);
        }, ct);
    }
    public async Task<PaymentReplacementOrigin?> ReadReplacementOriginAsync(ActorContext actor, long id, long revision, CancellationToken ct = default) {
        if (id <= 0 || revision <= 0) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var head = await HeadAsync(actor, authority, tx, id, token);
            if (head["revision"] != revision) throw PaymentDraftRecords.Changed();
            var first = await records.Rows(B.Versions).Find(tx, new BsonDocument { { "declarationId", id }, { "revision", 1L } }).SingleOrDefaultAsync(token) ?? throw B.Invalid();
            first = await records.VersionAsync(tx, first["_id"].AsInt64, token);
            PaymentReplacementOrigin? origin = first["schemaVersion"] == 2 ? new(first["replacement"]["source"]["declarationId"].AsInt64, first["replacement"]["source"]["revision"].AsInt64) : null;
            await ReplacementReadAuditAsync(actor, tx, "origin", id, revision, token); await records.TouchAsync(tx, head, token);
            await FenceAsync(actor, authority, tx, head["scopes"].AsBsonArray, token); return origin;
        }, ct);
    }
    private async Task ReplacementReadAuditAsync(ActorContext actor, IClientSessionHandle tx, string purpose, long id, long revision, CancellationToken ct) =>
        await records.Rows("auditEvents").InsertOneAsync(tx, new BsonDocument { { "_id", (await ids.NextAsync("auditEvents", ct)).Value }, { "schemaVersion", 1 },
            { "kind", "billing.payment.replacement-read." + purpose }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
            { "declarationId", id }, { "declarationRevision", revision }, { "createdAt", Now.UtcDateTime } }, cancellationToken: ct);
    private static void ReplacementKey(long parentId, string key) {
        var prefix = "rp." + parentId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
        if (parentId <= 0 || key is null || !key.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(key[prefix.Length..], "D", out var uuid) || uuid == Guid.Empty || uuid.ToString("D") != key[prefix.Length..]) throw B.Invalid();
    }
}
