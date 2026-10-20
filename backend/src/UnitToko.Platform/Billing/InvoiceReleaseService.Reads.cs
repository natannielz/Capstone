using System.Collections.Immutable;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Ordering;
using B = UnitToko.Platform.Billing.InvoiceReleaseBson;

namespace UnitToko.Platform.Billing;

public sealed record InvoiceReleaseCommandResult(LongId ReleaseId, LongId RecordId, LongId ProposalId, string Revision, string Action, string SelectionKey);
public sealed record InvoiceReleaseLine(LongId OrderId, LongId ShipmentId, LongId ReceiptId, LongId SkuId, string LineRevision, string SkuCode,
    string ProductName, string PackageLabel, string AcceptedBaseUnits, Money UnitPrice, Money Net, Money? Tax, Money? Charges);
public sealed record InvoiceReleaseBudget(LongId OrderId, Money ApprovedTax, Money ApprovedCharges, Money PostedTax, Money PostedCharges);
public sealed record InvoiceReleaseAccount(string Role, string MappingKey, LongId AccountId, string AccountCode);
public sealed record InvoiceReleaseFacts(string BuyerName, string SellerName, string Grouping, string TermOrigin, int TermDays,
    string DayRule, string Rounding, string TaxDocumentProcess, string NumberingProcedure, string ReissueProcedure,
    ImmutableArray<int> WorkingDays, ImmutableArray<DateOnly> Holidays, string? AccountingPeriod, ImmutableArray<InvoiceReleaseAccount> Accounts,
    ImmutableArray<InvoiceReleaseLine> Lines, ImmutableArray<InvoiceReleaseBudget> Budgets);
public sealed record InvoiceReleaseEligibility(string SelectionKey, string Revision, LongId? ReleaseId, string? State, InvoiceReleaseFacts Facts);
public sealed record InvoiceReleasePreview(string SelectionKey, string Binding, string Revision, InvoiceReleaseInput Input, InvoiceReleaseFacts Facts);
public sealed record InvoiceReleaseView(InvoiceReleaseCommandResult Result, LongId ActorId, DateTimeOffset RecordedAt, string Reason,
    InvoiceReleaseInput Input, InvoiceReleaseFacts Facts, string RecordHash);
public sealed record InvoiceReleasePage(ImmutableArray<InvoiceReleaseView> Items, LongId? NextId);

public sealed partial class InvoiceReleaseService
{
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static InvoiceReleaseCommandResult Result(BsonDocument row) => new(new(row["releaseId"].AsInt64), new(row["recordId"].AsInt64),
        new(row["proposalId"].AsInt64), Number(row["revision"].AsInt64), row["action"].AsString, row["selectionKey"].AsString);
    private static InvoiceReleaseFacts Facts(BsonDocument source) {
        var rule = OrderCommercialPolicyBson.Rule(source["rule"]); var terms = rule.Terms; var first = source["receipts"][0]["snapshot"];
        return new(first["commercial"]["terms"]["buyer"]["name"].AsString, first["shipment"]["seller"]["name"].AsString,
            terms.Grouping, terms.TermOrigin, terms.TermDays, terms.DayRule, rule.Rounding, terms.TaxDocumentProcess, terms.NumberingProcedure, terms.ReissueProcedure,
            [.. terms.WorkingDays.Select(day => (int)day)], terms.Holidays,
            source.Contains("accounting") ? source["accounting"]["periodMonth"].AsString : null,
            source.Contains("accounting") ? [.. source["accounting"]["accounts"].AsBsonArray.Select(value => new InvoiceReleaseAccount(value["role"].AsString,
                value["mappingKey"].AsString, new(value["accountId"].AsInt64), value["accountCode"].AsString))] : [],
            [.. source["lines"].AsBsonArray.Select(line => new InvoiceReleaseLine(new(line["orderId"].AsInt64), new(line["shipmentId"].AsInt64),
                new(line["receiptId"].AsInt64), new(line["skuId"].AsInt64), Number(OrderLineBson.Revision(line)), line["skuCode"].AsString,
                line["productName"].AsString, line["definition"]["packageLabel"].AsString, Number(line["acceptedBaseUnits"].AsInt64),
                InvoiceBson.Money(line["price"]["unitPrice"]), InvoiceBson.Money(line["net"]),
                line.AsBsonDocument.Contains("tax") ? InvoiceBson.Money(line["tax"]) : null, line.AsBsonDocument.Contains("charges") ? InvoiceBson.Money(line["charges"]) : null))],
            [.. source["budgets"].AsBsonArray.Select(budget => new InvoiceReleaseBudget(new(budget["orderId"].AsInt64),
                InvoiceBson.Money(budget["terms"]["tax"]), InvoiceBson.Money(budget["terms"]["charges"]),
                budget["allocations"].AsBsonArray.Aggregate(new Money(0m), (sum, row) => Money.Add(sum, InvoiceBson.Money(row["tax"]))),
                budget["allocations"].AsBsonArray.Aggregate(new Money(0m), (sum, row) => Money.Add(sum, InvoiceBson.Money(row["charges"])))))]);
    }
    private static InvoiceReleasePreview Preview(string binding, InvoiceReleaseInput input, BsonDocument source, long revision) => new(B.SelectionKey(input.Receipts), binding, Number(revision), input, Facts(source));
    private static InvoiceReleaseView View(BsonDocument row) => new(Result(B.Result(row)), new(row["actorId"].AsInt64), B.Time(row["recordedAt"]),
        row["reason"].AsString, B.Input(row["input"]), Facts(row["source"].AsBsonDocument), row["recordHash"].AsString);

    public async Task<InvoiceReleaseEligibility> EligibilityAsync(ActorContext actor, IEnumerable<InvoiceReceiptSelection> selected, CancellationToken ct = default) {
        var copied = InvoicePolicy.Copy(selected); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, copied, B.ReadPermission, token);
            var commercial = await new InvoiceReleaseCommercial(database, receipts).ReadAndFenceAsync(actor, tx, context.Receipts, token);
            var budgets = new BsonArray(); var lines = new BsonArray(); var invoiceRecords = new InvoiceRecords(database, receipts);
            foreach (var order in context.Receipts.GroupBy(source => source.Snapshot["commercial"]["orderId"].AsInt64).OrderBy(group => group.Key)) {
                var previous = await invoiceRecords.OrderAllocationsAsync(actor, tx, order.Key, token);
                var budget = await receipts.CurrentInvoiceBudgetAsync(actor, tx, order.Key, token);
                budgets.Add(new BsonDocument { { "orderId", order.Key }, { "terms", budget }, { "allocations", new BsonArray(previous) } });
                var accumulated = previous.Select(row => row.DeepClone().AsBsonDocument).ToList();
                foreach (var receipt in order) foreach (var accepted in receipt.Snapshot["receipt"]["lines"].AsBsonArray) {
                    if (previous.Any(row => row["receiptId"] == receipt.Snapshot["receipt"]["id"] && row["skuId"] == accepted["skuId"])) throw B.Changed();
                    var prior = accumulated.Where(row => OrderLineBson.Same(row, accepted)).ToArray();
                    if (prior.Any(row => row["rounding"] != commercial.Common.Rounding)) throw B.Changed();
                    var units = prior.Aggregate(0L, (sum, row) => checked(sum + row["acceptedBaseUnits"].AsInt64));
                    var previousNet = prior.Aggregate(new Money(0m), (sum, row) => Money.Add(sum, InvoiceBson.Money(row["net"])));
                    var quote = receipt.Snapshot["commercial"]["quote"]["lines"].AsBsonArray.Single(row => OrderLineBson.Same(row, accepted));
                    var net = InvoicePolicy.AcceptedDelta(InvoiceBson.Money(quote["price"]["unitPrice"]), quote["definition"]["baseUnitsPerPackage"].AsInt32,
                        quote["baseUnits"].AsInt64, units, previousNet, accepted["acceptedBaseUnits"].AsInt64, commercial.Common.Rounding);
                    var line = OrderLineBson.With(new BsonDocument { { "orderId", order.Key }, { "shipmentId", receipt.Snapshot["shipment"]["shipmentId"] },
                        { "receiptId", receipt.Snapshot["receipt"]["id"] }, { "skuId", accepted["skuId"] }, { "skuCode", quote["skuCode"] },
                        { "productName", quote["familyName"] }, { "definition", quote["definition"] }, { "price", quote["price"] },
                        { "acceptedBaseUnits", accepted["acceptedBaseUnits"] }, { "net", InvoiceBson.Money(net) }, { "rounding", commercial.Common.Rounding } }, OrderLineBson.Revision(accepted));
                    lines.Add(line); accumulated.Add(line);
                }
            }
            var source = new BsonDocument { { "receipts", InvoiceBson.Sources(context.Receipts) }, { "rule", OrderCommercialPolicyBson.Rule(commercial.Common) }, { "budgets", budgets }, { "lines", lines } };
            await FenceAsync(context, token);
            return new InvoiceReleaseEligibility(B.SelectionKey(copied), Number(context.History?.Head["revision"].AsInt64 ?? 0),
                context.History is null ? null : new(context.History.Head["_id"].AsInt64), context.History?.Head["state"].AsString, Facts(source));
        }, ct);
    }
    public async Task<InvoiceCandidatePage> CandidatesAsync(ActorContext actor, long? beforeShipmentId = null, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var shipmentIds = await receipts.InvoiceReleaseShipmentsAsync(actor, authority, tx, beforeShipmentId, token);
            var found = ImmutableArray.CreateBuilder<InvoiceCandidate>(); var allScopes = new List<ResourceScope>();
            foreach (var shipmentId in shipmentIds.Take(25)) {
                var source = await receipts.InvoiceSourceAsync(actor, authority, tx, shipmentId, Permissions.InvoiceRead, token); if (source is null) continue;
                allScopes.Add(source.Resource); var accepted = source.Snapshot["receipt"]["lines"].AsBsonArray; if (accepted.Count == 0) continue;
                var previous = await new InvoiceRecords(database, receipts).OrderAllocationsAsync(actor, tx, source.Snapshot["commercial"]["orderId"].AsInt64, token);
                var billed = previous.Count(row => row["receiptId"] == source.Snapshot["receipt"]["id"]);
                if (billed != 0 && billed != accepted.Count) throw B.Broken();
                found.Add(new(new(shipmentId), new(source.Snapshot["receipt"]["id"].AsInt64), B.Time(source.Snapshot["receipt"]["recordedAt"]),
                    source.Snapshot["commercial"]["terms"]["buyer"]["name"].AsString, accepted.Count, billed > 0));
            }
            await receipts.InvoiceFenceAsync(actor, authority, tx, allScopes, Permissions.InvoiceRead, token);
            await FenceScopesAsync(actor, authority, tx, allScopes, B.ReadPermission, token);
            return new InvoiceCandidatePage(found.ToImmutable(), shipmentIds.Length > 25 ? new(shipmentIds[24]) : null);
        }, ct);
    }
    public async Task<InvoiceReleaseView> ReadAsync(ActorContext actor, long recordId, CancellationToken ct = default) {
        if (recordId <= 0) throw InvoiceReleaseRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await RecordContextAsync(actor, authority, tx, recordId, B.ReadPermission, token);
            var row = context.History!.Events.Single(row => row["_id"] == recordId); await FenceAsync(context, token); return View(row);
        }, ct);
    }
    public async Task<InvoiceReleaseCommandResult?> RecoverAsync(ActorContext actor, string action, string key, CancellationToken ct = default) {
        var permission = B.Permission(action); Metadata(new(key, "Original operation lookup", "invoice-release-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, permission, clock.UtcNow));
            var route = await records.Rows(B.Events).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "operation", B.Prefix + action }, { "requestKey", key } })
                .Project(new BsonDocument("_id", 1)).SingleOrDefaultAsync(token);
            if (route is null) {
                _ = await records.ForKeyAsync(tx, actor.UserId, B.Prefix + action, key, token);
                await authorization.FenceAsync(actor, [], tx, token); return null;
            }
            var context = await RecordContextAsync(actor, authority, tx, route["_id"].AsInt64, permission, token);
            var row = await records.ForKeyAsync(tx, actor.UserId, B.Prefix + action, key, token) ?? throw B.Broken();
            var proposal = context.History!.Events.Single(value => value["_id"] == row["proposalId"]);
            await PeopleAsync(context, proposal, action == "propose" ? null : row, token); await FenceAsync(context, token); return Result(B.Result(row));
        }, ct);
    }
    public async Task<InvoiceReleasePage> ListAsync(ActorContext actor, long? beforeId = null, CancellationToken ct = default) {
        if (beforeId is <= 0) throw InvoiceReleaseRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var query = ScopePolicy.Compile(actor, authority, B.ReadPermission, clock.UtcNow); AuthorizationStore.Require(query);
            var sourceQuery = ScopePolicy.Compile(actor, authority, Permissions.InvoiceRead, clock.UtcNow); AuthorizationStore.Require(sourceQuery);
            var filter = ScopeQuery.Create(query) & ScopeQuery.Create(sourceQuery);
            var rendered = filter.Render(new RenderArgs<BsonDocument>(BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry));
            var pipeline = new List<BsonDocument>(); if (beforeId is not null) pipeline.Add(new("$match", new BsonDocument("_id", new BsonDocument("$lt", beforeId.Value))));
            pipeline.Add(new("$set", new BsonDocument("scopeCount", new BsonDocument("$size", "$scopes")))); pipeline.Add(new("$unwind", "$scopes"));
            pipeline.Add(new("$replaceRoot", new BsonDocument("newRoot", new BsonDocument("$mergeObjects", new BsonArray { "$scopes",
                new BsonDocument { { "releaseId", "$_id" }, { "eventId", "$eventId" }, { "scopeCount", "$scopeCount" } } }))));
            pipeline.Add(new("$match", rendered)); pipeline.Add(new("$group", new BsonDocument { { "_id", "$releaseId" }, { "eventId", new BsonDocument("$first", "$eventId") },
                { "matches", new BsonDocument("$sum", 1) }, { "expected", new BsonDocument("$first", "$scopeCount") } }));
            pipeline.Add(new("$match", new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$matches", "$expected" }))));
            pipeline.Add(new("$sort", new BsonDocument("_id", -1))); pipeline.Add(new("$limit", 26));
            var rows = await records.Rows(B.Heads).Aggregate<BsonDocument>(tx, pipeline).ToListAsync(token); var result = ImmutableArray.CreateBuilder<InvoiceReleaseView>();
            foreach (var row in rows.Take(25)) {
                var context = await RecordContextAsync(actor, authority, tx, row["eventId"].AsInt64, B.ReadPermission, token);
                await FenceAsync(context, token); result.Add(View(context.History!.Latest));
            }
            return new InvoiceReleasePage(result.ToImmutable(), rows.Count > 25 ? new(rows[24]["_id"].AsInt64) : null);
        }, ct);
    }
    private async Task FenceScopesAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        IEnumerable<ResourceScope> resources, string permission, CancellationToken ct) {
        var scopes = resources.ToArray(); var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query);
        if (scopes.Any(scope => !query.Matches(scope))) throw new AuthorizationDeniedException("scope_denied");
        var clauses = query.Clauses.Where(clause => scopes.Any(clause.Matches)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
            scopes.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory)).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, permission, clock.UtcNow));
    }
}
