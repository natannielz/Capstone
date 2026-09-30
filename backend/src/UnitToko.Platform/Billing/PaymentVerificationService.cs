using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Platform.Billing;

public sealed record PaymentVerificationPreview(string Binding, BsonDocument Declaration, BsonDocument Receipt, BsonDocument Account,
    BsonDocument Review, BsonDocument Policy, ImmutableArray<PaymentDiscrepancy> Discrepancies);

public sealed class PaymentVerificationService
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly IdAllocator ids;
    private readonly AuthorizationStore authorization;
    private readonly PaymentDraftService declarations;
    private readonly PaymentEvidenceService evidence;
    private readonly BankReceiptService receipts;
    private readonly PaymentVerificationRecords records;
    public PaymentVerificationService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        PaymentDraftService declarations, PaymentEvidenceService evidence, BankReceiptService receipts) {
        foreach (var owner in new[] { declarations.VerificationDatabase, evidence.VerificationDatabase, receipts.PaymentDatabase })
            if (!ReferenceEquals(owner.Client, database.Client) || !owner.DatabaseNamespace.Equals(database.DatabaseNamespace)) throw new ArgumentException("Payment verification owners must share one transaction");
        this.database = database; this.clock = clock; this.ids = ids; this.declarations = declarations; this.evidence = evidence; this.receipts = receipts;
        authorization = new(database, unit, clock); records = new(database);
    }
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed record Context(ActorContext Actor, AuthorizationSnapshot Authority, IClientSessionHandle Tx, bool Verify, BsonDocument? Source = null);
    private static string[] Required(bool verify) => verify ? [Permissions.PaymentReviewRead, Permissions.InvoiceRead, Permissions.PaymentVerify] : [Permissions.PaymentReviewRead, Permissions.InvoiceRead];
    private AuthorizationQuery[] Require(Context context) => Required(context.Verify).Select(permission => {
        var query = ScopePolicy.Compile(context.Actor, context.Authority, permission, clock.UtcNow); AuthorizationStore.Require(query);
        if (context.Source is { } source) foreach (var resource in PaymentDraftBson.Scopes(source["scopes"].AsBsonArray))
            if (!ScopePolicy.IsResourceActive(resource, context.Authority.Directory) || !query.Matches(resource)) throw B.Broken();
        return query;
    }).ToArray();
    private async Task StorageAsync(CancellationToken ct) {
        await declarations.VerificationStorageAsync(ct); await receipts.PaymentStorageAsync(ct); await PaymentVerificationStorage.RequireAsync(database, ct);
    }
    private async Task<Context> SourceAsync(Context context, long id, CancellationToken ct) {
        _ = Require(context); context = context with { Source = await declarations.VerificationSourceAsync(context.Actor, context.Authority, context.Tx, id, ct) };
        _ = Require(context); return context;
    }
    private async Task FenceAsync(Context context, CancellationToken ct) {
        var queries = Require(context); var resources = context.Source is null ? [] : PaymentDraftBson.Scopes(context.Source["scopes"].AsBsonArray);
        var clauses = queries.SelectMany(query => query.Clauses.Where(clause => resources.Length == 0 || resources.Any(clause.Matches))).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(resource => ScopePolicy.RequiredNodes(resource, context.Authority.Directory))).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        if (context.Actor.Session is { } session) {
            var live = await records.Rows("identitySessions").Find(context.Tx, SessionPersistence.LiveFilter(session, context.Actor.UserId,
                context.Actor.ProviderIssuer, context.Actor.ProviderSubject, context.Actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct);
            if (live is null || live["idleExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime || live["absoluteExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime) throw new AuthorizationDeniedException("stale_or_inactive_session");
        }
        _ = Require(context); ct.ThrowIfCancellationRequested();
    }
    private async Task<PersonAuthorityProof> PeopleAsync(Context context, CancellationToken ct) {
        var maker = context.Source!["recordedBy"].AsInt64;
        if (maker != context.Source["uploaderId"].AsInt64 || maker == context.Actor.UserId) throw new AuthorizationDeniedException("independent_person_required");
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [maker, context.Actor.UserId], ct);
        if (!PersonIndependencePolicy.AreIndependent(proof.Evidence.Single(p => p.UserId == context.Actor.UserId), proof.Evidence.Single(p => p.UserId == maker))) throw new AuthorizationDeniedException("independent_person_required");
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct); return proof;
    }
    private async Task<BsonDocument> PolicyAsync(Context context, BankReceiptPaymentSource bank, CancellationToken ct) {
        PaymentVerificationRecords.SameParty(context.Source!, bank);
        var raw = await records.Rows(B.Policies).Find(context.Tx, new BsonDocument("scope", bank.Receipt["scope"])).SingleOrDefaultAsync(ct) ?? throw B.Changed("payment_verification_policy_unavailable");
        var row = await records.RowAsync(context.Tx, B.Policies, raw["_id"].AsInt64, ct); PaymentVerificationRecords.PolicyAt(row, Now);
        await records.TouchAsync(context.Tx, B.Policies, row, ct); return row;
    }
    private async Task<BsonDocument> SavedAsync(Context context, BsonDocument saved, CancellationToken ct) {
        var row = await records.RawAsync(context.Tx, saved["_id"].AsInt64, ct);
        PaymentVerificationRecords.Equal(row["source"], context.Source!);
        var bank = await receipts.PaymentSourceAsync(context.Actor, context.Authority, context.Tx, row["receiptId"].AsInt64, ct);
        PaymentVerificationRecords.Equal(row["receiptHash"], bank.Receipt["snapshotHash"]);
        var binding = PaymentCorrectionBson.Binding(row["review"]["binding"].AsBsonDocument);
        var extraction = await evidence.AcceptedVerificationExtractionAsync(context.Tx, context.Actor, row["declarationId"].AsInt64, binding, ct);
        var review = await declarations.VerificationReviewAsync(context.Tx, extraction, context.Source!["revision"].AsInt64, ct);
        PaymentVerificationRecords.Equal(row["review"], review);
        PaymentVerificationRecords.Facts(context.Source, bank, review);
        PaymentVerificationPolicy.RequireResolutions(PaymentVerificationRecords.Discrepancies(context.Source, bank, review), B.Input(row));
        PaymentVerificationRecords.Equal(row["binding"], B.Binding(context.Source, bank, review, row["policy"].AsBsonDocument));
        PaymentVerificationRecords.Equal(row["policy"]["scope"], bank.Receipt["scope"]);
        var party = await records.PartyAsync(context.Tx, bank, context.Source, ct) ?? throw B.Broken();
        if (party["firstVerificationId"] != row["_id"]) await PartyHistoryAsync(context, party, ct);
        await records.TouchAsync(context.Tx, B.Parties, party, ct); await records.TouchAsync(context.Tx, B.Decisions, row, ct); return row;
    }
    private async Task PartyHistoryAsync(Context context, BsonDocument party, CancellationToken ct) {
        var first = await records.RawAsync(context.Tx, party["firstVerificationId"].AsInt64, ct);
        var original = await SourceAsync(context with { Verify = false }, first["declarationId"].AsInt64, ct);
        // Validate original payer authority through its actual submitted evidence and reviewed source.
        // Skip this shared anchor's second touch to preserve the caller's conditional write snapshot.
        var bank = await receipts.PaymentSourceAsync(context.Actor, context.Authority, context.Tx, first["receiptId"].AsInt64, ct);
        PaymentVerificationRecords.Equal(first["source"], original.Source!); PaymentVerificationRecords.Equal(first["receiptHash"], bank.Receipt["snapshotHash"]);
        var binding = PaymentCorrectionBson.Binding(first["review"]["binding"].AsBsonDocument);
        var extraction = await evidence.AcceptedVerificationExtractionAsync(context.Tx, context.Actor, first["declarationId"].AsInt64, binding, ct);
        var review = await declarations.VerificationReviewAsync(context.Tx, extraction, original.Source!["revision"].AsInt64, ct);
        PaymentVerificationRecords.Equal(first["review"], review); PaymentVerificationRecords.Facts(original.Source, bank, review);
        PaymentVerificationRecords.Equal(first["binding"], B.Binding(original.Source, bank, review, first["policy"].AsBsonDocument));
        PaymentVerificationPolicy.RequireResolutions(PaymentVerificationRecords.Discrepancies(original.Source, bank, review), B.Input(first));
        await FenceAsync(original, ct);
    }
    private async Task AuditReadAsync(Context context, string action, long? id, CancellationToken ct) => await records.Rows("auditEvents").InsertOneAsync(context.Tx, new BsonDocument {
        { "_id", (await ids.NextAsync("auditEvents", ct)).Value }, { "schemaVersion", 1 }, { "kind", B.Prefix + "read" }, { "operation", B.Prefix + action },
        { "actorId", context.Actor.UserId }, { "actorGrantVersion", context.Actor.GrantVersion }, { "declarationId", id is { } value ? new BsonInt64(value) : BsonNull.Value }, { "createdAt", Now.UtcDateTime }
    }, cancellationToken: ct);
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (!ReceivingAccountPolicy.Text(metadata.Reason, 1000) || !ReceivingAccountPolicy.Text(metadata.CorrelationId, 128) || metadata.IdempotencyKey.Length > 128 ||
            metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw PaymentVerificationPolicy.Invalid();
    }
    private async Task<BsonDocument> InitialSourceAsync(ActorContext actor, long id, CancellationToken ct) => await authorization.ReadAsync(actor, async (authority, tx, token) => {
        var context = await SourceAsync(new(actor, authority, tx, true), id, token); await PeopleAsync(context, token); await FenceAsync(context, token); return context.Source!;
    }, ct);
    public async Task<PaymentVerificationPreview> PreviewAsync(ActorContext actor, long id, long receiptId, CancellationToken ct = default) {
        if (id <= 0 || receiptId <= 0) throw PaymentVerificationPolicy.Invalid(); await StorageAsync(ct);
        var initial = await InitialSourceAsync(actor, id, ct);
        return await evidence.WithVerificationOriginalAsync<PaymentVerificationPreview>(actor, id, initial["revision"].AsInt64, initial["reservationId"].AsInt64,
            PaymentSubmissionBson.Reference(initial["evidence"].AsBsonDocument), (consume, token) => authorization.ReadAsync(actor, async (authority, tx, inner) => {
                var context = await SourceAsync(new(actor, authority, tx, true), id, inner); await records.RequireUnverifiedAsync(tx, id, inner);
                var bank = await receipts.PaymentSourceAsync(actor, authority, tx, receiptId, inner); var policy = await PolicyAsync(context, bank, inner);
                var proof = await PeopleAsync(context, inner);
                return await consume(tx, async (extraction, eligible, final) => {
                    var review = await declarations.VerificationReviewAsync(tx, extraction, context.Source!["revision"].AsInt64, final);
                    PaymentVerificationRecords.Facts(context.Source, bank, review); var party = await records.PartyAsync(tx, bank, context.Source, final);
                    if (party is not null) { await PartyHistoryAsync(context, party, final); await records.TouchAsync(tx, B.Parties, party, final); }
                    await AuditReadAsync(context, "preview", id, final); await FenceAsync(context, final);
                    PaymentVerificationRecords.PolicyAt(policy, Now); proof.RequireCurrent(tx, clock.UtcNow); eligible();
                    return new PaymentVerificationPreview(B.Binding(context.Source, bank, review, policy), context.Source, bank.Receipt, bank.Account,
                        review, B.Snapshot(policy), PaymentVerificationRecords.Discrepancies(context.Source, bank, review));
                }, inner);
            }, token), ct);
    }
    public async Task<BsonDocument> VerifyAsync(ActorContext actor, long id, long receiptId, PaymentVerificationInput input, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (id <= 0 || receiptId <= 0) throw PaymentVerificationPolicy.Invalid(); input = PaymentVerificationPolicy.Copy(input); Metadata(metadata); await StorageAsync(ct);
        var body = B.Body(id, receiptId, input); var bodyHash = AuthorizationStore.CommandHash(body, metadata.Reason);
        var previous = await RecoverCoreAsync(actor, metadata.IdempotencyKey, bodyHash, ct); if (previous is not null) return B.Result(previous);
        var initial = await InitialSourceAsync(actor, id, ct); var decisionId = (await ids.NextAsync(B.Decisions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        return await evidence.WithVerificationOriginalAsync<BsonDocument>(actor, id, initial["revision"].AsInt64, initial["reservationId"].AsInt64,
            PaymentSubmissionBson.Reference(initial["evidence"].AsBsonDocument), (consume, token) => {
                Context? context = null; BankReceiptPaymentSource? bank = null; BsonDocument? policy = null; PersonAuthorityProof? people = null; Action? eligible = null; bool fresh = false;
                return authorization.ExecuteCommandAsync(actor, B.Operation, metadata, body, async (authority, tx, inner) => {
                    eligible = null; people = null; policy = null; fresh = false;
                    var saved = await records.ForKeyAsync(tx, actor.UserId, metadata.IdempotencyKey, inner);
                    context = await SourceAsync(new(actor, authority, tx, true), saved?["declarationId"].AsInt64 ?? id, inner);
                    if (saved is null) {
                        bank = await receipts.PaymentSourceAsync(actor, authority, tx, receiptId, inner); policy = await PolicyAsync(context, bank, inner); fresh = true;
                    }
                    var resources = PaymentDraftBson.Scopes(context.Source!["scopes"].AsBsonArray); var queries = Require(context); var revision = context.Source["revision"].AsInt64;
                    return new(new(true, "allowed", queries.SelectMany(query => query.Clauses.Where(clause => resources.Any(clause.Matches)).Select(clause => clause.GrantId)).Distinct().ToImmutableArray()),
                        [.. resources.SelectMany(resource => ScopePolicy.RequiredNodes(resource, authority.Directory)).Distinct()], Permissions.PaymentVerify,
                        new(resources[0], new(revision, revision, RecordedBy: context.Source["recordedBy"].AsInt64)));
                }, (tx, inner) => consume(tx, async (extraction, check, final) => {
                    eligible = check; var active = context ?? throw B.Broken(); var source = active.Source!; var actual = bank ?? throw B.Broken(); var approved = policy ?? throw B.Broken();
                    await records.RequireUnverifiedAsync(tx, id, final);
                    var review = await declarations.VerificationReviewAsync(tx, extraction, source["revision"].AsInt64, final);
                    if (B.Binding(source, actual, review, approved) != input.Binding) throw B.Changed();
                    PaymentVerificationRecords.Facts(source, actual, review);
                    PaymentVerificationPolicy.RequireResolutions(PaymentVerificationRecords.Discrepancies(source, actual, review), input);
                    people = await PeopleAsync(active, final); var party = await records.PartyAsync(tx, actual, source, final);
                    if (party is not null) await PartyHistoryAsync(active, party, final); var at = Now;
                    var row = new BsonDocument { { "_id", decisionId }, { "schemaVersion", 1 }, { "declarationId", id }, { "receiptId", receiptId }, { "receiptHash", actual.Receipt["snapshotHash"] },
                        { "source", source }, { "review", review }, { "policy", B.Snapshot(approved) }, { "binding", input.Binding }, { "payerVerificationReference", input.PayerVerificationReference },
                        { "imageReviewed", true }, { "bankReviewed", true }, { "payerVerified", true }, { "resolutions", B.Resolutions(input) },
                        { "people", new BsonArray(people.Evidence.OrderBy(p => p.UserId).Select(ReceivingAccountBson.Person)) }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
                        { "operation", B.Operation }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", bodyHash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId },
                        { "auditId", auditId }, { "recordedAt", at.UtcDateTime }, { "fence", 0L } }; row.Add("snapshotHash", B.Hash(row));
                    await records.Rows(B.Decisions).InsertOneAsync(tx, row, cancellationToken: final);
                    if (party is null) {
                        party = new() { { "_id", receiptId }, { "schemaVersion", 1 }, { "party", source["party"] }, { "firstVerificationId", decisionId },
                            { "receiptHash", actual.Receipt["snapshotHash"] }, { "recordedAt", at.UtcDateTime }, { "fence", 0L } }; party.Add("snapshotHash", B.Hash(party));
                        await records.Rows(B.Parties).InsertOneAsync(tx, party, cancellationToken: final);
                    } else await records.TouchAsync(tx, B.Parties, party, final);
                    await records.Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: final);
                    await records.Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", B.Operation }, { "sourceKey", B.SourceKey(row) },
                        { "actorId", actor.UserId }, { "requestKey", metadata.IdempotencyKey }, { "payload", B.Result(row) }, { "createdAt", at.UtcDateTime }, { "dueAt", at.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: final);
                    return B.Result(row);
                }, inner), token, async (authority, tx, inner) => {
                    var active = context ?? throw B.Broken(); var saved = await records.ForKeyAsync(tx, actor.UserId, metadata.IdempotencyKey, inner) ?? throw B.Broken();
                    _ = await SavedAsync(active, saved, inner); people = await PeopleAsync(active, inner);
                    if (fresh) { var current = await PolicyAsync(active, bank!, inner); if (B.Hash(current) != B.Hash(policy!)) throw B.Changed(); }
                    await FenceAsync(active, inner);
                }, validateCurrent: () => {
                    _ = Require(context ?? throw B.Broken()); if (fresh) PaymentVerificationRecords.PolicyAt(policy!, Now);
                    people?.RequireCurrent(context!.Tx, clock.UtcNow); eligible?.Invoke();
                });
            }, ct);
    }
    public async Task<BsonDocument?> ReadAsync(ActorContext actor, long id, CancellationToken ct = default) {
        if (id <= 0) throw PaymentVerificationPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var context = await SourceAsync(new(actor, authority, tx, false), id, token);
            var saved = await records.Rows(B.Decisions).Find(tx, new BsonDocument("declarationId", id)).SingleOrDefaultAsync(token);
            if (saved is null) await records.RequireUnverifiedAsync(tx, id, token); else saved = await SavedAsync(context, saved, token);
            await AuditReadAsync(context, "detail", id, token); await FenceAsync(context, token); return saved;
        }, ct);
    }
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor, string key, CancellationToken ct = default) {
        Metadata(new(key, "Recover payment verification", "payment-verification-recovery")); await StorageAsync(ct);
        var row = await RecoverCoreAsync(actor, key, null, ct); return row is null ? null : B.Result(row);
    }
    private Task<BsonDocument?> RecoverCoreAsync(ActorContext actor, string key, string? bodyHash, CancellationToken ct) => authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
        var context = new Context(actor, authority, tx, true); _ = Require(context);
        var saved = await records.ForKeyAsync(tx, actor.UserId, key, token); PersonAuthorityProof? proof = null;
        if (saved is not null) {
            context = await SourceAsync(context, saved["declarationId"].AsInt64, token); saved = await SavedAsync(context, saved, token); proof = await PeopleAsync(context, token);
            if (bodyHash is not null && saved["bodyHash"] != bodyHash) throw B.Changed("idempotency_key_reused");
        }
        await AuditReadAsync(context, "recovery", saved?["declarationId"].AsInt64, token); await FenceAsync(context, token); proof?.RequireCurrent(tx, clock.UtcNow); return saved;
    }, ct);
}
