using MongoDB.Bson;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Organizations;

internal static class OrganizationAddressBson
{
    internal const string Heads = "organizationAddressHeads", Proposals = "organizationAddressProposals",
        Decisions = "organizationAddressDecisions", Versions = "organizationAddressVersions", Prefix = "organization.address.";
    internal static string Purpose(OrganizationAddressPurpose purpose) => purpose switch {
        OrganizationAddressPurpose.LegalBilling => "legal_billing", OrganizationAddressPurpose.Delivery => "delivery",
        OrganizationAddressPurpose.Dispatch => "dispatch", _ => throw OrganizationAddressPolicy.Invalid() };
    internal static OrganizationAddressPurpose Purpose(BsonValue value) => value.AsString switch {
        "legal_billing" => OrganizationAddressPurpose.LegalBilling, "delivery" => OrganizationAddressPurpose.Delivery,
        "dispatch" => OrganizationAddressPurpose.Dispatch, _ => throw Broken() };
    internal static string Decision(OrganizationAddressDecision value) => value switch {
        OrganizationAddressDecision.Approve => "approve", OrganizationAddressDecision.Reject => "reject", _ => throw OrganizationAddressPolicy.Invalid() };
    internal static OrganizationAddressDecision Decision(BsonValue value) => value.AsString switch {
        "approve" => OrganizationAddressDecision.Approve, "reject" => OrganizationAddressDecision.Reject, _ => throw Broken() };
    internal static string Kind(ScopeNodeKind kind) => AuthorizationBson.Name(kind);
    internal static ScopeNodeKind Kind(BsonValue value) => Enum.TryParse<ScopeNodeKind>(value.AsString, true, out var result) && Kind(result) == value.AsString ? result : throw Broken();
    internal static BsonDocument Target(OrganizationAddressTarget target) => new() { { "kind", Kind(target.Kind) }, { "id", target.Id } };
    internal static OrganizationAddressTarget Target(BsonValue row) => new(Kind(row["kind"]), row["id"].AsInt64);
    internal static BsonDocument Details(PostalAddress value) { value = OrganizationAddressPolicy.Copy(value); return new() {
        { "line1", value.Line1 }, { "line2", value.Line2 is null ? BsonNull.Value : value.Line2 }, { "city", value.City },
        { "region", value.Region }, { "postalCode", value.PostalCode }, { "countryCode", value.CountryCode } }; }
    internal static PostalAddress Details(BsonValue row) => OrganizationAddressPolicy.Copy(new PostalAddress(row["line1"].AsString,
        row["line2"].IsBsonNull ? null : row["line2"].AsString, row["city"].AsString, row["region"].AsString, row["postalCode"].AsString, row["countryCode"].AsString));
    internal static BsonDocument Input(OrganizationAddressInput value) { value = OrganizationAddressPolicy.Copy(value); return new() {
        { "target", Target(value.Target) }, { "purpose", Purpose(value.Purpose) }, { "expectedScopeVersion", value.ExpectedScopeVersion },
        { "expectedProfileVersion", value.ExpectedProfileVersion }, { "expectedAddressVersion", value.ExpectedAddressVersion }, { "address", Details(value.Address) } }; }
    internal static OrganizationAddressInput Input(BsonValue row) => OrganizationAddressPolicy.Copy(new OrganizationAddressInput(Target(row["target"]), Purpose(row["purpose"]),
        row["expectedScopeVersion"].AsInt64, row["expectedProfileVersion"].AsInt64, row["expectedAddressVersion"].AsInt64, Details(row["address"])));
    internal static BsonDocument DecisionInput(long proposalId, long revision, OrganizationAddressDecision decision) => new() {
        { "proposalId", proposalId }, { "expectedRevision", revision }, { "decision", Decision(decision) } };
    internal static object ProposeBody(OrganizationAddressInput input, string binding) => new { input = OrganizationAddressPolicy.Copy(input), binding };
    internal static object DecideBody(long proposalId, long revision, OrganizationAddressDecision decision, string binding) => new {
        input = new { proposalId, expectedRevision = revision, decision = Decision(decision) }, binding };
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(p => p.Name is not ("snapshotHash" or "fence"))));
    internal static string Binding(OrganizationAddressInput input, BsonDocument source, BsonDocument? head) => Hash(new() {
        { "input", Input(input) }, { "sourceHash", source["snapshotHash"] }, { "addressId", head is null ? BsonNull.Value : head["_id"] },
        { "addressVersion", head is null ? 0L : head["version"] }, { "versionId", head is null ? BsonNull.Value : head["versionId"] } });
    internal static string DecisionBinding(BsonDocument proposal, OrganizationAddressDecision decision) => Hash(new() {
        { "proposalId", proposal["_id"] }, { "proposalHash", proposal["snapshotHash"] }, { "sourceHash", proposal["source"]["snapshotHash"] },
        { "baseVersion", proposal["baseVersion"] }, { "decision", Decision(decision) } });
    internal static BsonDocument Person(PersonEvidence value) => new() { { "userId", value.UserId }, { "personId", value.PersonId is { } id ? new BsonInt64(id) : BsonNull.Value },
        { "verification", value.Verification.ToString() }, { "version", value.Version } };
    internal static PersonEvidence Person(BsonValue row) => new(row["userId"].AsInt64, row["personId"].IsBsonNull ? null : row["personId"].AsInt64,
        Enum.Parse<PersonVerification>(row["verification"].AsString), row["version"].AsInt64);
    internal static string Permission(string operation) => operation switch {
        Prefix + "propose" => Permissions.TechnicalConfigure, Prefix + "approve" or Prefix + "reject" => Permissions.OrganizationApprove,
        _ => throw OrganizationAddressPolicy.Invalid() };
    internal static void Metadata(BsonDocument row, string operation, long auditId, ActorContext actor, AuthorizationCommandMetadata metadata, object body, DateTimeOffset now) {
        row.Add("operation", operation); row.Add("actorId", actor.UserId); row.Add("actorGrantVersion", actor.GrantVersion); row.Add("recordedAt", now.UtcDateTime);
        row.Add("requestKey", metadata.IdempotencyKey); row.Add("bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason));
        row.Add("reason", metadata.Reason); row.Add("correlationId", metadata.CorrelationId); row.Add("auditId", auditId); row.Add("snapshotHash", Hash(row));
    }
    internal static BsonDocument Result(BsonDocument row) => new() { { "addressId", row["addressId"] }, { "proposalId", row["proposalId"] },
        { "recordId", row["_id"] }, { "operation", row["operation"] }, { "addressVersion", row.GetValue("addressVersion", BsonNull.Value) },
        { "versionId", row.GetValue("versionId", BsonNull.Value) }, { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", row["operation"] },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] },
        { "requestKey", row["requestKey"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => Prefix + "command:" + row["auditId"].AsInt64;
    internal static void RequireBinding(string binding) { if (binding is not { Length: 64 } || binding.Any(c => !char.IsAsciiHexDigitLower(c))) throw OrganizationAddressPolicy.Invalid(); }
    internal static PersistenceConflictException Broken() => new("organization_address_integrity_unavailable", "The complete approved postal address history is unavailable");
    internal static PersistenceConflictException Changed() => new("organization_address_changed", "Reload the current profile, address and proposal before continuing");
}
