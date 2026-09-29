using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Catalog;

internal static class CatalogPriceBson
{
    internal const string Proposals = "catalogPriceProposals", Decisions = "catalogPriceDecisions",
        Versions = "priceVersions", Heads = "catalogPriceHeads", Prefix = "catalog.price.";
    internal static AuthorizationDeniedException Invalid(string code = "catalog_price_unavailable") => new(code);
    internal static string Kind(string collection) => collection switch {
        Proposals => Prefix + "proposal", Decisions => Prefix + "decision", Versions => Prefix + "version",
        Heads => Prefix + "head", _ => throw Invalid() };
    internal static BsonDocument Header(string collection, long id) => new() {
        { "_id", id }, { "schemaVersion", 1 }, { "kind", Kind(collection) } };
    internal static void Exact(BsonDocument row, params string[] names)
    {
        if (row is null || row.ElementCount != names.Length || names.Any(name => !row.Contains(name))) throw Invalid();
    }
    internal static bool Positive(BsonDocument row, string name) =>
        row.TryGetValue(name, out var value) && value.IsInt64 && value.AsInt64 > 0;
    internal static bool Nonnegative(BsonDocument row, string name) =>
        row.TryGetValue(name, out var value) && value.IsInt64 && value.AsInt64 >= 0;
    internal static void Guard(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidCastException or KeyNotFoundException or ArgumentException or
            FormatException or OverflowException or DomainValidationException) { throw Invalid(); }
    }
    internal static DateTimeOffset Time(BsonValue value) => CatalogRepository.Date(value);
    internal static bool Text(string text, int maximum)
    {
        try { return CatalogAuthoringPolicy.Text(text, maximum) == text; }
        catch (DomainValidationException) { return false; }
    }
    internal static BsonDocument Input(CatalogPriceInput input)
    {
        CatalogPricePolicy.Validate(input);
        return new() { { "currency", input.Currency }, { "unitPrice", new BsonDecimal128(input.UnitPrice.Value) } };
    }
    internal static CatalogPriceInput Input(BsonDocument row)
    {
        CatalogPriceInput? result = null;
        Guard(() => {
            Exact(row, "currency", "unitPrice");
            result = new(row["currency"].AsString, Amount(row["unitPrice"]));
            CatalogPricePolicy.Validate(result);
        });
        return result!;
    }
    private static Money Amount(BsonValue value)
    {
        if (!value.IsDecimal128) throw Invalid();
        var raw = value.AsDecimal128;
        // Decimal128 conversion can round or underflow. Reject any changed mathematical value.
        var amount = Decimal128.ToDecimal(raw);
        if (new Decimal128(amount) != raw) throw Invalid();
        return new(amount);
    }
    internal static BsonDocument Expected(CatalogPriceExpectation expected) => new() {
        { "skuRevision", expected.SkuRevision }, { "definitionId", expected.DefinitionId.Value },
        { "definitionVersion", expected.DefinitionVersion }, { "priceVersion", expected.PriceVersion } };
    internal static CatalogPriceExpectation Expected(BsonDocument row)
    {
        Exact(row, "skuRevision", "definitionId", "definitionVersion", "priceVersion");
        if (!Positive(row, "skuRevision") || !Positive(row, "definitionId") ||
            !Positive(row, "definitionVersion") || !Nonnegative(row, "priceVersion") || row["priceVersion"] == long.MaxValue) throw Invalid();
        return new(row["skuRevision"].AsInt64, new(row["definitionId"].AsInt64), row["definitionVersion"].AsInt64, row["priceVersion"].AsInt64);
    }
    internal static BsonDocument Fact(PriceVersion price)
    {
        CatalogPricePolicy.Validate(price);
        return new() { { "_id", price.Id.Value }, { "skuId", price.SkuId.Value }, { "version", price.Version },
            { "currency", price.Currency }, { "unitPrice", new BsonDecimal128(price.UnitPrice.Value) }, { "effectiveAt", price.EffectiveAt.UtcDateTime } };
    }
    internal static PriceVersion Price(BsonDocument row, bool fact = false)
    {
        PriceVersion? result = null;
        Guard(() => {
            if (fact) Exact(row, "_id", "skuId", "version", "currency", "unitPrice", "effectiveAt");
            else {
                Exact(row, "_id", "schemaVersion", "kind", "skuId", "version", "currency", "unitPrice", "effectiveAt", "proposalId", "decisionId");
                RequireHeader(row, Versions);
                if (!Positive(row, "proposalId") || !Positive(row, "decisionId")) throw Invalid();
            }
            if (!Positive(row, "_id") || !Positive(row, "skuId") || !Positive(row, "version")) throw Invalid();
            result = new(new(row["_id"].AsInt64), new(row["skuId"].AsInt64), row["version"].AsInt64,
                row["currency"].AsString, Amount(row["unitPrice"]), Time(row["effectiveAt"]));
            CatalogPricePolicy.Validate(result);
        });
        return result!;
    }
    internal static void Metadata(BsonDocument row, long auditId, ActorContext actor, AuthorizationCommandMetadata metadata, string hash)
    {
        row.Add("auditId", auditId); row.Add("actorGrantVersion", actor.GrantVersion);
        row.Add("reason", metadata.Reason); row.Add("correlationId", metadata.CorrelationId);
        row.Add("requestKey", metadata.IdempotencyKey); row.Add("bodyHash", hash);
    }
    private static void RequireMetadata(BsonDocument row)
    {
        if (!Positive(row, "auditId") || !Positive(row, "actorGrantVersion") || !Text(row["reason"].AsString, 1000) ||
            !Text(row["correlationId"].AsString, 128) || !CatalogReceiptRules.Key(row["requestKey"].AsString) ||
            row["bodyHash"].AsString.Length != 64 || row["bodyHash"].AsString.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw Invalid();
    }
    private static void RequireHeader(BsonDocument row, string collection)
    {
        if (!Positive(row, "_id") || !row["schemaVersion"].IsInt32 || row["schemaVersion"] != 1 ||
            !row["kind"].IsString || row["kind"] != Kind(collection)) throw Invalid();
    }
    internal static void Proposal(BsonDocument row) => Guard(() => {
        Exact(row, "_id", "schemaVersion", "kind", "skuId", "skuSnapshot", "definition", "expected", "basePrice", "input",
            "revision", "proposedBy", "proposedAt", "auditId", "actorGrantVersion", "reason", "correlationId", "requestKey", "bodyHash");
        RequireHeader(row, Proposals); RequireMetadata(row);
        if (!Positive(row, "skuId") || !Positive(row, "revision") || row["revision"] != 1L || !Positive(row, "proposedBy")) throw Invalid();
        var sku = row["skuSnapshot"].AsBsonDocument; var definition = row["definition"].AsBsonDocument;
        CatalogRepository.Require("skus", sku); CatalogRepository.Require("variantDefinitions", definition);
        CatalogAuthoringService.RequirePair(sku, definition);
        if (sku["_id"] != row["skuId"] || !sku["active"].AsBoolean) throw Invalid();
        var current = row["basePrice"].IsBsonNull ? null : Price(row["basePrice"].AsBsonDocument, true);
        var expected = Expected(row["expected"].AsBsonDocument);
        var input = Input(row["input"].AsBsonDocument);
        // Validate the reviewed candidate without allocating its eventual approved identity.
        var candidate = new LongId(current?.Id.Value == 1 ? 2 : 1);
        CatalogPricePolicy.Approve(CatalogRepository.Sku(sku), CatalogRepository.Definition(definition), current,
            expected, input, candidate, Time(row["proposedAt"]));
        if (row["bodyHash"] != ProposalHash(row)) throw Invalid();
    });
    internal static string ProposalHash(BsonDocument row) => AuthorizationStore.CommandHash(new {
        skuId = row["skuId"].AsInt64, expected = Expected(row["expected"].AsBsonDocument), input = Input(row["input"].AsBsonDocument)
    }, row["reason"].AsString);
    internal static string Name(CatalogPriceDecision decision) => decision switch {
        CatalogPriceDecision.Approve => "approve", CatalogPriceDecision.Reject => "reject", CatalogPriceDecision.Withdraw => "withdraw", _ => throw Invalid() };
    internal static CatalogPriceDecision DecisionKind(string name) => name switch {
        "approve" => CatalogPriceDecision.Approve, "reject" => CatalogPriceDecision.Reject, "withdraw" => CatalogPriceDecision.Withdraw, _ => throw Invalid() };
    internal static void Decision(BsonDocument row, BsonDocument proposal) => Guard(() => {
        Exact(row, "_id", "schemaVersion", "kind", "skuId", "proposalId", "revision", "decision", "priceId", "priceVersion",
            "decidedBy", "decidedAt", "reviewReference", "auditId", "actorGrantVersion", "reason", "correlationId", "requestKey", "bodyHash");
        RequireHeader(row, Decisions); RequireMetadata(row); Proposal(proposal);
        if (!Positive(row, "skuId") || row["skuId"] != proposal["skuId"] || !Positive(row, "proposalId") ||
            row["proposalId"] != proposal["_id"] || !Positive(row, "revision") || row["revision"] != proposal["revision"] ||
            !Positive(row, "decidedBy") || !Text(row["reviewReference"].AsString, 160) ||
            Time(row["decidedAt"]) < Time(proposal["proposedAt"])) throw Invalid();
        var decision = DecisionKind(row["decision"].AsString);
        if ((decision == CatalogPriceDecision.Withdraw) != (row["decidedBy"] == proposal["proposedBy"])) throw Invalid();
        if (decision == CatalogPriceDecision.Approve ? !Positive(row, "priceId") || !Positive(row, "priceVersion") ||
            row["priceVersion"].AsInt64 != checked(Expected(proposal["expected"].AsBsonDocument).PriceVersion + 1) :
            !row["priceId"].IsBsonNull || !row["priceVersion"].IsBsonNull) throw Invalid();
        if (row["bodyHash"] != DecisionHash(row)) throw Invalid();
    });
    internal static string DecisionHash(BsonDocument row) => AuthorizationStore.CommandHash(new {
        proposalId = row["proposalId"].AsInt64, expectedRevision = row["revision"].AsInt64,
        decision = DecisionKind(row["decision"].AsString), reviewReference = row["reviewReference"].AsString
    }, row["reason"].AsString);
    internal static void Head(BsonDocument row) => Guard(() => {
        Exact(row, "_id", "schemaVersion", "kind", "priceId", "version", "fence");
        RequireHeader(row, Heads);
        if (!Positive(row, "priceId") || !Positive(row, "version") || !Nonnegative(row, "fence")) throw Invalid();
    });
    internal static BsonDocument Result(BsonDocument row) => row["kind"] == Kind(Proposals)
        ? new() { { "proposalId", row["_id"] }, { "skuId", row["skuId"] }, { "revision", row["revision"] } }
        : new() { { "proposalId", row["proposalId"] }, { "decisionId", row["_id"] }, { "skuId", row["skuId"] },
            { "status", row["decision"] }, { "priceId", row["priceId"] }, { "priceVersion", row["priceVersion"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() {
        { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Prefix + "command" },
        { "operation", Operation(row) }, { "actorId", Actor(row) }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "createdAt", At(row) }, { "record", row.DeepClone() }, { "result", Result(row) } };
    internal static string Operation(BsonDocument row) => Prefix + (row["kind"] == Kind(Proposals) ? "propose" : row["decision"].AsString);
    internal static BsonValue Actor(BsonDocument row) => row[row["kind"] == Kind(Proposals) ? "proposedBy" : "decidedBy"];
    internal static BsonValue At(BsonDocument row) => row[row["kind"] == Kind(Proposals) ? "proposedAt" : "decidedAt"];
}
