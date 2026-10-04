using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.DocumentProcessing;

/// <summary>Finite private rescan. It never reopens the original validation job or changes its result.</summary>
internal sealed class EvidenceImageRevalidationProcessor(IMongoDatabase database, MongoUnitOfWork unit,
    IdAllocator ids, IClock clock, EvidenceStoreRegistration registration, string consumerId,
    string pipelineId, EvidenceLimits limits, IContentScanner scanner, IImageDecoder decoder)
{
    private const int MaximumAttempts = 5;
    private static readonly TimeSpan ClaimDuration = TimeSpan.FromMinutes(5);
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private readonly EvidenceService evidence = new(database, unit, ids, clock, registration);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static PersistenceConflictException Unavailable() => new("image_revalidation_policy_unavailable", "Reviewed image scanner approval is unavailable");
    private static PersistenceConflictException Lost() => new("image_revalidation_claim_lost", "Image scan claim changed or expired");

    internal async Task<string> RunOnceAsync(CancellationToken ct = default)
    {
        if (registration.Store is null) return "unavailable";
        await EvidenceImageRevalidationStorage.RequireAsync(database, ct);
        var selection = await unit.ExecuteAsync(async (tx, cancellation) =>
        {
            var now = clock.UtcNow.UtcDateTime;
            var policy = await Rows(EvidenceImageRevalidationSchemas.Policies).FindOneAndUpdateAsync(tx,
                new BsonDocument("$and", new BsonArray {
                    EvidenceImageRevalidationSchemas.Validator(EvidenceImageRevalidationSchemas.Policies),
                    new BsonDocument { { "consumerId", consumerId }, { "pipelineId", pipelineId },
                        { "storeId", registration.StoreId }, { "active", true },
                        { "approvedAt", new BsonDocument("$lte", now) }, { "expiresAt", new BsonDocument("$gt", now) },
                        { "fence", new BsonDocument("$lt", long.MaxValue) } } }),
                new BsonDocument("$inc", new BsonDocument("fence", 1L)),
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, cancellation);
            if (policy is null) return (Claim: (BsonDocument?)null, State: "unavailable");
            var owned = F.Eq("kind", EvidenceImageRevalidationRecords.Kind) & F.Eq("consumerId", consumerId) &
                F.Eq("pipelineId", pipelineId) & F.Eq("storeId", registration.StoreId) &
                F.Eq("policyId", policy["_id"]) & F.Eq("policyRevision", policy["revision"]);
            var expired = F.Eq("status", "leased") &
                (F.Lte("leaseUntil", now) | F.Lte("claimDeadline", now));
            var exhausted = await Rows(EvidenceImageRevalidationSchemas.Jobs).FindOneAndUpdateAsync(tx,
                owned & expired & F.Eq("attempts", MaximumAttempts),
                Builders<BsonDocument>.Update.Set("status", "dead").Set("leaseOwner", BsonNull.Value)
                    .Set("leaseToken", BsonNull.Value).Set("leaseUntil", BsonNull.Value)
                    .Set("claimDeadline", BsonNull.Value).Set("updatedAt", now),
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After, Sort = new BsonDocument("_id", 1) }, cancellation);
            if (exhausted is not null) return (Claim: (BsonDocument?)null, State: "dead");
            var eligible = (F.In("status", new[] { "pending", "retry" }) & F.Lte("dueAt", now)) | expired;
            var until = now.Add(ClaimDuration);
            if (until > policy["expiresAt"].ToUniversalTime()) until = policy["expiresAt"].ToUniversalTime();
            var claim = await Rows(EvidenceImageRevalidationSchemas.Jobs).FindOneAndUpdateAsync(tx,
                owned & eligible & F.Lt("attempts", MaximumAttempts),
                Builders<BsonDocument>.Update.Set("status", "leased").Inc("attempts", 1)
                    .Set("leaseOwner", consumerId).Set("leaseToken", Guid.NewGuid().ToString("N"))
                    .Set("leaseUntil", until).Set("claimDeadline", until).Set("updatedAt", now),
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After, Sort = new BsonDocument("_id", 1) }, cancellation);
            if (clock.UtcNow.UtcDateTime >= policy["expiresAt"].ToUniversalTime()) throw Unavailable();
            return (Claim: claim, State: claim is null ? "idle" : "leased");
        }, ct);
        var claim = selection.Claim;
        if (claim is null) return selection.State;
        try
        {
            await ProcessAsync(claim, ct);
            return "completed";
        }
        catch (ProcessingUnavailable)
        {
            // Unavailable scanners/storage cannot mint a clean result. A dead claim leaves
            // its head pending and therefore refuses content until governed intervention.
            var latest = clock.UtcNow.UtcDateTime;
            var changed = await Rows(EvidenceImageRevalidationSchemas.Jobs).UpdateOneAsync(Live(claim, latest),
                Builders<BsonDocument>.Update.Set("status", claim["attempts"].AsInt32 >= MaximumAttempts ? "dead" : "retry")
                    .Set("dueAt", latest.AddMinutes(1)).Set("leaseOwner", BsonNull.Value)
                    .Set("leaseToken", BsonNull.Value).Set("leaseUntil", BsonNull.Value)
                    .Set("claimDeadline", BsonNull.Value).Set("updatedAt", latest), cancellationToken: ct);
            return changed.ModifiedCount != 1 ? "claim_lost" : claim["attempts"].AsInt32 >= MaximumAttempts ? "dead" : "retry";
        }
    }

    private async Task ProcessAsync(BsonDocument claim, CancellationToken ct)
    {
        var start = await unit.ExecuteAsync(async (tx, token) =>
        {
            var live = await RequireLiveAsync(tx, claim, token);
            var request = await RequestAsync(tx, live, token);
            var policy = await PolicyAsync(tx, request, token);
            var original = await evidence.ImageRevalidationOriginalAsync(tx, request["source"].AsBsonDocument, token);
            CheckDeadline(live, policy, token);
            return (Job: live, Request: request, Policy: policy, Original: original);
        }, ct);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remaining = Deadline(start.Job) - clock.UtcNow;
        if (remaining <= TimeSpan.Zero) throw Lost();
        window.CancelAfter(remaining);
        ContentScanResult scan;
        ImageDecodeResult decode = new("unavailable");
        string? failure = null;
        try
        {
            await using (var original = await OpenAsync(start.Original, window.Token))
                scan = await scanner.ScanAsync(original.Stream, window.Token);
            if (scan.Status is not ("clean" or "infected") || scan.EngineVersion is null ||
                scan.EngineVersion.Length is < 1 or > 40 || scan.SignatureVersion is not > 0 ||
                scan.SignaturesAt is null) throw new ProcessingUnavailable();
            if (scan.Length != start.Original.Publication.Content.Length ||
                scan.Sha256 != start.Original.Publication.Content.Sha256) failure = "integrity_mismatch";
            else if (scan.Status == "infected") failure = "infected_media";
            else
            {
                await using var original = await OpenAsync(start.Original, window.Token);
                decode = await decoder.DecodeAsync(original.Stream, limits, window.Token);
                if (decode.Status == "invalid_media") failure = "invalid_media";
                else if (decode.Status != "decoded") throw new ProcessingUnavailable();
                else if (decode.Length != start.Original.Publication.Content.Length ||
                    decode.Sha256 != start.Original.Publication.Content.Sha256 ||
                    decode.Mime is not ("image/png" or "image/jpeg") ||
                    !limits.AcceptsImage(decode.Length ?? 0, decode.Width ?? 0, decode.Height ?? 0,
                        decode.Frames ?? 0, decode.Pages ?? 0)) failure = "integrity_mismatch";
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProcessingUnavailable(); }

        await unit.ExecuteAsync(async (tx, token) =>
        {
            var live = await RequireLiveAsync(tx, claim, token);
            var request = await RequestAsync(tx, live, token);
            if (!request.Equals(start.Request)) throw EvidenceImageRevalidationRecords.Broken();
            var policy = await PolicyAsync(tx, request, token);
            if (policy["_id"] != start.Policy["_id"] || policy["revision"] != start.Policy["revision"])
                throw Unavailable();
            var original = await evidence.ImageRevalidationOriginalAsync(tx, request["source"].AsBsonDocument, token);
            if (!original.Source.Equals(start.Original.Source) ||
                original.Publication != start.Original.Publication ||
                original.ReadPolicy["_id"] != start.Original.ReadPolicy["_id"] ||
                original.ReadPolicy["revision"] != start.Original.ReadPolicy["revision"])
                throw EvidenceImageRevalidationRecords.Broken();
            var current = await EvidenceImageRevalidationRecords.CurrentAsync(database, tx,
                request["versionRowId"].AsInt64, token);
            if (current.Head is null || current.Head["requestId"] != request["_id"] ||
                current.Head["status"] != "pending" || current.Current is not null)
                throw EvidenceImageRevalidationRecords.Broken();
            var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
            if (scan.SignaturesAt!.Value > now ||
                scan.SignaturesAt.Value.AddMilliseconds(original.ReadPolicy["maximumSignatureAgeMs"].AsInt64) <= now)
                throw new ProcessingUnavailable();
            var resultId = (await ids.NextAsync(EvidenceImageRevalidationSchemas.Results, token)).Value;
            var auditId = (await ids.NextAsync(EvidenceImageRevalidationSchemas.Audits, token)).Value;
            var row = new BsonDocument { { "_id", resultId }, { "schemaVersion", 1 },
                { "requestId", request["_id"] }, { "jobId", live["_id"] }, { "source", request["source"] },
                { "requestHash", request["requestHash"] }, { "storeId", registration.StoreId },
                { "generation", request["source"]["generation"] }, { "length", request["source"]["length"] },
                { "pipelineId", pipelineId }, { "consumerId", consumerId },
                { "policyId", policy["_id"] }, { "policyRevision", policy["revision"] },
                { "claimToken", live["leaseToken"] }, { "claimAttempt", live["attempts"] },
                { "checkedAt", now.UtcDateTime }, { "claimUntil", Deadline(live).UtcDateTime },
                { "auditId", auditId }, { "status", failure is null ? "passed" : "rejected" },
                { "scanStatus", scan.Status },
                { "decoderStatus", failure is null ? "decoded" : decode.Status == "invalid_media" ? "invalid" : "unavailable" },
                { "safeFailure", failure is null ? BsonNull.Value : new BsonString(failure) },
                { "mime", failure is null ? new BsonString(decode.Mime!) : BsonNull.Value },
                { "width", failure is null && decode.Width is { } width ? new BsonInt32(width) : BsonNull.Value },
                { "height", failure is null && decode.Height is { } height ? new BsonInt32(height) : BsonNull.Value },
                { "scannerEngine", scan.EngineVersion! }, { "scannerDefinitions", scan.SignatureVersion!.Value },
                { "scannerDefinitionsAt", scan.SignaturesAt!.Value.UtcDateTime } };
            row["resultHash"] = EvidenceImageRevalidationRecords.Hash(row, "resultHash");
            await Rows(EvidenceImageRevalidationSchemas.Results).InsertOneAsync(tx, row, cancellationToken: token);
            await Rows(EvidenceImageRevalidationSchemas.Audits).InsertOneAsync(tx, new BsonDocument {
                { "_id", auditId }, { "schemaVersion", 1 }, { "requestId", request["_id"] },
                { "versionRowId", request["versionRowId"] },
                { "resultId", resultId }, { "resultHash", row["resultHash"] },
                { "jobId", live["_id"] }, { "checkedAt", now.UtcDateTime } }, cancellationToken: token);
            var completed = live.DeepClone().AsBsonDocument;
            completed["status"] = "completed"; completed["resultHash"] = row["resultHash"];
            completed["leaseOwner"] = BsonNull.Value; completed["leaseToken"] = BsonNull.Value;
            completed["leaseUntil"] = BsonNull.Value; completed["claimDeadline"] = BsonNull.Value;
            completed["updatedAt"] = now.UtcDateTime;
            if ((await Rows(EvidenceImageRevalidationSchemas.Jobs).ReplaceOneAsync(tx, live, completed,
                    cancellationToken: token)).ModifiedCount != 1) throw Lost();
            var nextHead = current.Head.DeepClone().AsBsonDocument;
            nextHead["status"] = row["status"]; nextHead["resultId"] = resultId;
            nextHead["resultHash"] = row["resultHash"];
            nextHead["fence"] = current.Head["fence"].AsInt64 + 1;
            if ((await Rows(EvidenceImageRevalidationSchemas.Heads).ReplaceOneAsync(tx, current.Head,
                    nextHead, cancellationToken: token)).ModifiedCount != 1) throw Lost();
            CheckDeadline(live, policy, token);
            return true;
        }, ct);
    }

    private async Task<BsonDocument> RequestAsync(IClientSessionHandle tx, BsonDocument job, CancellationToken ct)
    {
        var request = await Rows(EvidenceImageRevalidationSchemas.Requests).Find(tx,
            F.Eq("_id", job["requestId"])).SingleOrDefaultAsync(ct) ?? throw EvidenceImageRevalidationRecords.Broken();
        if (!await Rows(EvidenceImageRevalidationSchemas.Requests).Find(tx, new BsonDocument("$and", new BsonArray {
                new BsonDocument("_id", request["_id"]), EvidenceImageRevalidationSchemas.Validator(EvidenceImageRevalidationSchemas.Requests) })).AnyAsync(ct) ||
            request["requestHash"] != EvidenceImageRevalidationRecords.Hash(request, "requestHash") ||
            request["pipelineId"] != pipelineId || request["consumerId"] != consumerId ||
            job["consumerId"] != request["consumerId"] || job["pipelineId"] != request["pipelineId"] ||
            job["storeId"] != request["source"]["storeId"] || job["policyId"] != request["policyId"] ||
            job["policyRevision"] != request["policyRevision"])
            throw EvidenceImageRevalidationRecords.Broken();
        return request;
    }

    private async Task<BsonDocument> RequireLiveAsync(IClientSessionHandle tx, BsonDocument claim, CancellationToken ct)
    {
        var job = await Rows(EvidenceImageRevalidationSchemas.Jobs).Find(tx, Live(claim, clock.UtcNow.UtcDateTime))
            .SingleOrDefaultAsync(ct) ?? throw Lost();
        if (job["attempts"] != claim["attempts"] || job["sourceKey"] != claim["sourceKey"] ||
            job["requestId"] != claim["requestId"]) throw Lost();
        return job;
    }
    private FilterDefinition<BsonDocument> Live(BsonDocument claim, DateTime now) =>
        F.Eq("_id", claim["_id"]) & F.Eq("status", "leased") &
        F.Eq("leaseOwner", consumerId) & F.Eq("leaseToken", claim["leaseToken"]) &
        F.Gt("leaseUntil", now) & F.Gt("claimDeadline", now);

    private async Task<BsonDocument> PolicyAsync(IClientSessionHandle tx, BsonDocument request, CancellationToken ct) =>
        await Rows(EvidenceImageRevalidationSchemas.Policies).FindOneAndUpdateAsync(tx,
            new BsonDocument("$and", new BsonArray {
                EvidenceImageRevalidationSchemas.Validator(EvidenceImageRevalidationSchemas.Policies),
                new BsonDocument { { "_id", request["policyId"] }, { "revision", request["policyRevision"] },
                    { "consumerId", consumerId }, { "pipelineId", pipelineId }, { "storeId", registration.StoreId },
                    { "active", true }, { "approvedAt", new BsonDocument("$lte", clock.UtcNow.UtcDateTime) },
                    { "expiresAt", new BsonDocument("$gt", clock.UtcNow.UtcDateTime) },
                    { "fence", new BsonDocument("$lt", long.MaxValue) } } }),
            new BsonDocument("$inc", new BsonDocument("fence", 1L)),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct) ?? throw Unavailable();
    private static DateTimeOffset Deadline(BsonDocument job) =>
        new(job["leaseUntil"].ToUniversalTime() < job["claimDeadline"].ToUniversalTime() ?
            job["leaseUntil"].ToUniversalTime() : job["claimDeadline"].ToUniversalTime());
    private void CheckDeadline(BsonDocument job, BsonDocument policy, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (clock.UtcNow >= Deadline(job)) throw Lost();
        if (clock.UtcNow >= new DateTimeOffset(policy["expiresAt"].ToUniversalTime())) throw Unavailable();
    }
    private async Task<VerifiedOriginalHandle> OpenAsync(ImageRevalidationOriginal original, CancellationToken ct) =>
        await registration.Store!.OpenVerifiedAsync(original.Publication, original.Budget, ct) is OpenOutcome.Opened opened
            ? opened.Handle : throw new ProcessingUnavailable();
    private sealed class ProcessingUnavailable : Exception { }
}
