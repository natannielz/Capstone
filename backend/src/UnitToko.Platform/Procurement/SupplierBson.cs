using MongoDB.Bson;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Organizations;

namespace UnitToko.Platform.Procurement;

internal static class SupplierBson
{
    internal const string Heads = "supplierHeads", Proposals = "supplierProposals", Observations = "supplierObservations", Decisions = "supplierDecisions",
        Versions = "supplierVersions", Namespaces = "supplierSourceNamespaces", Policies = "supplierVerificationPolicies", Prefix = "procurement.supplier.";
    internal static readonly string[] SourceNames = ["organization", "shop", "channel"];
    internal static BsonValue Text(string? value) => value is null ? BsonNull.Value : value;
    internal static string? Text(BsonValue value) => value.IsBsonNull ? null : value.AsString;
    internal static BsonDocument Owner(SupplierOwner value) { SupplierRules.Owner(value); return new() { { "organizationId", value.OrganizationId }, { "shopId", value.ShopId }, { "channelId", value.ChannelId } }; }
    internal static SupplierOwner Owner(BsonValue value) => SupplierRules.Owner(new(value["organizationId"].AsInt64, value["shopId"].AsInt64, value["channelId"].AsInt64));
    internal static BsonDocument Details(SupplierDetails value) { value = SupplierRules.Details(value); return new() {
        { "subjectReference", value.SubjectReference }, { "legalName", value.LegalName }, { "displayName", value.DisplayName },
        { "registrationNamespace", Text(value.RegistrationNamespace) }, { "registrationValue", Text(value.RegistrationValue) }, { "address", value.Address },
        { "contactName", value.ContactName }, { "contactMethod", value.ContactMethod }, { "contactValue", value.ContactValue } }; }
    internal static SupplierDetails Details(BsonValue row) => SupplierRules.Details(new(row["subjectReference"].AsString, row["legalName"].AsString, row["displayName"].AsString,
        Text(row["registrationNamespace"]), Text(row["registrationValue"]), row["address"].AsString, row["contactName"].AsString, row["contactMethod"].AsString, row["contactValue"].AsString));
    internal static BsonDocument Mapping(SupplierMappingInput value) => new() { { "location", ProcurementRequestBson.Location(value.Location) },
        { "sourceSystem", value.SourceSystem }, { "sourceAccountReference", value.SourceAccountReference }, { "externalItemCode", value.ExternalItemCode },
        { "skuId", value.SkuId }, { "expectedSkuRevision", value.ExpectedSkuRevision }, { "definitionId", value.DefinitionId }, { "definitionVersion", value.DefinitionVersion },
        { "validFrom", value.ValidFrom.UtcDateTime }, { "validUntil", value.ValidUntil.UtcDateTime } };
    internal static SupplierMappingInput Mapping(BsonValue row) => new(ProcurementRequestBson.Location(row["location"]), row["sourceSystem"].AsString,
        row["sourceAccountReference"].AsString, row["externalItemCode"].AsString, row["skuId"].AsInt64, row["expectedSkuRevision"].AsInt64,
        row["definitionId"].AsInt64, row["definitionVersion"].AsInt64, new(row["validFrom"].ToUniversalTime()), new(row["validUntil"].ToUniversalTime()));
    internal static BsonDocument Input(SupplierChangeInput value) { value = SupplierRules.Copy(value); return new() { { "owner", Owner(value.Owner) },
        { "supplierId", value.SupplierId is { } id ? new BsonInt64(id) : BsonNull.Value }, { "baseVersion", value.BaseVersion }, { "policyId", value.PolicyId }, { "policyRevision", value.PolicyRevision },
        { "details", Details(value.Details) }, { "mappings", new BsonArray(value.Mappings.Select(Mapping)) } }; }
    internal static SupplierChangeInput Input(BsonValue row) => SupplierRules.Copy(new(Owner(row["owner"]), row["supplierId"].IsBsonNull ? null : row["supplierId"].AsInt64,
        row["baseVersion"].AsInt64, row["policyId"].AsInt64, row["policyRevision"].AsInt64, Details(row["details"]), [.. row["mappings"].AsBsonArray.Select(Mapping)]));
    internal static string Permission(SupplierAccess access) => access switch { SupplierAccess.Propose => Permissions.SupplierChangePropose, SupplierAccess.Approve => Permissions.SupplierChangeApprove, _ => throw SupplierRules.Invalid() };
    internal static string Permission(string operation) => operation switch { Prefix + "propose" => Permissions.SupplierChangePropose,
        Prefix + "observe" or Prefix + "approve" or Prefix + "reject" => Permissions.SupplierChangeApprove, _ => throw SupplierRules.Invalid() };
    internal static string Decision(SupplierDecision value) => value switch { SupplierDecision.Approve => "approve", SupplierDecision.Reject => "reject", _ => throw SupplierRules.Invalid() };
    internal static SupplierDecision Decision(BsonValue row) => row.AsString switch { "approve" => SupplierDecision.Approve, "reject" => SupplierDecision.Reject, _ => throw Broken() };
    internal static BsonDocument Person(PersonEvidence value) => OrganizationAddressBson.Person(value);
    internal static PersonEvidence Person(BsonValue row) => OrganizationAddressBson.Person(row);
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name is not ("snapshotHash" or "fence"))));
    internal static BsonDocument PolicySnapshot(BsonDocument row) => new(row.Where(field => field.Name != "fence"));
    internal static object ProposeBody(SupplierChangeInput input, string binding) => new { input = SupplierRules.Copy(input), binding };
    internal static object ObserveBody(long proposalId, long expectedVersion, SupplierDetails observed, long policyId, long policyRevision, string trustedChannelReference, string sourceReference, bool attested) =>
        new { proposalId, expectedVersion, observed, policyId, policyRevision, trustedChannelReference, sourceReference, attested };
    internal static object DecideBody(long proposalId, long expectedVersion, SupplierDecision decision, long? observationId, string binding) => new { proposalId, expectedVersion, decision = Decision(decision), observationId, binding };
    internal static string Binding(ActorContext actor, SupplierChangeInput input, BsonDocument sources, BsonArray mappings, BsonDocument policy) => Hash(new() {
        { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "input", Input(input) }, { "sources", sources }, { "mappingSources", mappings }, { "policy", PolicySnapshot(policy) } });
    internal static string Binding(long actor, long grantVersion, SupplierChangeInput input, BsonDocument sources, BsonArray mappings, BsonDocument policy) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grantVersion }, { "input", Input(input) }, { "sources", sources }, { "mappingSources", mappings }, { "policy", PolicySnapshot(policy) } });
    internal static string DecisionBinding(long actor, long grantVersion, BsonDocument proposal, SupplierDecision decision, BsonDocument? observation, BsonDocument sources,
        BsonArray mappings, BsonDocument? policy, long currentVersion) => Hash(new() { { "actorId", actor }, { "actorGrantVersion", grantVersion }, { "proposalHash", proposal["snapshotHash"] },
            { "decision", Decision(decision) }, { "observationHash", observation?["snapshotHash"] ?? BsonNull.Value }, { "sources", sources }, { "mappingSources", mappings },
            { "policy", policy is null ? BsonNull.Value : PolicySnapshot(policy) }, { "currentVersion", currentVersion } });
    internal static void Metadata(BsonDocument row, string operation, long auditId, ActorContext actor, AuthorizationCommandMetadata metadata, object body, DateTimeOffset now) {
        row.Add("operation", operation); row.Add("actorId", actor.UserId); row.Add("actorGrantVersion", actor.GrantVersion); row.Add("recordedAt", now.UtcDateTime);
        row.Add("requestKey", metadata.IdempotencyKey); row.Add("bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason)); row.Add("reason", metadata.Reason);
        row.Add("correlationId", metadata.CorrelationId); row.Add("auditId", auditId); row.Add("snapshotHash", Hash(row));
    }
    internal static BsonDocument Result(BsonDocument row) => new() { { "supplierId", row["supplierId"] }, { "proposalId", row["proposalId"] }, { "proposalVersion", 1L },
        { "recordId", row["_id"] }, { "operation", row["operation"] }, { "versionId", row.GetValue("versionId", BsonNull.Value) }, { "version", row.GetValue("supplierVersion", BsonNull.Value) } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", row["operation"] },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] },
        { "requestKey", row["requestKey"] }, { "owner", row["owner"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => Prefix + "command:" + row["auditId"].AsInt64;
    internal static BsonDocument SourceView(BsonDocument source) => new(SourceNames.Select(name => new BsonElement(name, ProcurementRequestBson.Label(source[name]))));
    internal static BsonArray MappingView(BsonArray mappings) => new(mappings.Select(value => {
        var row = new BsonDocument("location", value["location"]);
        foreach (var part in ProcurementRequestBson.SourceNames) row.Add(part, ProcurementRequestBson.Label(value["sources"][part]));
        row.Add("package", ProcurementRequestBson.Package(value["catalog"])); return row;
    }));
    internal static BsonDocument PolicyView(BsonDocument row) { var result = PolicySnapshot(row); result.Remove("schemaVersion"); result.Remove("_id"); result.Remove("scope");
        result.Add("id", row["_id"]); result.Add("owner", row["scope"]); return result; }
    internal static BsonDocument Namespace(SupplierOwner owner, SupplierMappingInput mapping) => new() { { "owner", Owner(owner) }, { "sourceSystem", mapping.SourceSystem },
        { "sourceAccountReference", mapping.SourceAccountReference }, { "externalItemCode", mapping.ExternalItemCode } };
    internal static void RequireBinding(string binding) { if (binding is not { Length: 64 } || binding.Any(c => !char.IsAsciiHexDigitLower(c))) throw SupplierRules.Invalid(); }
    internal static PersistenceConflictException Broken() => new("supplier_integrity_unavailable", "The complete supplier history is unavailable");
    internal static PersistenceConflictException Changed() => new("supplier_changed", "Reload the supplier, policy and catalog before continuing");
    internal static PersistenceConflictException Conflict() => new("supplier_mapping_conflict", "A source code already has an overlapping approved mapping");
}
