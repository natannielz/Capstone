using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using B = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementSupplierPayableService
{
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ProcurementSupplierPayableInput input, string permission)
    {
        internal readonly ActorContext Actor = actor; internal readonly AuthorizationSnapshot Authority = authority;
        internal readonly IClientSessionHandle Tx = tx; internal readonly long Transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        internal readonly ProcurementSupplierPayableInput Input = input; internal readonly string Permission = permission;
        internal ProcurementSupplierPayableSource Source = null!; internal BsonDocument Policy = null!, Accounting = null!;
        internal BsonDocument? Proposal; internal PersonAuthorityProof? People;
        internal ActorContext? MakerActor; internal AuthorizationSnapshot? MakerAuthority;
        internal DateTimeOffset? SessionExpiry; internal readonly List<(string Table, BsonDocument Row)> Fences = [];
    }
    private static void Binding(string value)
    {
        if (value is not { Length: 64 } || value.Any(c => !char.IsAsciiHexDigitLower(c))) throw B.Invalid();
    }
    private static void Key(string value)
    {
        ProcurementRequestRules.Text(value, 128);
        if (value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw B.Invalid();
    }
    private AuthorizationQuery[] Queries(Context context, bool posting)
    {
        var permissions = new List<string> { Permissions.ProcurementRead, context.Permission };
        if (posting) permissions.Add(Permissions.JournalPost);
        var resource = Resource(context.Input.Location);
        return permissions.Distinct().Select(permission => {
            var query = ScopePolicy.Compile(context.Actor, context.Authority, permission, Now);
            AuthorizationStore.Require(query);
            if (!ScopePolicy.IsResourceActive(resource, context.Authority.Directory) || !query.Matches(resource))
                throw new AuthorizationDeniedException("scope_denied");
            return query;
        }).ToArray();
    }
    private void Current(Context context, bool posting = false)
    {
        _ = Queries(context, posting);
        if (!context.Tx.IsInTransaction || !ReferenceEquals(context.Tx.Client, database.Client) ||
            context.Tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != context.Transaction) throw B.Broken();
        context.People?.RequireCurrent(context.Tx, Now);
        if (context.MakerActor is { } maker && context.MakerAuthority is { } makerAuthority) {
            var query = ScopePolicy.CompileParticipantGrants(maker, makerAuthority, Permissions.ProcurementPayablePropose, Now);
            if (!query.Matches(Resource(context.Input.Location))) throw new AuthorizationDeniedException("original_maker_scope_changed");
        }
        if (context.Policy is not null && Now.UtcDateTime >= context.Policy["expiresAt"].ToUniversalTime())
            throw B.Changed("procurement_supplier_payable_policy_unavailable");
        if (context.SessionExpiry is { } expiry && Now >= expiry) throw new AuthorizationDeniedException("stale_or_inactive_session");
    }
    private async Task PeopleAsync(Context context, BsonDocument? proposal, CancellationToken ct)
    {
        var users = new[] { context.Actor.UserId, proposal?["actorId"].AsInt64 ?? context.Actor.UserId,
            context.Policy["proposedBy"].AsInt64, context.Policy["approvedBy"].AsInt64 }.Distinct().Order().ToArray();
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [.. users], ct);
        if (proof.Evidence.Any(person => person.Verification != PersonVerification.Verified || person.PersonId <= 0 || person.Version <= 0))
            throw new AuthorizationDeniedException("person_evidence_invalid");
        var byUser = proof.Evidence.ToDictionary(person => person.UserId);
        foreach (var original in context.Policy["people"].AsBsonArray.Select(R.Person))
            if (byUser[original.UserId].PersonId != original.PersonId) throw new AuthorizationDeniedException("procurement_supplier_payable_person_changed");
        if (proposal is not null)
        {
            var original = proposal["people"].AsBsonArray.Select(R.Person).Single(person => person.UserId == proposal["actorId"].AsInt64);
            if (byUser[original.UserId].PersonId != original.PersonId ||
                !PersonIndependencePolicy.AreIndependent(byUser[context.Actor.UserId], byUser[original.UserId]))
                throw new AuthorizationDeniedException("independent_person_required");
        }
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct); context.People = proof;
    }
    private async Task<DateTimeOffset?> SessionAsync(ActorContext actor, IClientSessionHandle tx, CancellationToken ct)
    {
        if (actor.Session is not { } session) return null;
        var row = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId,
            actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, Now)).SingleOrDefaultAsync(ct)
            ?? throw new AuthorizationDeniedException("stale_or_inactive_session");
        return new(new[] { row["idleExpiresAt"].ToUniversalTime(), row["absoluteExpiresAt"].ToUniversalTime() }.Min());
    }
    private void AddFence(Context context, string table, BsonDocument row)
    {
        if (context.Fences.Any(value => value.Table == table && value.Row["_id"] == row["_id"])) return;
        context.Fences.Add((table, row));
    }
    private async Task FenceAsync(Context context, CancellationToken ct, bool posting = false)
    {
        Current(context, posting);
        if (context.Source.Valuation is { } valuation) await FenceImmutableAsync(context.Tx, ProcurementStockValuationBson.Allocations, valuation, ct);
        if (context.Source.Settlement is { } settlement) await FenceImmutableAsync(context.Tx, ProcurementSettlementBson.Events, settlement, ct);
        if (context.Source.Payable is { } payable) await FenceImmutableAsync(context.Tx, B.Payables, payable, ct);
        foreach (var (table, row) in context.Fences)
        {
            if (row["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(table).UpdateOneAsync(context.Tx, row,
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
            row["fence"] = row["fence"].AsInt64 + 1;
        }
        var resource = Resource(context.Input.Location); var queries = Queries(context, posting);
        var clauses = queries.SelectMany(query => query.Clauses.Where(clause => clause.Matches(resource))).Distinct().ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            ScopePolicy.RequiredNodes(resource, context.Authority.Directory).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        if (context.People is { } proof) await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct);
        context.SessionExpiry = await SessionAsync(context.Actor, context.Tx, ct); Current(context, posting);
    }
    private async Task FenceImmutableAsync(IClientSessionHandle tx, string table, BsonDocument row, CancellationToken ct)
    {
        if ((await Rows(table).DeleteOneAsync(tx, row, cancellationToken: ct)).DeletedCount != 1) throw B.Changed();
        await Rows(table).InsertOneAsync(tx, row, cancellationToken: ct);
    }
    private async Task<Context> PrepareAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ProcurementSupplierPayableInput input, string permission, CancellationToken ct, BsonDocument? proposal = null,
        bool posting = false, HistoricalProof? proof = null)
    {
        var context = new Context(actor, authority, tx, input, permission); _ = Queries(context, posting);
        context.Source = await SourceAsync(tx, input, ct, proof); context.Policy = await PolicyAsync(tx, input, ct);
        context.Accounting = await AccountingAsync(tx, input, context.Policy, ct);
        SourceAccounting(context.Source.Facts, context.Accounting);
        AddFence(context, B.Policies, context.Policy);
        if (context.Source.Capacity.TryGetValue("payableHead", out var payableHead) && payableHead.IsBsonDocument)
            AddFence(context, B.PayableHeads, payableHead.AsBsonDocument);
        if (context.Source.Capacity.TryGetValue("advanceHead", out var advanceHead) && advanceHead.IsBsonDocument)
            AddFence(context, B.AdvanceHeads, advanceHead.AsBsonDocument);
        context.Proposal = proposal; await PeopleAsync(context, proposal, ct);
        if (proposal is not null) await OriginalMakerAsync(context, proposal, ct);
        Current(context, posting); return context;
    }
    private async Task OriginalMakerAsync(Context context, BsonDocument proposal, CancellationToken ct)
    {
        var id = proposal["actorId"].AsInt64;
        var identity = await Rows("identities").Find(context.Tx, F.Eq("_id", id)).SingleOrDefaultAsync(ct);
        if (identity is null || identity.GetValue("status", BsonNull.Value) != "active" ||
            identity.GetValue("grantVersion", BsonNull.Value) != proposal["actorGrantVersion"] ||
            !identity.GetValue("issuer", BsonNull.Value).IsString || !identity.GetValue("subject", BsonNull.Value).IsString)
            throw new AuthorizationDeniedException("original_maker_authority_changed");
        var maker = new ActorContext(id, identity["issuer"].AsString, identity["subject"].AsString,
            identity["grantVersion"].AsInt64, new DateTimeOffset(proposal["recordedAt"].ToUniversalTime()), FactorAssurance.Unknown);
        var snapshot = await authorization.LoadAsync(maker, context.Tx, ct);
        var resource = Resource(context.Input.Location);
        var query = ScopePolicy.CompileParticipantGrants(maker, snapshot, Permissions.ProcurementPayablePropose, Now);
        if (!ScopePolicy.IsResourceActive(resource, snapshot.Directory) || !query.Matches(resource))
            throw new AuthorizationDeniedException("original_maker_scope_changed");
        var clauses = query.Clauses.Where(clause => clause.Matches(resource)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(maker,
            ScopePolicy.RequiredNodes(resource, snapshot.Directory).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        context.MakerActor = maker; context.MakerAuthority = snapshot;
    }
    private CommandAuthorization Command(Context context, BsonDocument? proposal = null)
    {
        var resource = Resource(context.Input.Location);
        var approval = proposal is null ? null : new ApprovalRequest(1, 1, RequestedBy: proposal["actorId"].AsInt64);
        return new(ScopePolicy.Authorize(context.Actor, context.Authority, context.Permission, resource, Now, approval),
            ScopePolicy.RequiredNodes(resource, context.Authority.Directory), context.Permission, new(resource, approval));
    }
    private static BsonDocument PolicyView(BsonDocument row) => new() { { "id", row["_id"] }, { "revision", row["revision"] },
        { "active", row["active"] }, { "location", row["location"] }, { "currency", row["currency"] },
        { "approvedAt", row["approvedAt"] }, { "expiresAt", row["expiresAt"] },
        { "sourceTreatment", row["sourceTreatment"] }, { "applicationTreatment", row["applicationTreatment"] },
        { "bookId", row["bookId"] }, { "chartVersionId", row["chartVersionId"] },
        { "receiptAccrual", AccountView(row, "receiptAccrual") }, { "supplierPayable", AccountView(row, "supplierPayable") },
        { "supplierAdvance", AccountView(row, "supplierAdvance") }, { "operatingOwnerReference", row["operatingOwnerReference"] },
        { "approvalReference", row["approvalReference"] } };
    private static BsonDocument AccountView(BsonDocument row, string role) => new() {
        { "mappingKey", row[role + "Mapping"] }, { "accountId", row[role + "AccountId"] }, { "accountCode", row[role + "AccountCode"] } };
    private static BsonDocument ProposalView(BsonDocument row) => new() { { "id", row["_id"] }, { "version", 1L },
        { "input", row["input"] }, { "source", row["source"] }, { "capacity", PublicCapacity(row["capacity"].AsBsonDocument) },
        { "policy", PolicyView(row["policy"].AsBsonDocument) }, { "accounting", row["accounting"] },
        { "amount", row["amount"] }, { "recordedAt", row["recordedAt"] }, { "reason", row["reason"] } };
    private static BsonDocument PublicCapacity(BsonDocument row) => row.Contains("payable") ? new() {
        { "payable", row["payable"] }, { "advance", row["advance"] } } : row.DeepClone().AsBsonDocument;
}
