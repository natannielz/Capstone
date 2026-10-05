using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Billing;

public sealed class PaymentSupplementEvidenceProtocolTests
{
    [Fact]
    public void Persisted_enum_append_and_exact_own_revision_do_not_adopt_primary_payment_PDF_or_image_admission()
    {
        Assert.Equal(8, (int)EvidenceTargetKind.PaymentSupplement); Assert.Equal(9, (int)EvidencePurpose.PaymentSupplementPdf);
        Assert.Equal(3, (int)EvidenceTargetKind.PaymentDeclaration); Assert.Equal(2, (int)EvidencePurpose.PaymentImage);
        var target = new EvidenceTargetRef(EvidenceTargetKind.PaymentSupplement, 101);
        Assert.Equal(1L, new ReserveEvidencePdf(target, EvidencePurpose.PaymentSupplementPdf, 1).ExpectedTargetRevision);
        Assert.Throws<DomainValidationException>(() => new ReserveEvidencePdf(target, EvidencePurpose.PaymentSupplementPdf, 2));
        Assert.Throws<DomainValidationException>(() => new ReserveEvidencePdf(new(EvidenceTargetKind.PaymentDeclaration, 101), EvidencePurpose.PaymentSupplementPdf, 1));
        Assert.Throws<DomainValidationException>(() => new ReserveEvidencePdf(target, EvidencePurpose.PaymentPdf, 1));
        Assert.Throws<DomainValidationException>(() => new ReserveEvidenceImage(target, EvidencePurpose.PaymentImage, 1));
        Assert.True(EvidencePolicy.CanSubmitPayment("validated", "clean", "image/png", true));
        Assert.False(EvidencePolicy.CanSubmitPayment("validated", "clean", "application/pdf", true));
    }
    [Fact]
    public void Extension_keeps_exact_predecessor_and_operator_conjuncts_and_rejects_an_unknown_weak_union()
    {
        foreach (var name in PaymentSupplementEvidenceSchemas.Collections) {
            var previous = name switch {
                "evidenceReadLeases" => CatalogEvidenceReadSchemas.Validator(name),
                "evidencePins" => SupplierDocumentEvidenceSchemas.Validator(name),
                _ => PdfEvidenceAdmissionSchemas.Validator(name)
            };
            Assert.Equal(previous, PaymentSupplementEvidenceSchemas.PreviousValidator(name));
            var control = new BsonDocument("_id", new BsonDocument("$ne", 777L));
            var installed = new BsonDocument("$and", new BsonArray { previous, control });
            var extended = PaymentSupplementEvidenceSchemas.Extend(name, installed);
            Assert.Equal(control, extended["$and"][1]);
            Assert.Equal(previous, extended["$and"][0]["$or"][0]);
            Assert.True(PaymentSupplementEvidenceSchemas.Supports(name, extended, previous));
            Assert.Equal(extended, PaymentSupplementEvidenceSchemas.Extend(name, extended));
            Assert.Throws<InvalidOperationException>(() => PaymentSupplementEvidenceSchemas.Extend(name,
                new("$or", new BsonArray { previous, new BsonDocument() })));
        }
        var pin = PaymentSupplementEvidenceSchemas.SupplementBranch("evidencePins");
        var properties = pin["$and"][0]["$jsonSchema"]["properties"];
        Assert.Equal(2L, properties["supplementRevision"]["minimum"]); Assert.Equal(2L, properties["supplementRevision"]["maximum"]);
        Assert.True(properties.AsBsonDocument.Contains("primary")); Assert.True(properties.AsBsonDocument.Contains("primarySubmissionHash"));
        Assert.False(properties.AsBsonDocument.Contains("declarationVersionId"));
    }

    [Fact]
    public void Exact_M80_lease_successor_retains_catalog_and_image_shapes_and_all_validation_policy_lifecycle_bindings()
    {
        const string name = "evidenceReadLeases";
        var predecessor = CatalogEvidenceReadSchemas.Validator(name);
        var control = new BsonDocument("until", new BsonDocument("$type", "date"));
        var next = PaymentSupplementEvidenceSchemas.Extend(name, new("$and", new BsonArray { predecessor, control }));
        Assert.Equal(predecessor, next["$and"][0]["$or"][0]);
        Assert.Equal(control, next["$and"][1]);
        Assert.True(SupplierDocumentEvidenceSchemas.Supports(name, next, predecessor));
        Assert.True(SupplierDocumentEvidenceSchemas.Supports(name, next, EvidenceAccessSchemas.Validator(name)));
        Assert.True(SupplierDocumentEvidenceSchemas.Supports(name, next, SupplierDocumentEvidenceSchemas.Validator(name)));
        Assert.False(SupplierDocumentEvidenceSchemas.Supports(name, new("$or", new BsonArray { predecessor, new BsonDocument() }), predecessor));
        // A missing M80 catalog branch is not a genuine current predecessor.
        Assert.Throws<InvalidOperationException>(() => PaymentSupplementEvidenceSchemas.Extend(name, SupplierDocumentEvidenceSchemas.Validator(name)));
        var changed = predecessor.DeepClone().AsBsonDocument;
        changed["$or"][1] = new BsonDocument();
        Assert.Throws<InvalidOperationException>(() => PaymentSupplementEvidenceSchemas.Extend(name, changed));
        var original = SupplierDocumentEvidenceSchemas.Validator(name)["$or"][1]["$and"][0]["$jsonSchema"]["properties"];
        var supplement = PaymentSupplementEvidenceSchemas.SupplementBranch(name);
        var current = supplement["$and"][0]["$and"][0]["$jsonSchema"]["properties"];
        foreach (var field in new[] { "reference", "reservationId", "versionRowId", "storeId", "generation", "validationHash", "lifecycleRevision",
            "readPolicyId", "readPolicyRevision", "validationPolicyId", "validationPolicyRevision", "actor", "deadline", "until" })
            Assert.Equal(original[field], current[field]);
        Assert.Equal(new BsonArray { "PaymentSupplement" }, current["target"]["properties"]["kind"]["enum"]);
        Assert.Equal(new BsonArray { 1L, 2L }, supplement["$and"][1]["target.revision"]["$in"]);
    }
}
