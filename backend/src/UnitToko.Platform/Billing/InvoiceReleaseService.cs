using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Billing.InvoiceReleaseBson;

namespace UnitToko.Platform.Billing;

public sealed partial class InvoiceReleaseService
{
    private readonly IMongoDatabase database;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly ShipmentReceiptService receipts;
    private readonly AuthorizationStore authorization;
    private readonly InvoiceReleaseRecords records;
    private readonly InvoiceReleaseSources sources;
    internal IMongoDatabase Database => database;
    public InvoiceReleaseService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, ShipmentReceiptService receipts)
    {
        if (!ReferenceEquals(database.Client, receipts.SourceDatabase.Client) || !database.DatabaseNamespace.Equals(receipts.SourceDatabase.DatabaseNamespace))
            throw new ArgumentException("Invoice release and source owners must share the same database transaction");
        this.database = database; this.ids = ids; this.clock = clock; this.receipts = receipts;
        authorization = new(database, unit, clock); records = new(database); sources = new(database, clock, receipts);
    }
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private async Task StorageAsync(CancellationToken ct) {
        await receipts.InvoiceStorageAsync(ct); await InvoiceStorage.RequireAsync(database, ct); await InvoiceReleaseRecords.RequireStorageAsync(database, ct);
    }
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata); InvoiceReleaseRules.Text(metadata.Reason);
        if (metadata.IdempotencyKey is not { Length: > 0 and <= 128 } || metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw InvoiceReleaseRules.Invalid();
    }
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string permission,
        ImmutableArray<InvoiceReceiptSource> receipts, DateTimeOffset capturedAt)
    {
        internal ActorContext Actor { get; } = actor;
        internal AuthorizationSnapshot Authority { get; } = authority;
        internal IClientSessionHandle Tx { get; } = tx;
        internal string Permission { get; } = permission;
        internal ImmutableArray<InvoiceReceiptSource> Receipts { get; } = receipts;
        internal DateTimeOffset CapturedAt { get; } = capturedAt;
        internal InvoiceReleaseHistory? History { get; set; }
        internal InvoiceReleaseCapture? Capture { get; set; }
        internal PersonAuthorityProof? People { get; set; }
        internal DateTimeOffset? SessionExpiresAt { get; set; }
    }
    private AuthorizationQuery Require(Context context) {
        var query = ScopePolicy.Compile(context.Actor, context.Authority, context.Permission, clock.UtcNow); AuthorizationStore.Require(query);
        if (context.Receipts.IsDefaultOrEmpty || context.Receipts.Any(source => !ScopePolicy.IsResourceActive(source.Resource, context.Authority.Directory) || !query.Matches(source.Resource)))
            throw new AuthorizationDeniedException("scope_denied");
        return query;
    }
    private async Task<Context> ContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ImmutableArray<InvoiceReceiptSelection> selected, string permission, CancellationToken ct)
    {
        AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, permission, clock.UtcNow)); var began = Now;
        var source = await sources.ReadAsync(actor, authority, tx, selected, Permissions.InvoiceRead, ct);
        var context = new Context(actor, authority, tx, permission, source, began); _ = Require(context);
        context.History = await records.SelectionAsync(tx, selected, ct); return context;
    }
    private async Task<Context> RecordContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        long recordId, string permission, CancellationToken ct)
    {
        AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, permission, clock.UtcNow));
        // Route scope is checked before reading this release's private history.
        var route = await records.Rows(B.Events).Find(tx, new BsonDocument("_id", recordId))
            .Project(new BsonDocument { { "input.receipts", 1 }, { "releaseId", 1 } }).SingleOrDefaultAsync(ct) ?? throw B.Changed();
        var context = await ContextAsync(actor, authority, tx, B.Selected(route["input"]["receipts"]), permission, ct);
        if (context.History is null || context.History.Head["_id"] != route["releaseId"] || !context.History.Events.Any(row => row["_id"] == recordId)) throw B.Broken();
        return context;
    }
    private async Task PeopleAsync(Context context, BsonDocument? proposal, BsonDocument? decision, CancellationToken ct)
    {
        var original = proposal is null ? null : B.Person(proposal["people"][0]);
        var originalChecker = decision is null ? null : decision["people"].AsBsonArray.Select(B.Person).Single(person => person.UserId == decision["actorId"].AsInt64);
        var users = new[] { context.Actor.UserId, original?.UserId ?? context.Actor.UserId, originalChecker?.UserId ?? context.Actor.UserId }.Distinct().Order().ToImmutableArray();
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, users, ct);
        if (proof.Evidence.Any(person => person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 })) throw new AuthorizationDeniedException("person_evidence_invalid");
        foreach (var historical in new[] { original, originalChecker }.OfType<PersonEvidence>())
            if (proof.Evidence.Single(person => person.UserId == historical.UserId).PersonId != historical.PersonId) throw new AuthorizationDeniedException("invoice_release_person_changed");
        if (context.Permission == B.ApprovePermission && original is not null &&
            !PersonIndependencePolicy.AreIndependent(proof.Evidence.Single(person => person.UserId == context.Actor.UserId),
                proof.Evidence.Single(person => person.UserId == original.UserId), original)) throw new AuthorizationDeniedException("independent_person_required");
        if (original is not null && originalChecker is not null && !PersonIndependencePolicy.AreIndependent(
            proof.Evidence.Single(person => person.UserId == originalChecker.UserId), proof.Evidence.Single(person => person.UserId == original.UserId), original))
            throw new AuthorizationDeniedException("independent_person_required");
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct); context.People = proof;
    }
    private void Current(Context context) {
        _ = Require(context); context.People?.RequireCurrent(context.Tx, clock.UtcNow);
        if (!context.Tx.IsInTransaction || !ReferenceEquals(context.Tx.Client, database.Client) || Now < context.CapturedAt) throw B.Changed();
        if (context.Capture is { } capture && (Now < capture.CapturedAt || Now < capture.Input.ValidFrom || Now >= capture.Input.ValidUntil)) throw B.Changed();
        if (context.SessionExpiresAt is { } expires && Now >= expires) throw new AuthorizationDeniedException("stale_or_inactive_session");
    }
    private async Task FenceAsync(Context context, CancellationToken ct)
    {
        Current(context);
        if (context.Capture is { } captured) {
            var fresh = await sources.CaptureAsync(context.Actor, context.Authority, context.Tx, captured.Input, Permissions.InvoiceRead, ct);
            InvoiceReleaseRecords.Equal(captured.Snapshot, fresh.Snapshot);
        } else if (context.History is { } historical) {
            foreach (var proposal in historical.Events.Where(row => row["action"] == "propose"))
                await sources.HistoricalAsync(context.Actor, context.Tx, proposal["source"].AsBsonDocument, ct);
        }
        if (context.History is { } history) await records.FenceAsync(context.Tx, history, ct);
        else if (await records.SelectionAsync(context.Tx, context.Receipts.Select(source => new InvoiceReceiptSelection(
            new(source.Snapshot["shipment"]["shipmentId"].AsInt64), new(source.Snapshot["receipt"]["id"].AsInt64))), ct) is not null) throw B.Changed();
        await receipts.InvoiceFenceAsync(context.Actor, context.Authority, context.Tx, context.Receipts.Select(source => source.Resource), Permissions.InvoiceRead, ct);
        var query = Require(context); var clauses = query.Clauses.Where(clause => context.Receipts.Any(source => clause.Matches(source.Resource))).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            context.Receipts.SelectMany(source => ScopePolicy.RequiredNodes(source.Resource, context.Authority.Directory)).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        await SessionAsync(context, ct);
        if (context.People is { } proof) await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct);
        Current(context); ct.ThrowIfCancellationRequested();
    }
    private async Task SessionAsync(Context context, CancellationToken ct) {
        if (context.Actor.Session is { } session) {
            var row = await records.Rows("identitySessions").Find(context.Tx, SessionPersistence.LiveFilter(session, context.Actor.UserId,
                context.Actor.ProviderIssuer, context.Actor.ProviderSubject, context.Actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct)
                ?? throw new AuthorizationDeniedException("stale_or_inactive_session");
            context.SessionExpiresAt = new[] { B.Time(row["idleExpiresAt"]), B.Time(row["absoluteExpiresAt"]) }.Min();
        }
    }
}
