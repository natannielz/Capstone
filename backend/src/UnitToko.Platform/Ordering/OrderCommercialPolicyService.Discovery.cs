using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Organizations;
using B = UnitToko.Platform.Ordering.OrderCommercialPolicyBson;

namespace UnitToko.Platform.Ordering;

public sealed partial class OrderCommercialPolicyService
{
    private static BsonDocument SellerLabels(BsonDocument sources) => new() {
        { "organizationName", sources["organization"]["profile"]["name"] }, { "shopName", sources["shop"]["profile"]["name"] },
        { "channelName", sources["channel"]["profile"]["name"] } };
    private static BsonDocument SellerVersions(BsonDocument sources) => B.SellerVersions(new OrderCommercialSellerVersions(
        sources["organization"]["target"]["version"].AsInt64, sources["organization"]["profile"]["version"].AsInt64,
        sources["shop"]["target"]["version"].AsInt64, sources["shop"]["profile"]["version"].AsInt64,
        sources["channel"]["target"]["version"].AsInt64, sources["channel"]["profile"]["version"].AsInt64));
    private static IEnumerable<(ScopeNode Node, ResourceScope Resource)> BuyingChoices(ActorContext actor, AuthorizationSnapshot authority)
    {
        foreach (var node in authority.Directory.Nodes.Where(node => node.Kind is ScopeNodeKind.Branch or ScopeNodeKind.Division && node.Status == AuthorizationStatus.Active)) {
            var branch = node.Kind == ScopeNodeKind.Branch ? node : node.ParentId is { } parent ? authority.Directory.Find(parent, ScopeNodeKind.Branch) : null;
            var organization = branch?.ParentId is { } owner ? authority.Directory.Find(owner, ScopeNodeKind.BuyingOrganization) : null;
            if (branch is not { Status: AuthorizationStatus.Active } || organization is not { Status: AuthorizationStatus.Active }) continue;
            yield return (node, new(BuyingOrganizationId: organization.Id, BranchId: branch.Id,
                DivisionId: node.Kind == ScopeNodeKind.Division ? node.Id : null, CreatedBy: actor.UserId));
        }
    }
    public async Task<BsonDocument> SellerChoicesAsync(ActorContext actor, long? beforeShopId = null, long? beforeChannelId = null, CancellationToken ct = default)
    {
        if (beforeShopId is <= 0 || beforeChannelId is <= 0 || beforeShopId.HasValue != beforeChannelId.HasValue) throw OrderCommercialPolicyRules.Invalid();
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var beganAt = Now; var query = Query(actor, authority, B.ReadPermission); var candidates = new HashSet<OperationalBookKey>();
            bool Before(OperationalBookKey key) => beforeShopId is null || key.ShopId < beforeShopId.Value || key.ShopId == beforeShopId.Value && key.ChannelId < beforeChannelId!.Value;
            var finance = query.Clauses.Where(clause => clause.Scope.Kind == GrantScopeKind.FulfillmentShop).ToArray();
            foreach (var shop in authority.Directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Shop && node.Status == AuthorizationStatus.Active && node.ParentId is > 0))
                foreach (var channel in authority.Directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Channel && node.Status == AuthorizationStatus.Active && node.ParentId == shop.ParentId)) {
                    var key = new OperationalBookKey(shop.ParentId!.Value, shop.Id, channel.Id, "IDR");
                    var resource = new ResourceScope(FulfillmentOrganizationId: key.OrganizationId, ShopId: key.ShopId, ChannelId: key.ChannelId);
                    if (Before(key) && ScopePolicy.IsResourceActive(resource, authority.Directory) && finance.Any(clause => clause.Matches(resource))) candidates.Add(key);
                }
            var buyerIds = BuyingChoices(actor, authority).Where(choice => query.Clauses.Any(clause => (clause.Scope.Kind is
                GrantScopeKind.BuyingBranch or GrantScopeKind.BuyingDivision or GrantScopeKind.BuyingOrganization) && clause.Matches(choice.Resource))).Select(choice => choice.Node.Id).ToArray();
            if (buyerIds.Length > 0) {
                var match = new BsonDocument { { "target.id", new BsonDocument("$in", new BsonArray(buyerIds)) },
                    { "version", new BsonDocument("$gte", 1L) }, { "versionId", new BsonDocument("$gt", 0L) } };
                if (beforeShopId is { } shop && beforeChannelId is { } channel) match.Add("$or", new BsonArray {
                    new BsonDocument("target.seller.shopId", new BsonDocument("$lt", shop)),
                    new BsonDocument { { "target.seller.shopId", shop }, { "target.seller.channelId", new BsonDocument("$lt", channel) } } });
                var known = await Rows(B.Heads).Aggregate<BsonDocument>(tx, new BsonDocument[] {
                    new("$match", match), new("$group", new BsonDocument("_id", "$target.seller")),
                    new("$sort", new BsonDocument { { "_id.shopId", -1 }, { "_id.channelId", -1 } }), new("$limit", 26)
                }).ToListAsync(token);
                foreach (var row in known) candidates.Add(AccountingBson.ReadKey(row["_id"]));
            }
            var page = candidates.OrderByDescending(key => key.ShopId).ThenByDescending(key => key.ChannelId).Take(26).ToArray();
            var items = new BsonArray(); var contexts = new List<Context>(); var sellerGuards = new List<Action>();
            foreach (var key in page.Take(25)) {
                var resource = new ResourceScope(FulfillmentOrganizationId: key.OrganizationId, ShopId: key.ShopId, ChannelId: key.ChannelId);
                if (finance.Any(clause => clause.Matches(resource))) {
                    if (!await Rows("organizationProfiles").Find(tx, new BsonDocument("_id", key.OrganizationId)).AnyAsync(token) ||
                        !await Rows("organizationProfiles").Find(tx, new BsonDocument("_id", key.ShopId)).AnyAsync(token) ||
                        !await Rows("organizationProfiles").Find(tx, new BsonDocument("_id", key.ChannelId)).AnyAsync(token)) continue;
                    var sources = new BsonDocument();
                    foreach (var (name, kind, id) in new[] { ("organization", ScopeNodeKind.FulfillmentOrganization, key.OrganizationId),
                        ("shop", ScopeNodeKind.Shop, key.ShopId), ("channel", ScopeNodeKind.Channel, key.ChannelId) }) {
                        var source = await organizations.ReadAddressProfileAsync(tx, new(kind, id), token);
                        if (source["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node).Any(node => node.Status != AuthorizationStatus.Active || authority.Directory.Find(node.Id, node.Kind) != node) ||
                            source["target"]["id"] != id || source["target"]["kind"] != B.Kind(kind) || kind != ScopeNodeKind.FulfillmentOrganization && source["target"]["parentId"] != key.OrganizationId) throw B.Changed();
                        sources.Add(name, source); await organizations.FenceAddressProfileAsync(tx, source, true, token);
                    }
                    var people = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, [actor.UserId], token);
                    if (people.Evidence.Any(person => person.Verification != PersonVerification.Verified)) throw new AuthorizationDeniedException("person_evidence_invalid");
                    await new PersonAuthorizationFence(database, clock).TouchAsync(tx, people, token);
                    var sessionEnd = await LiveSessionAsync(actor, tx, token);
                    void CurrentSeller() {
                        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || Now < beganAt ||
                            sessionEnd is { } end && Now >= end || sources.Values.Any(source => B.Date(source["profile"]["approvedAt"]) > Now) ||
                            !Query(actor, authority, B.ReadPermission).Matches(resource)) throw B.Changed();
                        people.RequireCurrent(tx, clock.UtcNow);
                    }
                    CurrentSeller(); sellerGuards.Add(CurrentSeller);
                    var item = SellerLabels(sources); item.Add("seller", AccountingBson.Key(key)); item.Add("sellerVersions", SellerVersions(sources)); items.Add(item);
                } else {
                    var route = await Rows(B.Heads).Find(tx, new BsonDocument { { "target.id", new BsonDocument("$in", new BsonArray(buyerIds)) },
                        { "target.seller", AccountingBson.Key(key) }, { "version", new BsonDocument("$gte", 1L) }, { "versionId", new BsonDocument("$gt", 0L) } })
                        .Sort(new BsonDocument("target.id", -1)).Project(new BsonDocument("target", 1)).FirstOrDefaultAsync(token) ?? throw B.Broken();
                    var context = await ContextAsync(actor, authority, tx, B.Target(route["target"]), B.ReadPermission, token);
                    var version = await VersionAsync(context, token) ?? throw B.Broken();
                    context.NotBefore = B.Date(version["effectiveAt"]); Current(context);
                    await ApprovedPeopleAsync(context, token); contexts.Add(context);
                    var item = SellerLabels(context.SellerSources); item.Add("seller", AccountingBson.Key(key)); item.Add("sellerVersions", SellerVersions(context.SellerSources)); items.Add(item);
                }
            }
            foreach (var context in contexts) await FenceAsync(context, token);
            await FenceQueryAsync(actor, authority, tx, B.ReadPermission, token);
            foreach (var context in contexts) Current(context); foreach (var guard in sellerGuards) guard();
            return new BsonDocument { { "items", items }, { "nextSeller", page.Length > 25 ? new BsonDocument {
                { "shopId", page[24].ShopId }, { "channelId", page[24].ChannelId } } : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, OperationalBookKey seller, long? beforeId = null, CancellationToken ct = default)
    {
        AccountingBookSetupRules.Key(seller); if (beforeId is <= 0) throw OrderCommercialPolicyRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var query = Query(actor, authority, B.ReadPermission);
            var sellerResource = new ResourceScope(FulfillmentOrganizationId: seller.OrganizationId, ShopId: seller.ShopId, ChannelId: seller.ChannelId);
            var finance = query.Clauses.Any(clause => clause.Scope.Kind == GrantScopeKind.FulfillmentShop && clause.Matches(sellerResource));
            var page = BuyingChoices(actor, authority).Where(choice => (!beforeId.HasValue || choice.Node.Id < beforeId) &&
                query.Matches(choice.Resource with { FulfillmentOrganizationId = seller.OrganizationId, ShopId = seller.ShopId, ChannelId = seller.ChannelId }))
                .OrderByDescending(choice => choice.Node.Id).Take(26).ToArray();
            var items = new BsonArray(); var contexts = new List<Context>();
            foreach (var choice in page.Take(25)) {
                var target = new OrderCommercialPolicyTarget(choice.Node.Kind, choice.Node.Id, seller);
                if (!finance && !await Rows(B.Heads).Find(tx, new BsonDocument { { "target", B.Target(target) },
                    { "version", new BsonDocument("$gte", 1L) }, { "versionId", new BsonDocument("$gt", 0L) } }).AnyAsync(token)) continue;
                if (!await Rows("organizationProfiles").Find(tx, new BsonDocument("_id", choice.Node.Id)).AnyAsync(token)) continue;
                var context = await ContextAsync(actor, authority, tx, target, B.ReadPermission, token);
                if (!finance) { var version = await VersionAsync(context, token) ?? throw B.Broken(); context.NotBefore = B.Date(version["effectiveAt"]); Current(context); }
                await ApprovedPeopleAsync(context, token); contexts.Add(context);
                items.Add(new BsonDocument { { "target", B.Target(target) }, { "context", ContextView(context.Source) },
                    { "code", context.Source["profile"]["code"] }, { "name", context.Source["profile"]["name"] },
                    { "seller", SellerLabels(context.SellerSources) },
                    { "expectedScopeVersion", context.Source["target"]["version"] }, { "expectedProfileVersion", context.Source["profile"]["version"] },
                    { "sellerVersions", SellerVersions(context.SellerSources) }, { "policyVersion", context.Head is null ? 0L : context.Head["version"] },
                    { "policyId", context.Head is null ? BsonNull.Value : context.Head["_id"] }, { "versionId", context.Head?["versionId"] ?? BsonNull.Value } });
            }
            foreach (var context in contexts) await FenceAsync(context, token);
            await FenceQueryAsync(actor, authority, tx, B.ReadPermission, token); foreach (var context in contexts) Current(context);
            return new BsonDocument { { "items", items }, { "nextTargetId", page.Length > 25 ? new BsonInt64(page[24].Node.Id) : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> ListProposalsAsync(ActorContext actor, OrderCommercialPolicyTarget target, long? beforeId = null, CancellationToken ct = default)
    {
        OrderCommercialPolicyRules.RequireTarget(target); if (beforeId is <= 0) throw OrderCommercialPolicyRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, target, B.ReadPermission, token);
            var filter = new BsonDocument("target", B.Target(target)); if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(B.Proposals).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = new BsonArray(); var proofs = new List<PersonAuthorityProof>();
            foreach (var row in rows.Take(25)) {
                var proposal = await records.ProposalAsync(tx, row["_id"].AsInt64, token); var decision = await records.DecisionForAsync(tx, proposal, token);
                BindPeople(context, proposal, decision); await PeopleAsync(context, decision?["actorId"].AsInt64, decision is null ? null : proposal["actorId"].AsInt64, token);
                proofs.Add(context.People!); items.Add(await ProposalViewAsync(context, proposal, token));
            }
            await ApprovedPeopleAsync(context, token); await FenceAsync(context, token);
            foreach (var proof in proofs) { await new PersonAuthorizationFence(database, clock).TouchAsync(tx, proof, token); proof.RequireCurrent(tx, clock.UtcNow); }
            Current(context); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "items", items }, { "nextProposalId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
}
