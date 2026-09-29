using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Catalog.CatalogPriceBson;

namespace UnitToko.Platform.Catalog;

public sealed partial class CatalogPriceService
{
    public async Task<BsonDocument> ProposeAsync(ActorContext actor, long skuId, CatalogPriceExpectation expected, CatalogPriceInput input,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        Metadata(metadata); CatalogPricePolicy.Validate(input);
        if (skuId <= 0 || expected is null) throw B.Invalid("invalid_catalog_price_request");
        _ = B.Expected(B.Expected(expected)); await StorageAsync(ct);
        var body = new { skuId, expected, input }; var hash = AuthorizationStore.CommandHash(body, metadata.Reason);
        var id = (await ids.NextAsync(B.Proposals, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        return await CommandAsync(actor, B.Prefix + "propose", Permissions.PricingManage, metadata, body,
            async (snapshot, tx, token) => (await ContextAsync(actor, snapshot, tx, skuId, Permissions.PricingManage, token), null),
            async (context, token) => {
                Active(context); var now = Now();
                _ = CatalogPricePolicy.Approve(CatalogRepository.Sku(context.Sku), CatalogRepository.Definition(context.Definition),
                    context.Current, expected, input, new(context.Current?.Id.Value == 1 ? 2 : 1), now);
                var row = B.Header(B.Proposals, id);
                row.Add("skuId", skuId); row.Add("skuSnapshot", context.Sku.DeepClone()); row.Add("definition", context.Definition.DeepClone());
                row.Add("expected", B.Expected(expected)); row.Add("basePrice", context.Current is null ? BsonNull.Value : B.Fact(context.Current));
                row.Add("input", B.Input(input)); row.Add("revision", 1L); row.Add("proposedBy", actor.UserId); row.Add("proposedAt", now.UtcDateTime);
                B.Metadata(row, auditId, actor, metadata, hash); B.Proposal(row);
                await Rows(B.Proposals).InsertOneAsync(context.Tx, row, cancellationToken: token);
                await EffectsAsync(context.Tx, row, token);
                return B.Result(row);
            }, ct);
    }
    public async Task<BsonDocument> DecideAsync(ActorContext actor, long proposalId, long expectedRevision, CatalogPriceDecision decision,
        string reviewReference, AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        Metadata(metadata);
        if (proposalId <= 0 || expectedRevision <= 0 || !Enum.IsDefined(decision) || !B.Text(reviewReference, 160)) throw B.Invalid("invalid_catalog_price_request");
        await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Decisions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var priceId = decision == CatalogPriceDecision.Approve ? (await ids.NextAsync(B.Versions, ct)).Value : (long?)null;
        var permission = decision == CatalogPriceDecision.Withdraw ? Permissions.PricingManage : Permissions.PricingApprove;
        var body = new { proposalId, expectedRevision, decision, reviewReference };
        var hash = AuthorizationStore.CommandHash(body, metadata.Reason);
        BsonDocument? selected = null;
        return await CommandAsync(actor, B.Prefix + B.Name(decision), permission, metadata, body,
            async (snapshot, tx, token) => {
                var scope = await Rows(B.Proposals).Find(tx, F.Eq("_id", proposalId)).Project(new BsonDocument("skuId", 1)).SingleOrDefaultAsync(token);
                if (scope is null || !B.Positive(scope, "skuId")) throw B.Invalid("scope_denied");
                var context = await ContextAsync(actor, snapshot, tx, scope["skuId"].AsInt64, permission, token);
                selected = await ProposalAsync(context, proposalId, token);
                if (decision == CatalogPriceDecision.Withdraw && selected["proposedBy"].AsInt64 != actor.UserId) throw B.Invalid("catalog_price_proposer_required");
                return (context, decision == CatalogPriceDecision.Withdraw ? null :
                    new ApprovalRequest(selected["revision"].AsInt64, expectedRevision, ProposedBy: selected["proposedBy"].AsInt64));
            },
            async (context, token) => {
                var proposal = selected ?? throw B.Invalid();
                if (proposal["revision"].AsInt64 != expectedRevision) throw Changed();
                if (await Rows(B.Decisions).Find(context.Tx, F.Eq("proposalId", proposalId)).AnyAsync(token))
                    throw new PersistenceConflictException("catalog_price_already_decided", "This price proposal already has a decision");
                var now = Now(); if (now < B.Time(proposal["proposedAt"])) throw B.Invalid();
                PriceVersion? price = null;
                if (decision == CatalogPriceDecision.Approve) {
                    Active(context);
                    price = CatalogPricePolicy.Approve(CatalogRepository.Sku(context.Sku), CatalogRepository.Definition(context.Definition),
                        context.Current, B.Expected(proposal["expected"].AsBsonDocument), B.Input(proposal["input"].AsBsonDocument), new(priceId!.Value), now);
                }
                var row = B.Header(B.Decisions, id);
                row.Add("skuId", proposal["skuId"]); row.Add("proposalId", proposalId); row.Add("revision", expectedRevision);
                row.Add("decision", B.Name(decision)); row.Add("priceId", price is null ? BsonNull.Value : new BsonInt64(price.Id.Value));
                row.Add("priceVersion", price is null ? BsonNull.Value : new BsonInt64(price.Version));
                row.Add("decidedBy", actor.UserId); row.Add("decidedAt", now.UtcDateTime); row.Add("reviewReference", reviewReference);
                B.Metadata(row, auditId, actor, metadata, hash); B.Decision(row, proposal);
                if (price is not null) {
                    var version = B.Header(B.Versions, price.Id.Value);
                    foreach (var field in B.Fact(price).Where(field => field.Name != "_id")) version.Add(field);
                    version.Add("proposalId", proposalId); version.Add("decisionId", id); _ = B.Price(version);
                    await Rows(B.Versions).InsertOneAsync(context.Tx, version, cancellationToken: token);
                    var head = B.Header(B.Heads, price.SkuId.Value);
                    head.Add("priceId", price.Id.Value); head.Add("version", price.Version);
                    head.Add("fence", context.Head is null ? 0L : NextFence(context.Head)); B.Head(head);
                    if (context.Head is null) await Rows(B.Heads).InsertOneAsync(context.Tx, head, cancellationToken: token);
                    else if ((await Rows(B.Heads).ReplaceOneAsync(context.Tx, context.Head, head, cancellationToken: token)).ModifiedCount != 1) throw Changed();
                    context.HeadChanged = true;
                }
                await Rows(B.Decisions).InsertOneAsync(context.Tx, row, cancellationToken: token);
                await EffectsAsync(context.Tx, row, token);
                return B.Result(row);
            }, ct);
    }
    private static long NextFence(BsonDocument row) => row["fence"].AsInt64 == long.MaxValue ? throw B.Invalid() : row["fence"].AsInt64 + 1;
    private static PersistenceConflictException Changed() => new("catalog_price_changed", "Price or package changed; reload and review");
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct)
    {
        await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument {
            { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "sourceKey", "catalog.price.command:" + row["auditId"].AsInt64 },
            { "kind", "catalog.price.changed" }, { "createdAt", B.At(row) }, { "status", "pending" }, { "attempts", 0 },
            { "dueAt", B.At(row) }, { "payload", B.Result(row) } }, cancellationToken: ct);
    }
    private Task<BsonDocument> CommandAsync(ActorContext actor, string operation, string permission, AuthorizationCommandMetadata metadata,
        object body, Func<AuthorizationSnapshot, IClientSessionHandle, CancellationToken, Task<(Context Context, ApprovalRequest? Approval)>> target,
        Func<Context, CancellationToken, Task<BsonDocument>> write, CancellationToken ct, bool requireReceipt = false)
    {
        Context? captured = null;
        return authorization.ExecuteCommandAsync(actor, operation, metadata, body, async (snapshot, tx, token) => {
            var result = await target(snapshot, tx, token); captured = result.Context;
            var query = Require(captured);
            return new(new(true, "allowed", query.Clauses.Where(clause => clause.Matches(captured.Resource)).Select(clause => clause.GrantId).ToImmutableArray()),
                ScopePolicy.RequiredNodes(captured.Resource, snapshot.Directory), permission, new(captured.Resource, result.Approval));
        }, (_, token) => write(captured ?? throw B.Invalid(), token), ct, async (finalSnapshot, tx, token) => {
            var context = captured ?? throw B.Invalid();
            var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) &
                F.Eq("key", metadata.IdempotencyKey)).SingleOrDefaultAsync(token) ?? throw B.Invalid();
            BsonDocument result = null!;
            B.Guard(() => {
                result = receipt["result"].AsBsonDocument;
                if (!B.Positive(result, "proposalId") || !B.Positive(result, "skuId") || result["skuId"] != context.Sku["_id"]) throw B.Invalid();
            });
            var proposal = await ProposalAsync(context, result["proposalId"].AsInt64, token);
            var selected = proposal;
            if (operation != B.Prefix + "propose") {
                if (!B.Positive(result, "decisionId")) throw B.Invalid();
                selected = await GetAsync(tx, B.Decisions, result["decisionId"].AsInt64, token);
                B.Decision(selected, proposal); await ProvenanceAsync(tx, selected, proposal, token);
                if (selected["decision"] == "approve") {
                    var price = await GetAsync(tx, B.Versions, selected["priceId"].AsInt64, token);
                    _ = await PriceGraphAsync(context, price, token);
                }
            }
            if (B.Actor(selected).AsInt64 != actor.UserId || B.Operation(selected) != operation ||
                selected["requestKey"] != metadata.IdempotencyKey || selected["bodyHash"] != AuthorizationStore.CommandHash(body, metadata.Reason) ||
                !result.Equals(B.Result(selected))) throw B.Invalid();
            await CompleteAsync(context, token);
        }, requireReceipt: requireReceipt);
    }
}
