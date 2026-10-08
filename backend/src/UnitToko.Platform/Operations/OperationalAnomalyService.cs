using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Operations;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using R = UnitToko.Platform.Operations.OperationalAnomalyRecords;

namespace UnitToko.Platform.Operations;

public sealed class OperationalAnomalyService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids,
    IClock clock, AuthorizationStore authorization, OrderSubmissionService orders)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private sealed record Source(string Kind, long Id, ResourceScope Scope, BsonDocument Row, BsonDocument Owner, string Hash);
    private static string Table(string kind) => kind switch { "order" => "orderSuborders", "refund" => "refundPayouts", _ => throw R.Unavailable() };
    private static string[] SourcePermissions(string kind) => kind == "order" ? [Permissions.OrderRead] :
        [Permissions.DisbursementRecord, Permissions.CaseRead, Permissions.InvoiceRead, Permissions.PaymentReviewRead];
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot authority, string permission)
    {
        var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow);
        query = query with { Clauses = [.. query.Clauses.Where(clause => clause.Scope.Kind == GrantScopeKind.FulfillmentShop)] };
        AuthorizationStore.Require(query); return query;
    }
    private AuthorizationQuery[] Require(ActorContext actor, AuthorizationSnapshot authority, string kind, ResourceScope scope, string permission)
    {
        RequireSourceAssurance(actor, kind);
        var queries = SourcePermissions(kind).Append(Permissions.AnomalyRead).Append(permission).Distinct()
            .Select(value => Query(actor, authority, value)).ToArray();
        if (queries.Any(query => !query.Matches(scope))) throw new AuthorizationDeniedException("scope_denied");
        return queries;
    }
    private FilterDefinition<BsonDocument> Filter(ActorContext actor, AuthorizationSnapshot authority, string kind)
    {
        RequireSourceAssurance(actor, kind);
        var prefix = kind == "refund" ? "party." : "";
        return F.And(SourcePermissions(kind).Append(Permissions.AnomalyRead).Select(permission => {
            var query = Query(actor, authority, permission);
            return F.Or(query.Clauses.Select(clause => F.Eq(prefix + "fulfillmentOrganizationId", clause.Scope.OrganizationId!.Value) &
                F.Eq(prefix + "shopId", clause.Scope.ShopId!.Value) & F.Eq(prefix + "channelId", clause.Scope.ChannelId!.Value)));
        }));
    }
    private void RequireSourceAssurance(ActorContext actor, string kind) {
        if (kind == "refund" && (actor.Assurance is not (FactorAssurance.MultiFactor or FactorAssurance.PhishingResistant) ||
            actor.FactorCompletedAt > clock.UtcNow || clock.UtcNow - actor.FactorCompletedAt > TimeSpan.FromMinutes(5)))
            throw new AuthorizationDeniedException("step_up_required");
    }
    private async Task<Source> SourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        string kind, long id, string permission, CancellationToken ct)
    {
        if (id <= 0) throw R.Unavailable();
        var row = await Rows(Table(kind)).Find(tx, Filter(actor, authority, kind) & F.Eq("_id", id)).SingleOrDefaultAsync(ct) ??
            throw new AuthorizationDeniedException("scope_denied");
        var facts = kind == "order" ? row : row["party"].AsBsonDocument;
        var scope = new ResourceScope(FulfillmentOrganizationId: facts["fulfillmentOrganizationId"].AsInt64,
            ShopId: facts["shopId"].AsInt64, ChannelId: facts["channelId"].AsInt64);
        _ = Require(actor, authority, kind, scope, permission);
        BsonDocument owner;
        if (kind == "order") {
            owner = await orders.AnomalyOrderAsync(tx, row["orderId"].AsInt64, ct);
            if (!owner["suborderIds"].AsBsonArray.Contains(row["_id"])) throw R.Unavailable();
        } else {
            owner = await new RefundPayoutRecords(database, clock, unit).PayoutAsync(tx, id, false, ct);
            if (row != owner) throw R.Changed();
        }
        return new(kind, id, scope, row, owner, R.Hash(new() { { "source", R.Stable(row) }, { "owner", R.Stable(owner) } }));
    }
    private async Task<BsonDocument> PolicyAsync(IClientSessionHandle tx, ResourceScope scope, bool create, CancellationToken ct)
    {
        var key = R.PolicyKey(scope); var row = await Rows(R.Policies).Find(tx, F.Eq("_id", key)).SingleOrDefaultAsync(ct);
        if (row is null) {
            row = new() { { "_id", key }, { "schemaVersion", 1 }, { "scope", R.Scope(scope) }, { "revision", 1L }, { "fence", 0L }, { "policy", BsonNull.Value }, { "versionId", BsonNull.Value } };
            // This is an explicit absent-policy owner for concurrency, never a default review threshold.
            if (create) await Rows(R.Policies).InsertOneAsync(tx, row, cancellationToken: ct);
        }
        if (row["scope"] != R.Scope(scope) || row["revision"].AsInt64 <= 0 || row["fence"].AsInt64 < 0) throw R.Unavailable();
        _ = R.Policy(row);
        var versions = await Rows(R.PolicyVersions).Find(tx, F.Eq("scope", R.Scope(scope))).Sort(new BsonDocument("revision", 1)).Limit(1001).ToListAsync(ct);
        if (versions.Count > 1000 || row["revision"].AsInt64 != versions.Count + 1L) throw R.Unavailable();
        for (var index = 0; index < versions.Count; index++) {
            var version = versions[index]; var policy = R.Policy(version) ?? throw R.Unavailable();
            var body = new { kind = version["sourceKind"].AsString, id = version["sourceId"].AsInt64,
                expectedRevision = version["expectedRevision"].AsInt64, policy };
            var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", version["actorId"]) & F.Eq("operation", R.ConfigureOperation) & F.Eq("key", version["requestKey"])).SingleOrDefaultAsync(ct);
            if (version["revision"] != index + 2L || version["expectedRevision"] != index + 1L ||
                version["bodyHash"] != AuthorizationStore.CommandHash(body, version["reason"].AsString) || receipt is null ||
                receipt["bodyHash"] != version["bodyHash"] || receipt["result"]["versionId"] != version["_id"] ||
                receipt["result"]["versionHash"] != R.Hash(version)) throw R.Unavailable();
        }
        if (versions.Count == 0 ? !row["policy"].IsBsonNull || !row["versionId"].IsBsonNull :
            row["policy"] != versions[^1]["policy"] || row["versionId"] != versions[^1]["_id"]) throw R.Unavailable();
        return row;
    }
    public async Task<string> ConfigurePolicyAsync(ActorContext actor, string kind, long id, long expectedRevision,
        AnomalyReviewPolicy policy, AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        OperationalAnomalyRules.Validate(policy); await R.RequireStorageAsync(database, ct);
        Source? source = null; BsonDocument? head = null; var body = new { kind, id, expectedRevision, policy };
        var result = await authorization.ExecuteCommandAsync(actor, R.ConfigureOperation, metadata, body,
            async (authority, tx, token) => {
                source = await SourceAsync(actor, authority, tx, kind, id, Permissions.AnomalyPolicyConfigure, token);
                return new(ScopePolicy.Authorize(actor, authority, Permissions.AnomalyPolicyConfigure, source.Scope, clock.UtcNow),
                    ScopePolicy.RequiredNodes(source.Scope, authority.Directory), Permissions.AnomalyPolicyConfigure, new(source.Scope));
            }, async (tx, token) => {
                head = await PolicyAsync(tx, source!.Scope, true, token);
                if (head["revision"] != expectedRevision || expectedRevision >= 1001 ||
                    await Rows(R.PolicyVersions).Find(tx, F.Eq("scope", R.Scope(source.Scope)) & F.Eq("policy.version", policy.Version)).AnyAsync(token)) throw R.Changed();
                var version = Metadata((await ids.NextAsync(R.PolicyVersions, token)).Value, actor, metadata, body, source.Scope);
                version.AddRange(new BsonDocument { { "sourceKind", kind }, { "sourceId", id }, { "expectedRevision", expectedRevision },
                    { "revision", expectedRevision + 1 }, { "policy", R.Policy(policy) } });
                await Rows(R.PolicyVersions).InsertOneAsync(tx, version, cancellationToken: token);
                var replacement = head.DeepClone().AsBsonDocument; replacement["policy"] = version["policy"].DeepClone();
                replacement["versionId"] = version["_id"]; replacement["revision"] = expectedRevision + 1;
                if ((await Rows(R.Policies).ReplaceOneAsync(tx, head, replacement, cancellationToken: token)).ModifiedCount != 1) throw R.Changed();
                return new() { { "versionId", version["_id"] }, { "versionHash", R.Hash(version) }, { "policyVersion", policy.Version } };
            }, ct, complete: async (authority, tx, token) => {
                head = await PolicyAsync(tx, source!.Scope, false, token);
                await FenceAsync(actor, authority, tx, source, Permissions.AnomalyPolicyConfigure, head, token);
            });
        return result["policyVersion"].AsString;
    }
    public async Task<AnomalyPolicyRecovery> RecoverPolicyAsync(ActorContext actor, string kind, long id, string key, CancellationToken ct = default)
    {
        if (key.Length is < 1 or > 128 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw R.Unavailable();
        await R.RequireStorageAsync(database, ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var source = await SourceAsync(actor, authority, tx, kind, id, Permissions.AnomalyPolicyConfigure, token);
            var head = await PolicyAsync(tx, source.Scope, false, token);
            var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", R.ConfigureOperation) & F.Eq("key", key)).SingleOrDefaultAsync(token);
            AnomalyPolicyRecovery result;
            if (receipt is null) result = new("not_confirmed", null);
            else {
                var version = await Rows(R.PolicyVersions).Find(tx, F.Eq("_id", receipt["result"]["versionId"])).SingleOrDefaultAsync(token) ?? throw R.Unavailable();
                if (version["scope"] != R.Scope(source.Scope) || version["actorId"] != actor.UserId || version["sourceKind"] != kind || version["sourceId"] != id ||
                    version["requestKey"] != key || version["bodyHash"] != receipt["bodyHash"] || R.Hash(version) != receipt["result"]["versionHash"]) throw R.Unavailable();
                result = new("confirmed", version["policy"]["version"].AsString);
            }
            await FenceAsync(actor, authority, tx, source, Permissions.AnomalyPolicyConfigure, head["policy"].IsBsonNull && head["revision"] == 1L ? null : head, token);
            return result;
        }, ct);
    }
    private static AnomalySignal[] Signals(Source source, BsonDocument policy)
    {
        var reviewPolicy = R.Policy(policy);
        if (source.Kind == "refund") {
            var row = source.Owner;
            return [.. OperationalAnomalyRules.Refund(new(Decimal128.ToDecimal(row["input"]["amount"].AsDecimal128),
                Decimal128.ToDecimal(row["source"]["approval"]["reservedRefund"].AsDecimal128), row["source"]["payingBank"]["currency"].AsString), reviewPolicy),
                OperationalAnomalyRules.AdjustmentUnavailable()];
        }
        var lines = source.Owner["quote"]["lines"].AsBsonArray.Where(line => source.Row["skuIds"].AsBsonArray.Contains(line["definition"]["skuId"]))
            .Select(line => new AnomalyOrderLine(line["skuCode"].AsString + "/" + line["definition"]["_id"].AsInt64,
                line["packages"].AsInt32, Decimal128.ToDecimal(line["price"]["unitPrice"].AsDecimal128),
                Decimal128.ToDecimal(line["subtotal"].AsDecimal128), source.Owner["quote"]["currency"].AsString)).ToArray();
        return [.. OperationalAnomalyRules.Order(lines, reviewPolicy), OperationalAnomalyRules.AdjustmentUnavailable()];
    }
    public async Task<AnomalyOperationRecovery> RecoverAsync(ActorContext actor, string kind, long sourceId, string operation,
        string key, long? evaluationId = null, CancellationToken ct = default)
    {
        if (operation is not ("evaluate" or "review") || (operation == "review") != evaluationId.HasValue || evaluationId is <= 0 ||
            key.Length is < 1 or > 128 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw R.Unavailable();
        await R.RequireStorageAsync(database, ct);
        var recoveredId = await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var permission = operation == "review" ? Permissions.AnomalyReview : Permissions.AnomalyRead;
            var source = await SourceAsync(actor, authority, tx, kind, sourceId, permission, token);
            var policy = await PolicyAsync(tx, source.Scope, false, token);
            var operationName = operation == "review" ? R.ReviewOperation : R.EvaluateOperation;
            var original = F.Eq("actorId", actor.UserId) & F.Eq("requestKey", key);
            var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", operationName) & F.Eq("key", key)).SingleOrDefaultAsync(token);
            long? found = null;
            if (receipt is null) {
                // Missing receipt with a persisted owner is broken provenance, never proof of absence.
                if (await Rows(operation == "review" ? R.Dispositions : R.Evaluations).Find(tx, original).AnyAsync(token)) throw R.Unavailable();
            } else {
                var row = await EvaluationRowAsync(tx, receipt["result"]["evaluationId"].AsInt64, token);
                if (row["sourceKind"] != kind || row["sourceId"] != sourceId || row["scope"] != R.Scope(source.Scope)) throw R.Unavailable();
                if (operation == "evaluate") {
                    if (row["actorId"] != actor.UserId || row["requestKey"] != key || row["bodyHash"] != receipt["bodyHash"] ||
                        receipt["result"]["evaluationHash"] != R.Hash(R.Stable(row))) throw R.Unavailable();
                } else {
                    if (row["_id"] != evaluationId!.Value) throw R.Unavailable();
                    var disposition = await Rows(R.Dispositions).Find(tx, original & F.Eq("_id", receipt["result"]["dispositionId"]) &
                        OperationalAnomalySchemas.Validator(R.Dispositions)).SingleOrDefaultAsync(token) ?? throw R.Unavailable();
                    var body = new { id = evaluationId.Value, outcome = disposition["outcome"].AsString };
                    if (disposition["evaluationId"] != row["_id"] || disposition["evaluationHash"] != R.Hash(R.Stable(row)) || disposition["scope"] != row["scope"] ||
                        disposition["bodyHash"] != AuthorizationStore.CommandHash(body, disposition["reason"].AsString) ||
                        disposition["bodyHash"] != receipt["bodyHash"] || receipt["result"]["dispositionHash"] != R.Hash(disposition)) throw R.Unavailable();
                }
                await TouchAsync(tx, R.Evaluations, row, token); found = row["_id"].AsInt64;
            }
            await FenceAsync(actor, authority, tx, source, permission, policy["policy"].IsBsonNull && policy["revision"] == 1L ? null : policy, token); return found;
        }, ct);
        // Re-read through the same owner proof and current authority after receipt lookup.
        return recoveredId.HasValue ? new("confirmed", await ReadAsync(actor, recoveredId.Value, ct)) : new("not_confirmed", null);
    }
    private async Task TouchAsync(IClientSessionHandle tx, string name, BsonDocument row, CancellationToken ct)
    {
        if (row["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(name).UpdateOneAsync(tx, row,
            Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw R.Changed();
    }
    private async Task FenceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        Source source, string permission, BsonDocument? policy, CancellationToken ct)
    {
        var queries = Require(actor, authority, source.Kind, source.Scope, permission);
        var clauses = queries.SelectMany(query => query.Clauses.Where(clause => clause.Matches(source.Scope))).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
            ScopePolicy.RequiredNodes(source.Scope, authority.Directory).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        if (source.Kind == "order") {
            await orders.FenceAnomalyOrderAsync(tx, source.Owner, ct);
            await TouchAsync(tx, "orderSuborders", source.Row, ct);
        } else await new RefundPayoutRecords(database, clock, unit).TouchAsync(tx, "refundPayouts", source.Row, ct);
        if (policy is not null) await TouchAsync(tx, R.Policies, policy, ct);
        _ = Require(actor, await authorization.LoadAsync(actor, tx, ct), source.Kind, source.Scope, permission);
        ct.ThrowIfCancellationRequested();
    }
    public async Task<AnomalySourceChoices> ChoicesAsync(ActorContext actor, string kind, long? before = null, CancellationToken ct = default)
    {
        if (before is <= 0) throw R.Unavailable();
        _ = Table(kind); await R.RequireStorageAsync(database, ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var filter = Filter(actor, authority, kind);
            if (before.HasValue) filter &= F.Lt("_id", before.Value);
            var rows = await Rows(Table(kind)).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(51).ToListAsync(token);
            var choices = new List<AnomalySourceChoice>();
            foreach (var row in rows.Take(50)) {
                var source = await SourceAsync(actor, authority, tx, kind, row["_id"].AsInt64, Permissions.AnomalyRead, token);
                await FenceAsync(actor, authority, tx, source, Permissions.AnomalyRead, null, token);
                choices.Add(new(kind, new(source.Id), (kind == "order" ? "Bagian pesanan #" : "Pengembalian dana #") + source.Id));
            }
            return new AnomalySourceChoices(choices, rows.Count > 50 ? choices[^1].Id : null, OperationalAnomalyRules.AdjustmentUnavailable());
        }, ct);
    }
    public async Task<AnomalyEvaluation> EvaluateAsync(ActorContext actor, string kind, long id,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        await R.RequireStorageAsync(database, ct); Source? source = null; BsonDocument? policy = null;
        var body = new { kind, id };
        var receipt = await authorization.ExecuteCommandAsync(actor, R.EvaluateOperation, metadata, body,
            async (authority, tx, token) => {
                policy = null;
                source = await SourceAsync(actor, authority, tx, kind, id, Permissions.AnomalyRead, token);
                var decision = ScopePolicy.Authorize(actor, authority, Permissions.AnomalyRead, source.Scope, clock.UtcNow);
                return new(decision, ScopePolicy.RequiredNodes(source.Scope, authority.Directory), Permissions.AnomalyRead, new(source.Scope));
            }, async (tx, token) => {
                policy = await PolicyAsync(tx, source!.Scope, true, token);
                var row = Metadata((await ids.NextAsync(R.Evaluations, token)).Value, actor, metadata, body, source.Scope);
                row.AddRange(new BsonDocument { { "sourceKind", kind }, { "sourceId", id }, { "sourceHash", source.Hash },
                    { "ruleVersion", OperationalAnomalyRules.Version }, { "policyVersion", (BsonValue?)R.Policy(policy)?.Version ?? BsonNull.Value },
                    { "policyRevision", policy["revision"] }, { "policyHash", R.Hash(R.Stable(policy)) }, { "fence", 0L },
                    { "signals", new BsonArray(Signals(source, policy).Select(R.Signal)) } });
                await Rows(R.Evaluations).InsertOneAsync(tx, row, cancellationToken: token);
                return new() { { "evaluationId", row["_id"] }, { "evaluationHash", R.Hash(R.Stable(row)) } };
            }, ct, complete: async (authority, tx, token) => {
                policy ??= await PolicyAsync(tx, source!.Scope, false, token);
                await FenceAsync(actor, authority, tx, source!, Permissions.AnomalyRead, policy, token);
            });
        return await ReadAsync(actor, receipt["evaluationId"].AsInt64, ct);
    }
    private BsonDocument Metadata(long id, ActorContext actor, AuthorizationCommandMetadata metadata, object body, ResourceScope scope) => new() {
        { "_id", id }, { "schemaVersion", 1 }, { "scope", R.Scope(scope) }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
        { "recordedAt", clock.UtcNow.UtcDateTime }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason) },
        { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId } };
    private async Task<BsonDocument> EvaluationRowAsync(IClientSessionHandle tx, long id, CancellationToken ct)
    {
        var row = await Rows(R.Evaluations).Find(tx, F.Eq("_id", id) & OperationalAnomalySchemas.Validator(R.Evaluations)).SingleOrDefaultAsync(ct) ?? throw R.Unavailable();
        var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", row["actorId"]) & F.Eq("operation", R.EvaluateOperation) & F.Eq("key", row["requestKey"])).SingleOrDefaultAsync(ct);
        var body = new { kind = row["sourceKind"].AsString, id = row["sourceId"].AsInt64 };
        if (row["bodyHash"] != AuthorizationStore.CommandHash(body, row["reason"].AsString) || receipt is null ||
            receipt["bodyHash"] != row["bodyHash"] || receipt["result"]["evaluationId"] != id ||
            receipt["result"]["evaluationHash"] != R.Hash(R.Stable(row))) throw R.Unavailable();
        return row;
    }
    public async Task<AnomalyEvaluation> ReadAsync(ActorContext actor, long id, CancellationToken ct = default)
    {
        await R.RequireStorageAsync(database, ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var row = await EvaluationRowAsync(tx, id, token);
            var source = await SourceAsync(actor, authority, tx, row["sourceKind"].AsString, row["sourceId"].AsInt64, Permissions.AnomalyRead, token);
            var policy = await PolicyAsync(tx, source.Scope, false, token);
            var current = row["sourceHash"] == source.Hash && row["policyHash"] == R.Hash(R.Stable(policy));
            var disposition = await Rows(R.Dispositions).Find(tx, F.Eq("evaluationId", id)).SingleOrDefaultAsync(token);
            if (row["scope"] != R.Scope(source.Scope) || disposition is not null && disposition["evaluationHash"] != R.Hash(R.Stable(row))) throw R.Unavailable();
            if (disposition is not null) {
                var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", disposition["actorId"]) & F.Eq("operation", R.ReviewOperation) & F.Eq("key", disposition["requestKey"])).SingleOrDefaultAsync(token);
                if (disposition["scope"] != row["scope"] || receipt is null || receipt["bodyHash"] != disposition["bodyHash"] ||
                    receipt["result"]["evaluationId"] != id || receipt["result"]["dispositionId"] != disposition["_id"] ||
                    receipt["result"]["dispositionHash"] != R.Hash(disposition)) throw R.Unavailable();
            }
            await TouchAsync(tx, R.Evaluations, row, token); await FenceAsync(actor, authority, tx, source, Permissions.AnomalyRead, policy, token);
            return R.Evaluation(row, current, disposition);
        }, ct);
    }
    public async Task<AnomalyEvaluation?> LatestAsync(ActorContext actor, string kind, long sourceId, CancellationToken ct = default)
    {
        await R.RequireStorageAsync(database, ct);
        var id = await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var source = await SourceAsync(actor, authority, tx, kind, sourceId, Permissions.AnomalyRead, token);
            var row = await Rows(R.Evaluations).Find(tx, F.Eq("sourceKind", kind) & F.Eq("sourceId", sourceId) & F.Eq("scope", R.Scope(source.Scope)))
                .Sort(new BsonDocument("_id", -1)).FirstOrDefaultAsync(token);
            await FenceAsync(actor, authority, tx, source, Permissions.AnomalyRead, null, token); return row?["_id"].AsInt64;
        }, ct);
        return id.HasValue ? await ReadAsync(actor, id.Value, ct) : null;
    }
    public async Task<AnomalyEvaluation> ReviewAsync(ActorContext actor, long id, string outcome, AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        if (outcome is not ("acknowledged" or "requires_action" or "not_concerning")) throw new DomainValidationException("anomaly_disposition_invalid", "Select an explicit review disposition");
        await R.RequireStorageAsync(database, ct); Source? source = null; BsonDocument? row = null; BsonDocument? policy = null;
        var body = new { id, outcome };
        await authorization.ExecuteCommandAsync(actor, R.ReviewOperation, metadata, body,
            async (authority, tx, token) => {
                row = await EvaluationRowAsync(tx, id, token);
                source = await SourceAsync(actor, authority, tx, row["sourceKind"].AsString, row["sourceId"].AsInt64, Permissions.AnomalyReview, token);
                policy = await PolicyAsync(tx, source.Scope, false, token);
                if (row["scope"] != R.Scope(source.Scope) || row["sourceHash"] != source.Hash || row["policyHash"] != R.Hash(R.Stable(policy))) throw R.Changed();
                return new(ScopePolicy.Authorize(actor, authority, Permissions.AnomalyReview, source.Scope, clock.UtcNow),
                    ScopePolicy.RequiredNodes(source.Scope, authority.Directory), Permissions.AnomalyReview, new(source.Scope));
            }, async (tx, token) => {
                if (await Rows(R.Dispositions).Find(tx, F.Eq("evaluationId", id)).AnyAsync(token))
                    throw new PersistenceConflictException("anomaly_already_reviewed", "This evaluation already has a human disposition; refresh its record");
                var disposition = Metadata((await ids.NextAsync(R.Dispositions, token)).Value, actor, metadata, body, source!.Scope);
                disposition.Add("evaluationId", id); disposition.Add("evaluationHash", R.Hash(R.Stable(row!))); disposition.Add("outcome", outcome);
                await Rows(R.Dispositions).InsertOneAsync(tx, disposition, cancellationToken: token);
                return new() { { "evaluationId", id }, { "dispositionId", disposition["_id"] }, { "dispositionHash", R.Hash(disposition) } };
            }, ct, complete: async (authority, tx, token) => {
                await TouchAsync(tx, R.Evaluations, row!, token);
                await FenceAsync(actor, authority, tx, source!, Permissions.AnomalyReview, policy, token);
            });
        return await ReadAsync(actor, id, ct);
    }
}
