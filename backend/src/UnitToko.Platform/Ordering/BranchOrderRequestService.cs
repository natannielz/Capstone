using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.BranchOrderRequestBson;

namespace UnitToko.Platform.Ordering;

/// <summary>Request-only buyer and staff decisions. No price, reservation, payment or fulfillment writer.</summary>
public sealed class BranchOrderRequestService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private readonly AuthorizationStore authorization = new(database, unit, clock);
    private readonly CatalogDefinitionQueryService catalog = new(database, new AuthorizationStore(database, unit, clock), clock);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private static DomainValidationException Invalid(string code = "branch_request_unavailable") => new(code, "The branch request or its reviewed source is unavailable");
    private static PersistenceConflictException Changed() => new("branch_request_changed", "Reload the current branch request");
    private static string Number(long id) => "PT-" + id.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
    private static object SubmitBody(BranchOrderRequestInput input, string binding) => new { input, binding };
    private static object DecideBody(long id, BranchOrderRequestDecision action, string binding) => new { id, action, binding };
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata); BranchOrderRequestRules.Reason(metadata.Reason);
        if (metadata.IdempotencyKey.Length > 128 || metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')) ||
            string.IsNullOrWhiteSpace(metadata.CorrelationId) || metadata.CorrelationId.Length > 128 || metadata.CorrelationId.Any(char.IsControl)) throw Invalid("invalid_branch_request_metadata");
    }
    private async Task StorageAsync(CancellationToken ct) {
        if (!await new CollectionMigrations(database, clock).IsCurrentAsync(ct)) throw Invalid("branch_request_storage_unavailable");
        foreach (var name in BranchOrderRequestSchemas.Collections) {
            await InventoryRepository.RequireStorageAsync(database, name, BranchOrderRequestSchemas.Validator(name), BranchOrderRequestSchemas.Keys(name), ct);
            var indexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            foreach (var key in BranchOrderRequestSchemas.SupportKeys(name))
                if (!indexes.Any(index => index["key"] == key && !index.GetValue("unique", false).AsBoolean && !InvoiceStorage.Unsafe(index) && !index.Contains("partialFilterExpression")))
                    throw Invalid("branch_request_storage_unavailable");
        }
    }
    private static ResourceScope Resource(OrderBuyingContext context, GrantScope owner, long maker) => new(
        BuyingOrganizationId: context.OrganizationId.Value, BranchId: context.BranchId.Value, DivisionId: context.DivisionId?.Value,
        FulfillmentOrganizationId: owner.OrganizationId, ShopId: owner.ShopId, WarehouseId: owner.WarehouseId,
        ChannelId: owner.ChannelId, CreatedBy: maker);
    private static ResourceScope Resource(BsonDocument row) => Resource(OrderDraftBson.Context(row["input"]["context"].AsBsonDocument),
        B.Owner(row["lines"][0]["owner"]), row["createdBy"].AsInt64);
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot snapshot, string permission, ResourceScope? resource = null) {
        var query = ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow); AuthorizationStore.Require(query);
        if (resource is not null && (!ScopePolicy.IsResourceActive(resource, snapshot.Directory) || !query.Matches(resource))) throw new AuthorizationDeniedException("scope_denied");
        return query;
    }
    private static AuthorizationQuery StaffRead(AuthorizationQuery query) => query with {
        Clauses = query.Clauses.Where(clause => clause.Scope.Kind is GrantScopeKind.FulfillmentShop or GrantScopeKind.FulfillmentWarehouse)
            .ToImmutableArray() };
    private static bool CanRead(AuthorizationQuery query, ResourceScope resource, long maker, long actorId) =>
        maker == actorId && query.Matches(resource) || StaffRead(query).Matches(resource);
    private async Task CompleteAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx,
        string permission, IReadOnlyCollection<ResourceScope> resources, CancellationToken ct) {
        var query = Query(actor, snapshot, permission);
        if (resources.Any(resource => !ScopePolicy.IsResourceActive(resource, snapshot.Directory) || !query.Matches(resource))) throw new AuthorizationDeniedException("scope_denied");
        var clauses = query.Clauses.Where(clause => resources.Count == 0 || resources.Any(clause.Matches)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(resource => ScopePolicy.RequiredNodes(resource, snapshot.Directory))).Distinct(), [],
            snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        await authorization.FenceAsync(actor, [], tx, ct);
        _ = Query(actor, snapshot, permission);
        foreach (var resource in resources) _ = Query(actor, snapshot, permission, resource);
    }
    public Task<ImmutableArray<OrderBuyingOption>> ContextsAsync(ActorContext actor, CancellationToken ct = default) =>
        authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var query = Query(actor, snapshot, Permissions.BranchOrderRequestSubmit);
            var contexts = snapshot.Directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Branch && node.ParentId is > 0)
                .SelectMany(branch => new[] { new OrderBuyingContext(new(branch.ParentId!.Value), new(branch.Id)) }.Concat(
                    snapshot.Directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Division && node.ParentId == branch.Id)
                        .Select(node => new OrderBuyingContext(new(branch.ParentId!.Value), new(branch.Id), new(node.Id)))))
                .Where(context => query.Matches(context.Resource(actor.UserId))).Distinct().OrderBy(value => value.BranchId.Value).ToArray();
            if (contexts.Length > 256) throw Invalid("branch_request_context_limit");
            var profiles = await database.GetCollection<BsonDocument>("organizationProfiles").Find(tx,
                F.In("_id", contexts.SelectMany(context => new long?[] { context.OrganizationId.Value, context.BranchId.Value, context.DivisionId?.Value }).OfType<long>().Distinct()))
                .ToListAsync(token);
            string? Name(long? id) => id is null ? null : profiles.SingleOrDefault(row => row["_id"].AsInt64 == id)?.GetValue("name", BsonNull.Value).AsString;
            if (contexts.Any(context => Name(context.OrganizationId.Value) is null || Name(context.BranchId.Value) is null ||
                context.DivisionId is not null && Name(context.DivisionId.Value.Value) is null)) throw Invalid("branch_request_labels_unavailable");
            await CompleteAsync(actor, snapshot, tx, Permissions.BranchOrderRequestSubmit, contexts.Select(context => context.Resource(actor.UserId)).ToArray(), token);
            return contexts.Select(context => new OrderBuyingOption(context, Name(context.OrganizationId.Value), Name(context.BranchId.Value), Name(context.DivisionId?.Value)))
                .ToImmutableArray();
        }, ct);
    public Task<CatalogPage<CatalogPublishedVariant>> PackagesAsync(ActorContext actor, OrderBuyingContext context,
        int offset = 0, int limit = 25, string? search = null, CancellationToken ct = default) {
        context.Validate();
        return authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            _ = Query(actor, snapshot, Permissions.BranchOrderRequestSubmit, context.Resource(actor.UserId));
            var page = await catalog.QueryForBranchAsync(actor, snapshot, tx, context.Scope, offset, limit, search,
                inner => CompleteAsync(actor, snapshot, tx, Permissions.BranchOrderRequestSubmit, [context.Resource(actor.UserId)], inner), token);
            await CompleteAsync(actor, snapshot, tx, Permissions.BranchOrderRequestSubmit, [context.Resource(actor.UserId)], token);
            return page;
        }, ct);
    }
    private async Task<BsonDocument> LabelsAsync(IClientSessionHandle tx, OrderBuyingContext context, GrantScope owner, CancellationToken ct) {
        var idsToRead = new long?[] { context.OrganizationId.Value, context.BranchId.Value, context.DivisionId?.Value,
            owner.OrganizationId, owner.ShopId, owner.WarehouseId, owner.ChannelId }.OfType<long>().Distinct().ToArray();
        var profiles = await database.GetCollection<BsonDocument>("organizationProfiles").Find(tx, F.In("_id", idsToRead)).ToListAsync(ct);
        BsonValue Name(long? id) {
            if (id is null) return BsonNull.Value;
            var row = profiles.SingleOrDefault(value => value["_id"] == id.Value);
            if (row is null || !row.TryGetValue("name", out var name) || !name.IsString || string.IsNullOrWhiteSpace(name.AsString))
                throw Invalid("branch_request_labels_unavailable");
            return name.DeepClone();
        }
        return new() { { "buyingOrganization", Name(context.OrganizationId.Value) }, { "branch", Name(context.BranchId.Value) },
            { "division", Name(context.DivisionId?.Value) }, { "fulfillmentOrganization", Name(owner.OrganizationId) },
            { "shop", Name(owner.ShopId) }, { "warehouse", Name(owner.WarehouseId) }, { "channel", Name(owner.ChannelId) } };
    }
    private async Task<(BsonArray Lines, GrantScope Owner, BsonDocument Labels)> SourceAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx,
        BranchOrderRequestInput input, CancellationToken ct) {
        _ = Query(actor, snapshot, Permissions.BranchOrderRequestSubmit, input.Context.Resource(actor.UserId));
        var facts = await catalog.ReadSelectedAsync(actor, snapshot, tx, input.Context.Scope, input.Lines.Select(line => line.SkuId.Value).ToImmutableArray(),
            token => CompleteAsync(actor, snapshot, tx, Permissions.BranchOrderRequestSubmit, [input.Context.Resource(actor.UserId)], token), ct);
        var bySku = facts.ToDictionary(fact => fact.Sku.Id.Value);
        var lines = new BsonArray(); GrantScope? owner = null;
        foreach (var selected in input.Lines) {
            if (!bySku.TryGetValue(selected.SkuId.Value, out var fact)) throw Invalid("branch_request_package_unavailable");
            CatalogPolicy.Validate(fact.Sku, fact.Definition);
            if (fact.Definition.Id != selected.DefinitionId || fact.Definition.Version != selected.DefinitionVersion) throw Changed();
            if (selected.Packages < fact.Definition.MinimumOrderPackages || selected.Packages % fact.Definition.OrderIncrementPackages != 0) throw Invalid("branch_request_quantity_invalid");
            owner ??= fact.Family.AuthoringOwner;
            if (owner != fact.Family.AuthoringOwner) throw Invalid("branch_request_multiple_fulfillment_owners");
            lines.Add(B.Line(new(fact.Family, fact.Sku, fact.Definition, fact.CurrentPrice), selected));
        }
        if (owner is null || owner.OrganizationId is not > 0 || owner.ShopId is not > 0 || owner.ChannelId is not > 0) throw Invalid();
        return (lines, owner, await LabelsAsync(tx, input.Context, owner, ct));
    }
    private static string Binding(ActorContext actor, BranchOrderRequestInput input, BsonArray lines, BsonDocument labels) => CatalogRepository.Digest(new BsonDocument {
        { "purpose", "branch-request-review" }, { "actorId", actor.UserId }, { "grantVersion", actor.GrantVersion },
        { "input", B.Input(input) }, { "lines", lines.DeepClone() }, { "labels", labels.DeepClone() } });
    public Task<BsonDocument> PreviewAsync(ActorContext actor, BranchOrderRequestInput input, CancellationToken ct = default) {
        input = BranchOrderRequestRules.Copy(input);
        return authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var source = await SourceAsync(actor, snapshot, tx, input, token);
            await CompleteAsync(actor, snapshot, tx, Permissions.BranchOrderRequestSubmit, [Resource(input.Context, source.Owner, actor.UserId)], token);
            return new BsonDocument { { "input", B.Input(input) }, { "lines", source.Lines }, { "owner", B.Owner(source.Owner) },
                { "labels", source.Labels }, { "binding", Binding(actor, input, source.Lines, source.Labels) }, { "observedAt", Now.UtcDateTime } };
        }, ct);
    }
    public async Task<BsonDocument> SubmitAsync(ActorContext actor, BranchOrderRequestInput input, string binding,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = BranchOrderRequestRules.Copy(input); Metadata(metadata); await StorageAsync(ct);
        if (binding is null || binding.Length != 64 || binding.Any(c => !char.IsAsciiHexDigitLower(c))) throw Invalid("invalid_branch_request_binding");
        var id = (await ids.NextAsync(B.Requests, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = SubmitBody(input, binding); var hash = AuthorizationStore.CommandHash(body, metadata.Reason);
        AuthorizationSnapshot current = null!; BsonDocument? wrote = null;
        return await authorization.ExecuteCommandAsync(actor, B.SubmitOperation, metadata, body, async (snapshot, tx, token) => {
            current = snapshot; wrote = null;
            var resource = input.Context.Resource(actor.UserId); _ = Query(actor, snapshot, Permissions.BranchOrderRequestSubmit, resource);
            var prior = await Rows(B.Requests).Find(tx, F.Eq("createdBy", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).Limit(2).ToListAsync(token);
            if (prior.Count > 1) throw Invalid();
            if (prior.Count == 1) _ = await RequestAsync(tx, prior[0]["_id"].AsInt64, token);
            return new(ScopePolicy.Authorize(actor, snapshot, Permissions.BranchOrderRequestSubmit, resource, clock.UtcNow),
                ScopePolicy.RequiredNodes(resource, snapshot.Directory), Permissions.BranchOrderRequestSubmit, new(resource));
        }, async (tx, token) => {
            var source = await SourceAsync(actor, current, tx, input, token);
            if (Binding(actor, input, source.Lines, source.Labels) != binding) throw Changed();
            var now = Now; var owner = source.Owner;
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "kind", "ordering.branchrequest" },
                { "number", Number(id) }, { "fence", 0L }, { "state", "pending" }, { "decisionId", BsonNull.Value },
                { "createdBy", actor.UserId }, { "buyingOrganizationId", input.Context.OrganizationId.Value }, { "branchId", input.Context.BranchId.Value },
                { "divisionId", input.Context.DivisionId is { } division ? new BsonInt64(division.Value) : BsonNull.Value },
                { "fulfillmentOrganizationId", owner.OrganizationId!.Value }, { "shopId", owner.ShopId!.Value },
                { "warehouseId", owner.WarehouseId is { } warehouse ? new BsonInt64(warehouse) : BsonNull.Value },
                { "channelId", owner.ChannelId!.Value }, { "input", B.Input(input) }, { "lines", source.Lines }, { "labels", source.Labels },
                { "recordedAt", now.UtcDateTime }, { "actorGrantVersion", actor.GrantVersion }, { "requestKey", metadata.IdempotencyKey },
                { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "bodyHash", hash }, { "auditId", auditId } };
            row["snapshotHash"] = B.Hash(row);
            await Rows(B.Requests).InsertOneAsync(tx, row, cancellationToken: token);
            await Rows("auditEvents").InsertOneAsync(tx, B.SubmitAudit(row), cancellationToken: token);
            await Rows("outboxEvents").InsertOneAsync(tx, B.Outbox("ordering.branchrequest.submitted:" + id, "ordering.branchrequest.submitted", B.SubmitResult(row), now), cancellationToken: token);
            wrote = row; return B.SubmitResult(row);
        }, ct, async (snapshot, tx, token) => {
            var row = await RequestAsync(tx, wrote?["_id"].AsInt64 ?? await OriginalIdAsync(tx, actor.UserId, B.SubmitOperation, metadata.IdempotencyKey, token), token);
            if (row["createdBy"] != actor.UserId || row["bodyHash"] != hash) throw Invalid();
            if (wrote is not null) {
                var source = await SourceAsync(actor, snapshot, tx, input, token);
                if (Binding(actor, input, source.Lines, source.Labels) != binding) throw Changed();
            }
            await CompleteAsync(actor, snapshot, tx, Permissions.BranchOrderRequestSubmit, [Resource(row)], token);
        });
    }
    private async Task<long> OriginalIdAsync(IClientSessionHandle tx, long actorId, string operation, string key, CancellationToken ct) {
        var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actorId) & F.Eq("operation", operation) & F.Eq("key", key)).SingleOrDefaultAsync(ct);
        if (receipt is null || !receipt.TryGetValue("result", out var result) || !result.IsBsonDocument ||
            !result.AsBsonDocument.TryGetValue("requestId", out var id) || !id.IsInt64) throw Invalid();
        return id.AsInt64;
    }
    private async Task<BsonDocument> RequestAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await Rows(B.Requests).Find(tx, F.Eq("_id", id) & BranchOrderRequestSchemas.Validator(B.Requests)).SingleOrDefaultAsync(ct)
            ?? throw Invalid();
        var input = B.Input(row["input"]);
        if (row["snapshotHash"] != B.Hash(row) || row["number"] != Number(id) || row["createdBy"].AsInt64 <= 0 ||
            row["buyingOrganizationId"] != input.Context.OrganizationId.Value || row["branchId"] != input.Context.BranchId.Value ||
            row["divisionId"] != (input.Context.DivisionId is { } division ? new BsonInt64(division.Value) : BsonNull.Value) ||
            row["lines"].AsBsonArray.Count != input.Lines.Length ||
            row["lines"].AsBsonArray.Where((line, index) => line["skuId"] != input.Lines[index].SkuId.Value ||
                line["definitionId"] != input.Lines[index].DefinitionId.Value || line["definitionVersion"] != input.Lines[index].DefinitionVersion ||
                line["packages"] != input.Lines[index].Packages || line["owner"] != row["lines"][0]["owner"]).Any()) throw Invalid();
        var owner = B.Owner(row["lines"][0]["owner"]);
        if (row["fulfillmentOrganizationId"] != owner.OrganizationId || row["shopId"] != owner.ShopId || row["warehouseId"] !=
            (owner.WarehouseId is { } warehouse ? new BsonInt64(warehouse) : BsonNull.Value) || row["channelId"] != owner.ChannelId)
            throw Invalid();
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", row["createdBy"]) & F.Eq("operation", B.SubmitOperation) &
            F.Eq("key", row["requestKey"])).SingleOrDefaultAsync(ct);
        var events = await Rows("outboxEvents").Find(tx, F.Eq("sourceKey", "ordering.branchrequest.submitted:" + id)).Limit(2).ToListAsync(ct);
        if (audit is null || audit != B.SubmitAudit(row) || receipt is null || receipt["bodyHash"] != row["bodyHash"] ||
            receipt["result"] != B.SubmitResult(row) || events.Count != 1 || events[0]["kind"] != "ordering.branchrequest.submitted" ||
            events[0]["payload"] != B.SubmitResult(row)) throw Invalid();
        var decisions = await Rows(B.Decisions).Find(tx, F.Eq("requestId", id)).Limit(2).ToListAsync(ct);
        if (row["state"] == "pending") { if (!row["decisionId"].IsBsonNull || decisions.Count != 0) throw Invalid(); }
        else if (row["decisionId"].IsBsonNull || decisions.Count != 1 || decisions[0]["_id"] != row["decisionId"] ||
            (row["state"] == "accepted") != (decisions[0]["action"] == "accept")) throw Invalid();
        else _ = await DecisionAsync(tx, row, decisions[0], ct);
        return row;
    }
    private async Task<BsonDocument> DecisionAsync(IClientSessionHandle tx, BsonDocument request, BsonDocument decision, CancellationToken ct) {
        if (!await Rows(B.Decisions).Find(tx, F.Eq("_id", decision["_id"]) & BranchOrderRequestSchemas.Validator(B.Decisions)).AnyAsync(ct)) throw Invalid();
        if (decision["requestId"] != request["_id"] || decision["requestHash"] != request["snapshotHash"] ||
            decision["expectedVersion"] != 1L || decision["decidedBy"] == request["createdBy"] ||
            OrderDraftBson.Time(decision["recordedAt"]) < OrderDraftBson.Time(request["recordedAt"]) ||
            decision["bodyHash"] != AuthorizationStore.CommandHash(DecideBody(request["_id"].AsInt64,
                decision["action"] == "accept" ? BranchOrderRequestDecision.Accept : BranchOrderRequestDecision.Reject,
                decision["binding"].AsString), decision["reason"].AsString)) throw Invalid();
        var result = B.DecideResult(decision, request);
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", decision["auditId"])).SingleOrDefaultAsync(ct);
        var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", decision["decidedBy"]) & F.Eq("operation", B.DecideOperation) &
            F.Eq("key", decision["requestKey"])).SingleOrDefaultAsync(ct);
        var events = await Rows("outboxEvents").Find(tx, F.Eq("sourceKey", "ordering.branchrequest.decided:" + decision["_id"])).Limit(2).ToListAsync(ct);
        if (audit is null || audit != B.DecideAudit(decision, request) || receipt is null || receipt["bodyHash"] != decision["bodyHash"] ||
            receipt["result"] != result || events.Count != 1 || events[0]["kind"] != "ordering.branchrequest.decided" || events[0]["payload"] != result ||
            PersonApprovalReceipt.Read(receipt, Permissions.BranchOrderRequestDecide) != new ApprovalRequest(1, 1, RequestedBy: request["createdBy"].AsInt64)) throw Invalid();
        return decision;
    }
    private static BsonDocument View(BsonDocument row, BsonDocument? decision) => new() {
        { "id", row["_id"] }, { "number", row["number"] }, { "version", decision is null ? 1L : 2L }, { "state", row["state"] },
        { "input", row["input"].DeepClone() }, { "lines", row["lines"].DeepClone() }, { "owner", row["lines"][0]["owner"].DeepClone() },
        { "labels", row["labels"].DeepClone() },
        { "createdBy", row["createdBy"] }, { "recordedAt", row["recordedAt"] },
        { "decision", decision is null ? BsonNull.Value : new BsonDocument { { "id", decision["_id"] }, { "action", decision["action"] },
            { "reason", decision["reason"] }, { "recordedAt", decision["recordedAt"] } } } };
    private async Task<BsonDocument> ViewAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        var decision = row["decisionId"].IsBsonNull ? null : await Rows(B.Decisions).Find(tx, F.Eq("_id", row["decisionId"])).SingleAsync(ct);
        return View(row, decision);
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, long id, CancellationToken ct = default) {
        if (id <= 0) throw Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var query = Query(actor, snapshot, Permissions.BranchOrderRequestRead);
            var visible = F.Or(ScopeQuery.Create(query) & F.Eq("createdBy", actor.UserId), ScopeQuery.Create(StaffRead(query)));
            if (!await Rows(B.Requests).Find(tx, visible & F.Eq("_id", id)).AnyAsync(token)) throw new AuthorizationDeniedException("scope_denied");
            var row = await RequestAsync(tx, id, token); var view = await ViewAsync(tx, row, token);
            await CompleteAsync(actor, snapshot, tx, Permissions.BranchOrderRequestRead, [Resource(row)], token);
            if (!CanRead(Query(actor, snapshot, Permissions.BranchOrderRequestRead), Resource(row), row["createdBy"].AsInt64, actor.UserId))
                throw new AuthorizationDeniedException("scope_denied");
            return view;
        }, ct);
    }
    public async Task<BsonDocument> ListAsync(ActorContext actor, bool incoming, long? beforeId = null, CancellationToken ct = default) {
        if (beforeId is <= 0) throw Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var query = Query(actor, snapshot, Permissions.BranchOrderRequestRead);
            if (incoming) query = StaffRead(query);
            var filter = ScopeQuery.Create(query) &
                (incoming ? F.Ne("createdBy", actor.UserId) : F.Eq("createdBy", actor.UserId));
            if (beforeId.HasValue) filter &= F.Lt("_id", beforeId.Value);
            var rows = await Rows(B.Requests).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = new BsonArray(); var resources = new List<ResourceScope>();
            foreach (var candidate in rows.Take(25)) { var row = await RequestAsync(tx, candidate["_id"].AsInt64, token);
                items.Add(await ViewAsync(tx, row, token)); resources.Add(Resource(row)); }
            await CompleteAsync(actor, snapshot, tx, Permissions.BranchOrderRequestRead, resources, token);
            var current = Query(actor, snapshot, Permissions.BranchOrderRequestRead);
            if (resources.Zip(rows.Take(25)).Any(pair => !CanRead(current, pair.First, pair.Second["createdBy"].AsInt64, actor.UserId) ||
                incoming && !StaffRead(current).Matches(pair.First))) throw new AuthorizationDeniedException("scope_denied");
            return new BsonDocument { { "items", items }, { "nextRequestId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    private static string DecisionBinding(ActorContext actor, BsonDocument row, BranchOrderRequestDecision action) => CatalogRepository.Digest(new BsonDocument {
        { "purpose", "branch-request-decision" }, { "actorId", actor.UserId }, { "grantVersion", actor.GrantVersion },
        { "requestId", row["_id"] }, { "requestHash", row["snapshotHash"] }, { "state", row["state"] }, { "action", action.ToString() } });
    public async Task<BsonDocument> PreviewDecisionAsync(ActorContext actor, long id, BranchOrderRequestDecision action, CancellationToken ct = default) {
        if (id <= 0 || !Enum.IsDefined(action)) throw Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var query = Query(actor, snapshot, Permissions.BranchOrderRequestDecide);
            if (!await Rows(B.Requests).Find(tx, ScopeQuery.Create(query) & F.Eq("_id", id)).AnyAsync(token)) throw new AuthorizationDeniedException("scope_denied");
            var row = await RequestAsync(tx, id, token); _ = Query(actor, snapshot, Permissions.BranchOrderRequestDecide, Resource(row));
            var proof = new ApprovalRequest(1, 1, RequestedBy: row["createdBy"].AsInt64);
            if (!ApprovalPolicy.Evaluate(actor.UserId, Permissions.BranchOrderRequestDecide, proof).Allowed) throw new AuthorizationDeniedException("independent_actor_required");
            var view = await ViewAsync(tx, row, token);
            await CompleteAsync(actor, snapshot, tx, Permissions.BranchOrderRequestDecide, [Resource(row)], token);
            return new BsonDocument { { "request", view }, { "decision", action == BranchOrderRequestDecision.Accept ? "accept" : "reject" },
                { "canDecide", row["state"] == "pending" }, { "binding", DecisionBinding(actor, row, action) }, { "observedAt", Now.UtcDateTime } };
        }, ct);
    }
    public async Task<BsonDocument> DecideAsync(ActorContext actor, long id, long expectedVersion, BranchOrderRequestDecision action,
        string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (id <= 0 || expectedVersion != 1 || !Enum.IsDefined(action) || binding is null || binding.Length != 64 ||
            binding.Any(c => !char.IsAsciiHexDigitLower(c))) throw Invalid("invalid_branch_request_decision");
        Metadata(metadata); await StorageAsync(ct);
        var decisionId = (await ids.NextAsync(B.Decisions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = DecideBody(id, action, binding); var hash = AuthorizationStore.CommandHash(body, metadata.Reason);
        BsonDocument? original = null; BsonDocument? wrote = null;
        return await authorization.ExecuteCommandAsync(actor, B.DecideOperation, metadata, body, async (snapshot, tx, token) => {
            wrote = null; var query = Query(actor, snapshot, Permissions.BranchOrderRequestDecide);
            if (!await Rows(B.Requests).Find(tx, ScopeQuery.Create(query) & F.Eq("_id", id)).AnyAsync(token)) throw new AuthorizationDeniedException("scope_denied");
            original = await RequestAsync(tx, id, token);
            var resource = Resource(original); _ = Query(actor, snapshot, Permissions.BranchOrderRequestDecide, resource);
            var previous = await Rows(B.Decisions).Find(tx, F.Eq("decidedBy", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).Limit(2).ToListAsync(token);
            if (previous.Count > 1 || previous.Count == 1 && previous[0]["requestId"] != id) throw Invalid();
            var approval = new ApprovalRequest(1, expectedVersion, RequestedBy: original["createdBy"].AsInt64);
            return new(ScopePolicy.Authorize(actor, snapshot, Permissions.BranchOrderRequestDecide, resource, clock.UtcNow, approval),
                ScopePolicy.RequiredNodes(resource, snapshot.Directory), Permissions.BranchOrderRequestDecide, new(resource, approval));
        }, async (tx, token) => {
            var row = original ?? throw Invalid();
            if (row["state"] != "pending" || row["decisionId"] != BsonNull.Value || DecisionBinding(actor, row, action) != binding) throw Changed();
            var now = Now;
            var decision = new BsonDocument { { "_id", decisionId }, { "schemaVersion", 1 }, { "kind", "ordering.branchrequest.decision" },
                { "requestId", id }, { "requestHash", row["snapshotHash"] }, { "action", action == BranchOrderRequestDecision.Accept ? "accept" : "reject" },
                { "expectedVersion", expectedVersion }, { "binding", binding }, { "decidedBy", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
                { "recordedAt", now.UtcDateTime }, { "requestKey", metadata.IdempotencyKey }, { "reason", metadata.Reason },
                { "correlationId", metadata.CorrelationId }, { "bodyHash", hash }, { "auditId", auditId } };
            await Rows(B.Decisions).InsertOneAsync(tx, decision, cancellationToken: token);
            var next = row.DeepClone().AsBsonDocument; next["state"] = decision["action"] == "accept" ? "accepted" : "rejected";
            next["decisionId"] = decisionId; next["fence"] = checked(row["fence"].AsInt64 + 1);
            if ((await Rows(B.Requests).ReplaceOneAsync(tx, row, next, cancellationToken: token)).ModifiedCount != 1) throw Changed();
            var result = B.DecideResult(decision, next);
            await Rows("auditEvents").InsertOneAsync(tx, B.DecideAudit(decision, next), cancellationToken: token);
            await Rows("outboxEvents").InsertOneAsync(tx, B.Outbox("ordering.branchrequest.decided:" + decisionId, "ordering.branchrequest.decided", result, now), cancellationToken: token);
            wrote = decision; return result;
        }, ct, async (snapshot, tx, token) => {
            var row = await RequestAsync(tx, id, token);
            if (row["createdBy"] == actor.UserId) throw Invalid();
            var selected = wrote ?? await Rows(B.Decisions).Find(tx, F.Eq("decidedBy", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).SingleOrDefaultAsync(token)
                ?? throw Invalid();
            if (selected["requestId"] != id || selected["bodyHash"] != hash || row["decisionId"] != selected["_id"]) throw Invalid();
            await CompleteAsync(actor, snapshot, tx, Permissions.BranchOrderRequestDecide, [Resource(row)], token);
        });
    }
    public async Task<BsonDocument> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        if (operation is not (B.SubmitOperation or B.DecideOperation) || string.IsNullOrWhiteSpace(key) || key.Length > 128 ||
            key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw Invalid("invalid_branch_request_recovery");
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var permission = operation == B.SubmitOperation ? Permissions.BranchOrderRequestSubmit : Permissions.BranchOrderRequestDecide;
            _ = Query(actor, snapshot, permission);
            var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", key)).SingleOrDefaultAsync(token);
            if (receipt is null) { await CompleteAsync(actor, snapshot, tx, permission, [], token);
                return new BsonDocument { { "state", "not_confirmed" }, { "result", BsonNull.Value } }; }
            var result = receipt["result"].AsBsonDocument; var row = await RequestAsync(tx, result["requestId"].AsInt64, token);
            if (operation == B.SubmitOperation) {
                if (row["createdBy"] != actor.UserId || row["requestKey"] != key || result != B.SubmitResult(row)) throw Invalid();
            } else {
                var decision = await Rows(B.Decisions).Find(tx, F.Eq("_id", row["decisionId"])).SingleOrDefaultAsync(token) ?? throw Invalid();
                if (decision["decidedBy"] != actor.UserId || decision["requestKey"] != key || result != B.DecideResult(decision, row)) throw Invalid();
            }
            await CompleteAsync(actor, snapshot, tx, permission, [Resource(row)], token);
            return new BsonDocument { { "state", "confirmed" }, { "result", result.DeepClone() } };
        }, ct);
    }
}
