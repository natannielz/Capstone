using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Procurement.SupplierClaimBson;
using Q = UnitToko.Platform.Procurement.ProcurementQuarantineDispositionBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class SupplierClaimService
{
    private async Task RequireNewRequestAsync(Context context, SupplierClaimInput input, CancellationToken ct) {
        if (input.BusinessDate > AccountingCalendar.BusinessDate(Now)) throw SupplierClaimRules.Invalid();
        var duplicate = new BsonDocument { { "location", R.Location(input.Location) }, { "supplierId", context.Order!.Source["proposal"]["input"]["supplierId"] },
            { "orderId", input.OrderId }, { "claimReference", input.ClaimReference } };
        if (await Rows(B.Requests).Find(context.Tx, duplicate).AnyAsync(ct)) throw B.Changed("supplier_claim_reference_exists");
        for (var index = 0; index < input.Lines.Length; index++) {
            var line = input.Lines[index]; var capacity = context.Capacities[index];
            if (line.Packages > capacity.Capacity["remainingPackages"].AsInt64) throw B.Changed("supplier_claim_capacity_exceeded");
            var condition = line.Issue switch { "damaged" => "damagedPackages", "expired" => "expiredPackages", "opened" => "openedPackages", _ => null };
            if (condition is not null && capacity.IssuePackages.GetValueOrDefault(line.Issue) + line.Packages > context.Sources!.Lines[index]["inspection"]["line"][condition].AsInt32)
                throw B.Changed("supplier_claim_condition_exceeded");
        }
        Current(context);
    }
    public async Task<BsonDocument> PreviewAsync(ActorContext actor, SupplierClaimInput input, CancellationToken ct = default) {
        input = SupplierClaimRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await OrderContextAsync(actor, authority, tx, input.Location, input.OrderId, input.OrderVersion, input.OrderApprovalId, Permissions.SupplierClaim, token);
            await AddSourcesAsync(context, input, token); await RequireNewRequestAsync(context, input, token);
            var result = new BsonDocument { { "input", B.Input(input) }, { "order", PurchaseOrderService.FundingOrderView(context.Order!.Source) }, { "lines", context.Sources!.Lines },
                { "capacity", context.Capacity }, { "binding", B.Binding(actor.UserId, actor.GrantVersion, input, context.Order.Source, context.Sources.Lines, context.Capacity) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> RequestAsync(ActorContext actor, SupplierClaimInput input, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = SupplierClaimRules.Copy(input); Binding(binding); Metadata(metadata); if (metadata.Reason != input.Reason) throw SupplierClaimRules.Invalid(); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Requests, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value; var body = B.RequestBody(input, binding); Context? context = null;
        return await authorization.ExecuteCommandAsync(actor, B.Prefix + "request", metadata, body, async (authority, tx, token) => {
            _ = Query(actor, authority, Permissions.SupplierClaim); var existing = await records.ForKeyAsync(tx, actor.UserId, B.Prefix + "request", metadata.IdempotencyKey, token);
            if (existing is not null) context = await ClaimContextAsync(actor, authority, tx, existing["_id"].AsInt64, Permissions.SupplierClaim, token);
            else {
                context = await OrderContextAsync(actor, authority, tx, input.Location, input.OrderId, input.OrderVersion, input.OrderApprovalId, Permissions.SupplierClaim, token);
                await AddSourcesAsync(context, input, token); await RequireNewRequestAsync(context, input, token);
                SupplierClaimRecords.Equal(binding, B.Binding(actor.UserId, actor.GrantVersion, input, context.Order!.Source, context.Sources!.Lines, context.Capacity));
            }
            return Command(context);
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); Current(active); var allocations = new BsonArray();
            for (var index = 0; index < input.Lines.Length; index++) {
                var line = input.Lines[index]; var capacity = active.Capacities[index]; var before = capacity.Capacity;
                var headId = capacity.Head?["_id"].AsInt64 ?? (await ids.NextAsync(Q.Heads(line.Basis), token)).Value;
                var allocationId = (await ids.NextAsync(Q.Allocations(line.Basis), token)).Value;
                allocations.Add(new BsonDocument { { "basis", B.Basis(line.Basis) }, { "allocationId", allocationId }, { "headId", headId }, { "inspectionId", line.InspectionId },
                    { "inspectionLineNumber", line.InspectionLineNumber }, { "inspectionHash", before["inspectionHash"] }, { "packages", line.Packages }, { "before", before },
                    { "after", Q.After(before, headId, line.Packages) }, { "previousAllocationId", capacity.Head?["lastAllocationId"] ?? BsonNull.Value }, { "previousHash", capacity.Head?["lastHash"] ?? BsonNull.Value } });
            }
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "claimId", id }, { "version", 1L }, { "location", R.Location(input.Location) }, { "orderId", input.OrderId },
                { "supplierId", active.Order!.Source["proposal"]["input"]["supplierId"] }, { "claimReference", input.ClaimReference }, { "input", B.Input(input) }, { "source", active.Order.Source },
                { "lines", active.Sources!.Lines }, { "capacity", active.Capacity }, { "allocations", allocations }, { "binding", binding }, { "people", new BsonArray(active.People!.Evidence.OrderBy(person => person.UserId).Select(R.Person)) } };
            R.Metadata(row, B.Prefix + "request", audit, actor, metadata, body, Now);
            await Rows(B.Requests).InsertOneAsync(tx, row, cancellationToken: token);
            for (var index = 0; index < input.Lines.Length; index++) {
                var capture = active.Capacities[index]; var allocation = Q.Allocation(row, index); var head = ProcurementQuarantineDisposition.Head(capture, allocation);
                await Rows(Q.Allocations(capture.Basis)).InsertOneAsync(tx, allocation, cancellationToken: token);
                if (capture.Head is null) await Rows(Q.Heads(capture.Basis)).InsertOneAsync(tx, head, cancellationToken: token);
                else if ((await Rows(Q.Heads(capture.Basis)).ReplaceOneAsync(tx, capture.Head, head, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
                active.Fences.RemoveAll(value => value.Table == Q.Heads(capture.Basis) && value.Row["_id"] == head["_id"]); AddFence(active, Q.Heads(capture.Basis), head);
            }
            await EffectsAsync(tx, row, token); active.Request = row; return B.Result(row);
        }, ct, async (authority, tx, token) => {
            var active = context ?? throw B.Broken(); var actual = await records.RequestAsync(tx, active.Request!["_id"].AsInt64, token);
            SupplierClaimRecords.Equal(actual["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason));
            await AddSourcesAsync(active, B.Input(actual["input"]), token); await records.OrphansAsync(tx, token); await FenceAsync(active, token);
        }, validateCurrent: () => Current(context ?? throw B.Broken()));
    }
    public async Task<BsonDocument> PreviewDecisionAsync(ActorContext actor, long claimId, SupplierClaimDecision decision, CancellationToken ct = default) {
        if (claimId <= 0 || !Enum.IsDefined(decision)) throw SupplierClaimRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ClaimContextAsync(actor, authority, tx, claimId, Permissions.SupplierClaimApprove, token);
            if (context.Decision is not null) throw B.Changed("supplier_claim_already_decided");
            var result = new BsonDocument { { "request", B.RequestView(context.Request!) }, { "decision", B.Decision(decision) }, { "capacity", context.Capacity },
                { "binding", B.DecisionBinding(actor.UserId, actor.GrantVersion, context.Request!, decision, context.Capacity) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> DecideAsync(ActorContext actor, long claimId, long expectedVersion, SupplierClaimDecisionInput input, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (claimId <= 0 || expectedVersion != 1) throw SupplierClaimRules.Invalid(); input = SupplierClaimRules.Copy(input); Binding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Decisions, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value; var operation = B.Prefix + B.Decision(input.Decision);
        var body = B.DecisionBody(claimId, expectedVersion, input, binding); Context? context = null;
        return await authorization.ExecuteCommandAsync(actor, operation, metadata, body, async (authority, tx, token) => {
            context = await ClaimContextAsync(actor, authority, tx, claimId, Permissions.SupplierClaimApprove, token); return Command(context);
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); Current(active); if (active.Decision is not null) throw B.Changed("supplier_claim_already_decided");
            SupplierClaimRecords.Equal(binding, B.DecisionBinding(actor.UserId, actor.GrantVersion, active.Request!, input.Decision, active.Capacity));
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "claimId", claimId }, { "version", 1L }, { "location", active.Request!["location"] },
                { "orderId", active.Request["orderId"] }, { "supplierId", active.Request["supplierId"] }, { "requestHash", active.Request["snapshotHash"] }, { "decision", B.Decision(input.Decision) },
                { "input", B.DecisionInput(input) }, { "capacity", active.Capacity }, { "binding", binding }, { "people", new BsonArray(active.People!.Evidence.OrderBy(person => person.UserId).Select(R.Person)) } };
            R.Metadata(row, operation, audit, actor, metadata, body, Now); await Rows(B.Decisions).InsertOneAsync(tx, row, cancellationToken: token); await EffectsAsync(tx, row, token);
            active.Decision = row; return B.Result(row);
        }, ct, async (authority, tx, token) => {
            var active = context ?? throw B.Broken(); var decision = await records.DecisionAsync(tx, active.Request!, token) ?? throw B.Broken();
            SupplierClaimRecords.Equal(decision["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason)); SupplierClaimRecords.Equal(decision["actorId"], actor.UserId); SupplierClaimRecords.Equal(decision["requestKey"], metadata.IdempotencyKey);
            await records.OrphansAsync(tx, token); await FenceAsync(active, token);
        }, validateCurrent: () => Current(context ?? throw B.Broken()));
    }
}
