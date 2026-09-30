using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Billing.PaymentDraftBson;

namespace UnitToko.Platform.Billing;

public sealed record PaymentDuplicateMatch(long DeclarationId, long Revision, long SubmissionId, DateTimeOffset SubmittedAt, ImmutableArray<string> Signals);
public sealed record PaymentDuplicatePage(long DeclarationId, long Revision, long SubmissionId, ImmutableArray<PaymentDuplicateMatch> Items, bool HasMore);

public sealed partial class PaymentDraftService
{
    // These exact-source hints neither decide payment status nor establish transfer identity.
    // Authorization precedes matching and pagination, so hidden submissions affect no result or count.
    public async Task<PaymentDuplicatePage> ReadDuplicatesAsync(ActorContext actor, long id, long revision, EvidenceRef evidence,
        long? beforeId, AuthorizationReadMetadata metadata, CancellationToken ct = default) {
        if (id <= 0 || revision <= 0 || beforeId is <= 0 || evidence is null) throw B.Invalid();
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var owner = await ReviewHeadAsync(actor, authority, tx, id, token);
            var version = await records.VersionAsync(tx, owner["versionId"].AsInt64, token);
            var submitted = await new PaymentSubmissionRecords(database).ReadAsync(tx, owner["submissionId"].AsInt64, token);
            if (owner["revision"] != revision || !submitted["evidence"].Equals(PaymentSubmissionBson.Reference(evidence))) throw PaymentDraftRecords.Changed();
            var queries = ReviewQueries(actor, authority);
            var scoped = new BsonDocument("$and", new BsonArray(queries.Select(query => ScopeQuery.Create(query).Render(new RenderArgs<BsonDocument>(BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry)))));
            var idsFilter = new BsonDocument("$ne", id); if (beforeId.HasValue) idsFilter.Add("$lt", beforeId.Value);
            var signals = new BsonArray { new BsonDocument("submission.evidence.sha256", evidence.Sha256) };
            var reference = version["input"]["bankReference"];
            if (!reference.IsBsonNull) signals.Add(new BsonDocument("version.input.bankReference", reference));
            BsonDocument[] pipeline = [
                new("$match", new BsonDocument { { "state", "submitted" }, { "_id", idsFilter } }),
                new("$set", new BsonDocument("scopeCount", new BsonDocument("$size", "$scopes"))), new("$unwind", "$scopes"),
                new("$replaceRoot", new BsonDocument("newRoot", new BsonDocument("$mergeObjects", new BsonArray { "$scopes", new BsonDocument {
                    { "declarationId", "$_id" }, { "scopeCount", "$scopeCount" }, { "versionId", "$versionId" }, { "submissionId", "$submissionId" } } }))),
                new("$match", scoped), new("$group", new BsonDocument { { "_id", "$declarationId" }, { "matches", new BsonDocument("$sum", 1) },
                    { "expected", new BsonDocument("$first", "$scopeCount") }, { "versionId", new BsonDocument("$first", "$versionId") }, { "submissionId", new BsonDocument("$first", "$submissionId") } }),
                new("$match", new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$matches", "$expected" }))),
                new("$lookup", new BsonDocument { { "from", B.Versions }, { "localField", "versionId" }, { "foreignField", "_id" }, { "as", "version" } }), new("$unwind", "$version"),
                new("$lookup", new BsonDocument { { "from", "paymentSubmissions" }, { "localField", "submissionId" }, { "foreignField", "_id" }, { "as", "submission" } }), new("$unwind", "$submission"),
                new("$match", new BsonDocument("$or", signals)), new("$sort", new BsonDocument("_id", -1)), new("$limit", 26), new("$project", new BsonDocument("_id", 1))];
            var selected = await records.Rows(B.Heads).Aggregate<BsonDocument>(tx, pipeline).ToListAsync(token);
            var result = ImmutableArray.CreateBuilder<PaymentDuplicateMatch>(); var scopes = owner["scopes"].DeepClone().AsBsonArray;
            foreach (var item in selected) {
                var candidate = await ReviewHeadAsync(actor, authority, tx, item["_id"].AsInt64, token);
                var candidateVersion = await records.VersionAsync(tx, candidate["versionId"].AsInt64, token);
                var candidateSubmission = await new PaymentSubmissionRecords(database).ReadAsync(tx, candidate["submissionId"].AsInt64, token);
                var matches = ImmutableArray.CreateBuilder<string>();
                if (candidateSubmission["evidence"]["sha256"] == evidence.Sha256) matches.Add("same_image");
                if (!reference.IsBsonNull && reference == candidateVersion["input"]["bankReference"]) matches.Add("same_reference");
                if (matches.Count == 0) throw PaymentDraftRecords.Changed();
                result.Add(new(candidate["_id"].AsInt64, candidate["revision"].AsInt64, candidateSubmission["_id"].AsInt64,
                    new(candidateSubmission["submittedAt"].ToUniversalTime()), matches.ToImmutable()));
                await records.TouchAsync(tx, candidate, token); scopes.AddRange(candidate["scopes"].AsBsonArray);
            }
            await ReviewAuditAsync(actor, tx, "billing.payment.review.duplicates", [id, .. selected.Select(item => item["_id"].AsInt64)], metadata, token);
            await records.TouchAsync(tx, owner, token); await ReviewFenceAsync(actor, authority, tx, scopes, token);
            return new PaymentDuplicatePage(id, revision, submitted["_id"].AsInt64, [.. result.Take(25)], selected.Count > 25);
        }, ct);
    }
}
