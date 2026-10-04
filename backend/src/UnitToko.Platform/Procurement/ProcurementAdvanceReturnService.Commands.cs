using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using B = UnitToko.Platform.Procurement.ProcurementAdvanceReturnBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementAdvanceReturnService
{
    private async Task<Context> PrepareAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ProcurementAdvanceReturnInput input, string permission, bool posting, BsonDocument? proposal, CancellationToken ct)
    {
        var context = new Context(actor, authority, tx, input, permission); _ = Queries(context, posting);
        context.Bank = await bankReceipts.SupplierReturnSourceAsync(actor, authority, tx, input.ReceiptId, posting, ct);
        await new PaymentReceiptPurposeRecords(database).RequireUnclaimedForSupplierAsync(tx, context.Bank.Receipt, ct);
        var bank = new BankReceiptPaymentSource(context.Bank.Receipt, context.Bank.Account, context.Bank.BankRow);
        var (source, advances) = await records.FactsAsync(tx, input, bank, context.Bank.SuspenseAccount, ct);
        context.Source = source; context.Advances = advances;
        var capacityLines = new BsonArray();
        foreach (var selected in input.Lines) {
            var advance = advances[selected.SettlementId];
            var (state, head) = await payables.ReadAdvanceCapacityAsync(tx, advance, ct, context.ReturnProofs);
            if (selected.Amount.Value > Money.Parse(state["remainingAmount"].AsString).Value)
                throw B.Changed("procurement_advance_return_capacity_exceeded");
            capacityLines.Add(new BsonDocument { { "settlementId", selected.SettlementId }, { "advance", state } });
            context.Heads.Add(selected.SettlementId, head);
        }
        context.Capacity = new("lines", capacityLines);
        context.Policy = await PolicyAsync(tx, input, ct); context.Accounting = await AccountingAsync(tx, input, context.Policy, ct);
        Equal(source["bookId"], context.Accounting["bookId"]);
        Equal(source["suspenseAccount"], context.Accounting["suspense"]);
        foreach (var line in source["lines"].AsBsonArray) Equal(line["sourceAdvanceAccount"], context.Accounting["supplierAdvance"]);
        context.Proposal = proposal; await PeopleAsync(context, proposal, ct);
        if (proposal is not null) await OriginalMakerAsync(context, proposal, ct);
        Current(context, posting); return context;
    }
    private async Task PeopleAsync(Context context, BsonDocument? proposal, CancellationToken ct)
    {
        var policy = proposal?["policy"] ?? context.Policy;
        var users = new[] { context.Actor.UserId, proposal?["actorId"].AsInt64 ?? context.Actor.UserId,
            policy["proposedBy"].AsInt64, policy["approvedBy"].AsInt64 }.Distinct().Order().ToArray();
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [.. users], ct);
        if (proof.Evidence.Any(person => person.Verification != PersonVerification.Verified || person.PersonId <= 0 || person.Version <= 0))
            throw new AuthorizationDeniedException("person_evidence_invalid");
        var byUser = proof.Evidence.ToDictionary(person => person.UserId);
        foreach (var original in policy["people"].AsBsonArray.Select(R.Person))
            if (byUser[original.UserId].PersonId != original.PersonId) throw new AuthorizationDeniedException("procurement_advance_return_person_changed");
        if (proposal is not null) {
            var original = proposal["people"].AsBsonArray.Select(R.Person).Single(person => person.UserId == proposal["actorId"].AsInt64);
            if (byUser[original.UserId].PersonId != original.PersonId ||
                !PersonIndependencePolicy.AreIndependent(byUser[context.Actor.UserId], byUser[original.UserId]))
                throw new AuthorizationDeniedException("independent_person_required");
        }
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct); context.People = proof;
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
        var query = ScopePolicy.CompileParticipantGrants(maker, snapshot, Permissions.ProcurementAdvanceReturnPropose, Now);
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
    private static BsonDocument AccountView(BsonDocument row, string role) => new() {
        { "mappingKey", row[role + "Mapping"] }, { "accountId", row[role + "AccountId"] }, { "accountCode", row[role + "AccountCode"] } };
    private static BsonDocument PolicyView(BsonDocument row) => new() { { "id", row["_id"] }, { "revision", row["revision"] },
        { "active", row["active"] }, { "location", row["location"] }, { "currency", row["currency"] },
        { "approvedAt", row["approvedAt"] }, { "expiresAt", row["expiresAt"] }, { "treatment", row["treatment"] },
        { "bookId", row["bookId"] }, { "chartVersionId", row["chartVersionId"] },
        { "suspense", AccountView(row, "suspense") }, { "supplierAdvance", AccountView(row, "supplierAdvance") },
        { "operatingOwnerReference", row["operatingOwnerReference"] }, { "approvalReference", row["approvalReference"] } };
    private static BsonDocument ProposalView(BsonDocument row) => new() { { "id", row["_id"] }, { "version", 1L },
        { "input", row["input"] }, { "source", row["source"] }, { "capacity", row["capacity"] },
        { "policy", PolicyView(row["policy"].AsBsonDocument) }, { "accounting", row["accounting"] },
        { "amount", row["amount"] }, { "recordedAt", row["recordedAt"] }, { "reason", row["reason"] } };
}
