using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using A = UnitToko.Platform.Procurement.PurchaseArrivalRecords;
using B = UnitToko.Platform.Procurement.PurchaseOrderBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class PurchaseOrderService
{
    private sealed record ArrivalContext(Context Owner, BsonDocument Source, PurchaseOrderLineInput Line,
        BsonDocument Head, IReadOnlyList<BsonDocument> Events, IReadOnlyList<BsonDocument> Audits,
        IReadOnlyList<BsonDocument> Receipts, bool Exists)
    {
        internal BsonDocument? Latest => Events.LastOrDefault();
        internal PersonAuthorityProof? ReviewPeople { get; set; }
    }
    private async Task ArrivalStorageAsync(CancellationToken ct) { await StorageAsync(ct); await A.RequireStorageAsync(database, ct); }
    private async Task<ArrivalContext> ArrivalContextAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long orderId, long skuId, string permission, CancellationToken ct)
    {
        if (orderId <= 0 || skuId <= 0) throw PurchaseArrivalRules.Invalid();
        if (permission is not (Permissions.ProcurementRead or Permissions.ProcurementPrepare or Permissions.ProcurementApprove or Permissions.StockRead)) throw A.Broken();
        _ = Query(actor, authority, permission);
        var route = await Rows(B.Orders).Find(tx, new BsonDocument("_id", orderId)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        var context = await ContextAsync(actor, authority, tx, R.Location(route["location"]), permission, ct);
        var source = await FundingSourceAsync(database, tx, orderId, route["version"].AsInt64, route["approvalId"].AsInt64, ct);
        BindPeople(context, source["proposal"].AsBsonDocument, source["approval"].AsBsonDocument);
        await PeopleAsync(context, source["approval"]["actorId"].AsInt64, source["proposal"]["actorId"].AsInt64, ct);
        var line = B.Input(source["proposal"]["input"]).Lines.SingleOrDefault(line => line.RequestSkuId == skuId) ?? throw PurchaseArrivalRules.Invalid();
        var key = A.Key(orderId, skuId); var head = await Rows(A.Heads).Find(tx, new BsonDocument("_id", key)).SingleOrDefaultAsync(ct);
        var exists = head is not null;
        head ??= new() { { "_id", key }, { "schemaVersion", 1 }, { "location", route["location"].DeepClone() }, { "orderId", orderId },
            { "skuId", skuId }, { "revision", 1L }, { "eventId", BsonNull.Value }, { "fence", 0L } };
        if (head["location"] != route["location"] || head["orderId"] != orderId || head["skuId"] != skuId || head["revision"].AsInt64 <= 0) throw A.Broken();
        var events = await Rows(A.Events).Find(tx, new BsonDocument("headId", key)).Sort(new BsonDocument("revision", 1)).ToListAsync(ct);
        if (head["revision"].AsInt64 != events.Count + 1L || (!exists && events.Count != 0)) throw A.Broken();
        if (await Rows(A.Audits).CountDocumentsAsync(tx, new BsonDocument("headId", key), cancellationToken: ct) != events.Count ||
            await Rows("idempotencyRecords").CountDocumentsAsync(tx, new BsonDocument {
                { "operation", new BsonDocument("$in", new BsonArray { A.Prefix + "propose", A.Prefix + "approve", A.Prefix + "reject" }) },
                { "result.orderId", orderId }, { "result.skuId", skuId } }, cancellationToken: ct) != events.Count) throw A.Broken();
        BsonDocument? previous = null; var audits = new List<BsonDocument>(); var receipts = new List<BsonDocument>();
        for (var index = 0; index < events.Count; index++) {
            var row = events[index]; var input = A.Input(row["input"]); var action = row["action"].AsString;
            if (row["revision"] != index + 2L || row["previousRevision"] != index + 1L || row["location"] != head["location"] ||
                input.OrderId != orderId || input.SkuId != skuId || input.OrderVersion != route["version"].AsInt64 || input.ApprovalId != route["approvalId"].AsInt64 ||
                input.DefinitionId != line.DefinitionId || row["orderHash"] != route["snapshotHash"] || A.Hash(row) != row["eventHash"] ||
                row["bodyHash"] != AuthorizationStore.CommandHash(A.Body(row), row["reason"].AsString) ||
                row["actorPerson"]["userId"] != row["actorId"]) throw A.Broken();
            if (action == "propose") {
                if (row["proposalId"] != row["_id"] || input.ExpectedRevision != index + 1L) throw A.Broken();
            } else {
                if (previous is null || previous["action"] != "propose" || row["proposalId"] != previous["_id"] || row["input"] != previous["input"] ||
                    !PersonIndependencePolicy.AreIndependent(B.Person(row["actorPerson"]), B.Person(previous["actorPerson"]))) throw A.Broken();
            }
            var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", A.Prefix + action }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
            var audit = await Rows(A.Audits).Find(tx, new BsonDocument("_id", row["_id"])).SingleOrDefaultAsync(ct);
            if (receipt is null || receipt["bodyHash"] != row["bodyHash"] || receipt["result"] != A.Result(row) || audit is null || audit != A.Audit(row)) throw A.Broken();
            string[] fields = ["_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result"];
            if (action != "propose") {
                fields = [.. fields, PersonApprovalReceipt.Field];
                if (PersonApprovalReceipt.Read(receipt, Permissions.ProcurementApprove) != new ApprovalRequest(1, 1, RequestedBy: previous!["actorId"].AsInt64)) throw A.Broken();
            }
            if (receipt.ElementCount != fields.Length || fields.Any(field => !receipt.Contains(field)) || !receipt["_id"].IsObjectId ||
                receipt["schemaVersion"] != 1 || !receipt["createdAt"].IsBsonDateTime || receipt["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw A.Broken();
            audits.Add(audit); receipts.Add(receipt);
            previous = row;
        }
        if (previous is null ? !head["eventId"].IsBsonNull : head["eventId"] != previous["_id"]) throw A.Broken();
        return new(context, source, line, head, events, audits, receipts, exists);
    }
    private async Task ArrivalPeopleAsync(ArrivalContext context, BsonDocument proposal, CancellationToken ct)
    {
        var actor = context.Owner.Actor;
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Owner.Tx,
            [.. new[] { actor.UserId, proposal["actorId"].AsInt64 }.Distinct().Order()], ct);
        var maker = proof.Evidence.Single(person => person.UserId == proposal["actorId"].AsInt64);
        var checker = proof.Evidence.Single(person => person.UserId == actor.UserId);
        if (maker.PersonId != B.Person(proposal["actorPerson"]).PersonId ||
            !PersonIndependencePolicy.AreIndependent(checker, maker, B.Person(proposal["actorPerson"])))
            throw new AuthorizationDeniedException("independent_person_required");
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Owner.Tx, proof, ct); context.ReviewPeople = proof;
    }
    private async Task FenceArrivalAsync(ArrivalContext context, CancellationToken ct)
    {
        Current(context.Owner); var tx = context.Owner.Tx;
        foreach (var (name, row) in new[] { (B.Orders, context.Source["order"].AsBsonDocument),
            (B.Proposals, context.Source["proposal"].AsBsonDocument), (B.Decisions, context.Source["approval"].AsBsonDocument) })
            if ((await Rows(name).UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Set("version", row["version"]), cancellationToken: ct)).MatchedCount != 1) throw A.Changed();
        if (context.Exists) {
            if (context.Head["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(A.Heads).UpdateOneAsync(tx, context.Head,
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw A.Changed();
            context.Head["fence"] = checked(context.Head["fence"].AsInt64 + 1);
        } else if (await Rows(A.Heads).Find(tx, new BsonDocument("_id", context.Head["_id"])).AnyAsync(ct)) throw A.Changed();
        await FenceAsync(context.Owner, ct);
        if (context.ReviewPeople is { } proof) { await new PersonAuthorizationFence(database, clock).TouchAsync(tx, proof, ct); proof.RequireCurrent(tx, clock.UtcNow); }
        await A.RequireStorageAsync(database, ct);
        await RequireArrivalMembershipAsync(context, ct);
        _ = Require(context.Owner); Current(context.Owner); ct.ThrowIfCancellationRequested();
    }
    private async Task RequireArrivalMembershipAsync(ArrivalContext context, CancellationToken ct)
    {
        var head = await Rows(A.Heads).Find(context.Owner.Tx, new BsonDocument("_id", context.Head["_id"])).SingleOrDefaultAsync(ct);
        if (context.Exists ? head != context.Head : head is not null) throw A.Changed();
        var receiptQuery = new BsonDocument {
            { "operation", new BsonDocument("$in", new BsonArray { A.Prefix + "propose", A.Prefix + "approve", A.Prefix + "reject" }) },
            { "result.orderId", context.Source["order"]["_id"] }, { "result.skuId", context.Line.RequestSkuId } };
        foreach (var (name, query, captured) in new[] {
            (A.Events, new BsonDocument("headId", context.Head["_id"]), context.Events),
            (A.Audits, new BsonDocument("headId", context.Head["_id"]), context.Audits),
            ("idempotencyRecords", receiptQuery, context.Receipts) }) {
            var current = await Rows(name).Find(context.Owner.Tx, query).ToListAsync(ct);
            if (current.Count != captured.Count || captured.Any(row => !current.Any(value => value == row))) throw A.Broken();
        }
    }
    private static BsonDocument ArrivalView(ArrivalContext context)
    {
        var latest = context.Latest; var pending = latest?["action"] == "propose";
        var input = latest is null ? null : A.Input(latest["input"]);
        var approved = latest?["action"] == "approve";
        return new() { { "orderId", context.Source["order"]["_id"] }, { "orderVersion", context.Source["order"]["version"] },
            { "approvalId", context.Source["order"]["approvalId"] }, { "skuId", context.Line.RequestSkuId }, { "definitionId", context.Line.DefinitionId },
            { "revision", context.Head["revision"] }, { "orderedPackages", context.Line.Packages },
            { "state", latest is null ? "unrecorded" : pending ? "pending" : approved ? "approved" : "rejected" },
            { "proposalId", latest is null ? BsonNull.Value : latest["proposalId"] },
            { "proposedAt", input?.ExpectedAt is { } proposed ? new BsonDateTime(proposed.UtcDateTime) : BsonNull.Value },
            { "expectedAt", approved && input?.ExpectedAt is { } expected ? new BsonDateTime(expected.UtcDateTime) : BsonNull.Value },
            { "confirmationReference", input is null ? BsonNull.Value : input.ConfirmationReference },
            { "reason", latest is null ? BsonNull.Value : latest["reason"] },
            { "recordedAt", latest is null ? BsonNull.Value : latest["recordedAt"] } };
    }
    private static string ArrivalBinding(ArrivalContext context, object body) => B.Hash(new() {
        { "actorId", context.Owner.Actor.UserId }, { "grantVersion", context.Owner.Actor.GrantVersion },
        { "orderHash", context.Source["order"]["snapshotHash"] }, { "revision", context.Head["revision"] },
        { "eventHash", context.Latest is { } latest ? latest["eventHash"] : BsonNull.Value },
        { "body", AuthorizationStore.CommandHash(body, "arrival-preview") } });
    public async Task<BsonDocument> ReadArrivalAsync(ActorContext actor, long orderId, long skuId, CancellationToken ct = default)
    {
        await ArrivalStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ArrivalContextAsync(actor, authority, tx, orderId, skuId, Permissions.ProcurementRead, token);
            var view = ArrivalView(context); await FenceArrivalAsync(context, token); return view;
        }, ct);
    }
    public async Task<BsonDocument> PreviewArrivalAsync(ActorContext actor, PurchaseArrivalInput input, CancellationToken ct = default)
    {
        input = PurchaseArrivalRules.Copy(input); await ArrivalStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ArrivalContextAsync(actor, authority, tx, input.OrderId, input.SkuId, Permissions.ProcurementPrepare, token);
            RequireArrivalInput(context, input); var view = ArrivalView(context);
            view.Add("input", A.Input(input)); view.Add("binding", ArrivalBinding(context, input));
            await FenceArrivalAsync(context, token); return view;
        }, ct);
    }
    private static void RequireArrivalInput(ArrivalContext context, PurchaseArrivalInput input)
    {
        if (input.OrderVersion != context.Source["order"]["version"].AsInt64 || input.ApprovalId != context.Source["order"]["approvalId"].AsInt64 ||
            input.DefinitionId != context.Line.DefinitionId || input.ExpectedRevision != context.Head["revision"].AsInt64) throw A.Changed();
    }
}
