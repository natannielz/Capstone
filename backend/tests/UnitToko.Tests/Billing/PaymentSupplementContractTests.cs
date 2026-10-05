using System.Text.Json;
using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Billing;

public sealed class PaymentSupplementContractTests
{
    [Fact]
    public void Canonical_scalar_reason_accepts_full_bound_and_refuses_truncation_controls_format_and_malformed_surrogates()
    {
        PaymentSupplementRules.Text(string.Concat(Enumerable.Repeat("\U0001f4ce", 1000)), 1000);
        PaymentSupplementRules.Text("Bukti tambahan disetujui untuk ditinjau", 1000);
        foreach (var invalid in new[] { "", " alasan", "alasan ", "e\u0301", "x\n", "x\u200b", "\ud800", "\udc00", "a\ud800z", new string('x', 1001) })
            Assert.Equal("invalid_payment_supplement", Assert.Throws<DomainValidationException>(() => PaymentSupplementRules.Text(invalid, 1000)).Code);
    }
    [Fact]
    public void Association_IDs_respect_reserved_boundary_and_attach_requires_its_own_draft_revision()
    {
        var primary = new PaymentSupplementPrimary(1, 1, long.MaxValue - 2, new(4, 1, new string('a', 64))).Validate();
        foreach (var invalid in new[] { 0L, -1L, long.MaxValue - 1, long.MaxValue })
            Assert.Throws<DomainValidationException>(() => (primary with { SubmissionId = invalid }).Validate());
        foreach (var invalid in new[] { 0L, 2L, long.MaxValue })
            Assert.Throws<DomainValidationException>(() => new PaymentSupplementAttachInput(5, invalid, 6, new(7, 1, new string('b', 64))).Validate());
        Assert.Throws<DomainValidationException>(() => new PaymentSupplementAttachInput(5, 1, 6, new(7, 2, new string('b', 64))).Validate());
        new PaymentSupplementAttachInput(5, 1, 6, new(7, 1, new string('b', 64))).Validate();
    }
    [Fact]
    public void D06_snapshot_requires_three_distinct_owner_decisions_finite_validity_exact_unit_and_historical_time()
    {
        var now = DateTime.UtcNow; var scope = new BsonDocument { { "organizationId", 11L }, { "shopId", 12L }, { "channelId", 13L } };
        var snapshot = Policy(scope, now); PaymentSupplementPolicy.ValidateUse(snapshot, scope, new BsonDateTime(now));
        var duplicate = snapshot.DeepClone().AsBsonDocument; duplicate["approvals"][2]["owner"] = "finance";
        Assert.Throws<PersistenceConflictException>(() => PaymentSupplementPolicy.ValidateSnapshot(duplicate));
        var indefinite = snapshot.DeepClone().AsBsonDocument; indefinite["expiresAt"] = now.AddDays(91);
        Assert.Throws<PersistenceConflictException>(() => PaymentSupplementPolicy.ValidateSnapshot(indefinite));
        Assert.Throws<PersistenceConflictException>(() => PaymentSupplementPolicy.ValidateUse(snapshot, new("organizationId", 99L), new BsonDateTime(now)));
        Assert.Throws<PersistenceConflictException>(() => PaymentSupplementPolicy.ValidateUse(snapshot, scope, new BsonDateTime(now.AddDays(2))));
        var wrongHash = snapshot.DeepClone().AsBsonDocument; wrongHash["decisionHash"] = new string('A', 64);
        Assert.Throws<PersistenceConflictException>(() => PaymentSupplementPolicy.ValidateSnapshot(wrongHash));
    }
    [Fact]
    public void Public_supplement_view_preserves_both_evidence_identities_and_cannot_serialize_private_publication()
    {
        var view = new PaymentSupplementView(1, 1, 2, new(3, 1, new string('a', 64)), 4, 2, "attached", 5,
            new(6, 1, new string('b', 64)), DateTimeOffset.UtcNow, "Lampiran tambahan");
        var json = JsonSerializer.Serialize(view); Assert.Contains("PrimaryEvidence", json); Assert.Contains("PdfEvidence", json);
        foreach (var privateName in new[] { "StageToken", "Original", "Publication", "StoreId", "PinId", "ClaimToken" }) Assert.DoesNotContain(privateName, json);
    }
    [Fact]
    public void Reviewed_schema_extension_preserves_operator_conjuncts_and_never_adopts_unrecognized_bare_definitions()
    {
        var queueBefore = new BsonDocument("$and", new BsonArray { PdfDocumentExtractionSchemas.ExtendedOutboxValidator(),
            new BsonDocument("sourceKey", new BsonDocument("$type", "string")) });
        var queueAfter = PaymentSupplementSchemas.ExtendOutbox(queueBefore);
        Assert.Equal(queueBefore["$and"][1], queueAfter["$and"][1]);
        Assert.True(PaymentSupplementSchemas.SupportsOutbox(queueAfter, PdfDocumentExtractionSchemas.ExtendedOutboxValidator()));
        Assert.Equal(queueAfter, PaymentSupplementSchemas.ExtendOutbox(queueAfter));
        foreach (var name in PdfDocumentExtractionSchemas.Collections) {
            var before = new BsonDocument("$and", new BsonArray { PdfDocumentExtractionSchemas.Validator(name), new BsonDocument("_id", new BsonDocument("$ne", 999L)) });
            var after = PaymentSupplementSchemas.ExtendDocumentOwner(name, before);
            Assert.Equal(before["$and"][1], after["$and"][1]); Assert.Equal(after, PaymentSupplementSchemas.ExtendDocumentOwner(name, after));
            Assert.True(PaymentSupplementSchemas.SupportsDocumentOwner(name, after, PdfDocumentExtractionSchemas.Validator(name)));
        }
        Assert.Throws<InvalidOperationException>(() => PaymentSupplementSchemas.ExtendOutbox(new BsonDocument()));
        Assert.Throws<InvalidOperationException>(() => PaymentSupplementSchemas.ExtendDocumentOwner("pdfDocumentExtractionResults", new BsonDocument()));
    }
    internal static BsonDocument Policy(BsonDocument scope, DateTime now) => new() { { "id", 1L }, { "revision", 1L }, { "scope", scope },
        { "decisionRef", "synthetic-D06-not-production-approval" }, { "decisionHash", new string('c', 64) }, { "maximumSupplements", 2 },
        { "approvedAt", now.AddMinutes(-1) }, { "expiresAt", now.AddDays(1) }, { "approvals", new BsonArray(new[] { "operations", "finance", "data-owner" }
            .Select(owner => new BsonDocument { { "owner", owner }, { "reference", "synthetic-" + owner }, { "approvedAt", now.AddMinutes(-2) } })) } };
}
