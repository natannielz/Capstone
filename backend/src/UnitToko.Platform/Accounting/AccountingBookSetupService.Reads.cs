using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using B = UnitToko.Platform.Accounting.AccountingBookSetupBson;

namespace UnitToko.Platform.Accounting;

public sealed partial class AccountingBookSetupService
{
    private async Task FenceQueryAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string permission, CancellationToken ct) {
        var query = Query(actor, authority, permission); var clauses = query.Clauses;
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        var deadline = await LiveSessionAsync(actor, tx, ct); _ = Query(actor, authority, permission);
        if (deadline is { } expires && clock.UtcNow >= expires) throw new AuthorizationDeniedException("stale_or_inactive_session"); ct.ThrowIfCancellationRequested();
    }
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var query = Query(actor, authority, Permissions.AccountingBookRead);
            var keys = query.Clauses.Where(clause => clause.Scope.Kind == GrantScopeKind.FulfillmentShop)
                .Select(clause => new OperationalBookKey(clause.Scope.OrganizationId!.Value, clause.Scope.ShopId!.Value, clause.Scope.ChannelId!.Value, "IDR"))
                .Distinct().OrderBy(key => key.OrganizationId).ThenBy(key => key.ShopId).ThenBy(key => key.ChannelId).ToArray();
            if (keys.Length > 256) throw B.Changed(); var contexts = new List<Context>(); var items = new BsonArray();
            foreach (var key in keys) {
                var context = await ContextAsync(actor, authority, tx, key, Permissions.AccountingBookRead, token); await PeopleAsync(context, null, null, token);
                var item = new BsonDocument { { "key", B.Key(key) }, { "configured", await Rows("accountingBooks").Find(tx, new BsonDocument("canonicalKey", key.CanonicalKey)).AnyAsync(token) } };
                foreach (var name in B.SourceNames) item.Add(name, B.Label(context.Sources[name])); items.Add(item); contexts.Add(context);
            }
            foreach (var context in contexts) await FenceAsync(context, token);
            await FenceQueryAsync(actor, authority, tx, Permissions.AccountingBookRead, token); foreach (var context in contexts) Current(context);
            token.ThrowIfCancellationRequested(); return new BsonDocument("items", items);
        }, ct);
    }
    private async Task<BsonDocument> ProposalViewAsync(Context context, BsonDocument proposal, CancellationToken ct) {
        var decision = await records.DecisionForAsync(context.Tx, proposal, ct); BsonValue view = BsonNull.Value;
        if (decision is not null) {
            view = new BsonDocument { { "id", decision["_id"] }, { "decision", decision["decision"] }, { "decidedAt", decision["recordedAt"] },
                { "bookId", decision["bookId"] }, { "chartId", decision["chartId"] }, { "periodId", decision["periodId"] }, { "accounts", decision["accounts"] } };
            if (decision["bookId"].IsInt64) context.BookId = decision["bookId"].AsInt64;
        }
        var result = new BsonDocument { { "id", proposal["_id"] }, { "version", 1L }, { "key", proposal["key"] },
            { "initialMonth", proposal["input"]["initialMonth"] }, { "mappings", proposal["input"]["mappings"] }, { "proposedAt", proposal["recordedAt"] },
            { "state", decision is null ? "pending" : decision["decision"] == "approve" ? "approved" : "rejected" }, { "decision", view } };
        foreach (var name in B.SourceNames) result.Add(name, B.Label(proposal["sources"][name])); return result;
    }
    public async Task<BsonDocument> CurrentAsync(ActorContext actor, OperationalBookKey key, CancellationToken ct = default) {
        AccountingBookSetupRules.Key(key); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, key, Permissions.AccountingBookRead, token);
            await records.HistoryAsync(tx, key, token); var proposal = await records.ApprovedAsync(tx, key, token);
            var configured = await Rows("accountingBooks").Find(tx, new BsonDocument("canonicalKey", key.CanonicalKey)).AnyAsync(token);
            if (configured && proposal is null) throw B.Configured(); if (!configured && proposal is not null) throw B.Broken();
            BsonValue setup = BsonNull.Value;
            if (proposal is not null) {
                var decision = await records.DecisionForAsync(tx, proposal, token) ?? throw B.Broken(); BindPeople(context, proposal, decision);
                await PeopleAsync(context, decision["actorId"].AsInt64, proposal["actorId"].AsInt64, token); setup = await ProposalViewAsync(context, proposal, token);
            } else await PeopleAsync(context, null, null, token);
            await FenceAsync(context, token); return new BsonDocument { { "key", B.Key(key) }, { "configured", configured }, { "setup", setup } };
        }, ct);
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, long proposalId, CancellationToken ct = default) {
        if (proposalId <= 0) throw AccountingBookSetupRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, Permissions.AccountingBookRead, token); var proposal = context.Proposal!;
            var decision = await records.DecisionForAsync(tx, proposal, token); BindPeople(context, proposal, decision);
            await PeopleAsync(context, decision?["actorId"].AsInt64, decision is null ? null : proposal["actorId"].AsInt64, token);
            var result = await ProposalViewAsync(context, proposal, token); await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> ListAsync(ActorContext actor, OperationalBookKey key, long? beforeId = null, CancellationToken ct = default) {
        AccountingBookSetupRules.Key(key); if (beforeId is <= 0) throw AccountingBookSetupRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, key, Permissions.AccountingBookRead, token);
            await records.HistoryAsync(tx, key, token); var filter = new BsonDocument("key", B.Key(key)); if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(B.Proposals).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = new BsonArray(); var proofs = new List<PersonAuthorityProof>();
            foreach (var row in rows.Take(25)) {
                var proposal = await records.ProposalAsync(tx, row["_id"].AsInt64, token); var decision = await records.DecisionForAsync(tx, proposal, token);
                BindPeople(context, proposal, decision); await PeopleAsync(context, decision?["actorId"].AsInt64, decision is null ? null : proposal["actorId"].AsInt64, token);
                proofs.Add(context.People!); items.Add(await ProposalViewAsync(context, proposal, token));
            }
            if (rows.Count == 0) await PeopleAsync(context, null, null, token);
            await FenceAsync(context, token); foreach (var proof in proofs) await new PersonAuthorizationFence(database, clock).TouchAsync(tx, proof, token);
            Current(context); foreach (var proof in proofs) proof.RequireCurrent(tx, clock.UtcNow); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "key", B.Key(key) }, { "items", items }, { "nextProposalId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument?> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        var permission = B.Permission(operation); Metadata(new(key, "Recover original accounting setup operation", "accounting-book-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            _ = Query(actor, authority, permission); var row = await ForKeyAsync(actor, authority, tx, operation, key, token);
            if (row is null) { await FenceQueryAsync(actor, authority, tx, permission, token); return null; }
            var context = await ContextAsync(actor, authority, tx, B.Key(row["key"]), permission, token);
            var proposal = await records.ProposalAsync(tx, row["proposalId"].AsInt64, token); BindPeople(context, proposal, operation == B.Prefix + "propose" ? null : row);
            await PeopleAsync(context, operation == B.Prefix + "propose" ? null : actor.UserId, operation == B.Prefix + "propose" ? null : proposal["actorId"].AsInt64, token);
            if (row.GetValue("bookId", BsonNull.Value).IsInt64) context.BookId = row["bookId"].AsInt64;
            await FenceAsync(context, token); return B.Result(row);
        }, ct);
    }
}
