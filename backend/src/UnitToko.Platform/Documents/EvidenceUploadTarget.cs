using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Platform.Documents;
internal sealed record EvidenceUploadTarget(EvidenceTargetRef Target, GrantScope Owner, long Revision, long? DefinitionId, long? DefinitionVersion, long? SubmittedReservationId = null, long? SubmittedSubmissionId = null, long? ReviewUploaderId = null);
internal interface IPaymentEvidenceUploadTarget
{
    IMongoDatabase Database { get; }
    Task<T> WithTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct);
    Task<T> WithReadTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct);
    Task<PaymentEvidenceAssociation> RequireSubmissionAssociationAsync(IClientSessionHandle tx, ActorContext actor, long submissionId, EvidenceReadClaim claim, CancellationToken ct);
}
internal sealed record PaymentEvidenceAssociation(long Id, long PinId, long DeclarationId, long DeclarationVersionId, long DeclarationRevision,
    long ReviewRevision, string Hash, string Reason, string CorrelationId, DateTimeOffset SubmittedAt, GrantScope Owner);
internal sealed partial class EvidenceService
{
    private Task<T> WithReadTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision, EvidenceRef? reference,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) =>
        target.Kind == EvidenceTargetKind.PaymentDeclaration && paymentTargets is not null
            ? paymentTargets.WithReadTargetAsync(tx, actor, target, expectedRevision, reference, action, ct)
            : WithTargetAsync(tx, actor, target, expectedRevision, action, ct);
    private Task<T> WithTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) {
        if (target.Kind is EvidenceTargetKind.CatalogFamily or EvidenceTargetKind.CatalogSku)
            return catalog.WithCatalogImageUploadTargetAsync(database, tx, actor, target, expectedRevision,
                (value, token) => action(new(value.Target, value.Owner, value.Revision, value.DefinitionId, value.DefinitionVersion), token), ct);
        if (target.Kind == EvidenceTargetKind.PaymentDeclaration && paymentTargets is not null)
            return paymentTargets.WithTargetAsync(tx, actor, target, expectedRevision, action, ct);
        throw Invalid("evidence_target_unsupported");
    }
    private static EvidencePurpose Purpose(EvidenceTargetRef target) => target.Kind switch {
        EvidenceTargetKind.CatalogFamily or EvidenceTargetKind.CatalogSku => EvidencePurpose.CatalogImage,
        EvidenceTargetKind.PaymentDeclaration => EvidencePurpose.PaymentImage, _ => throw Invalid("evidence_target_unsupported") };
    private static string ReserveOperation(EvidencePurpose purpose) => purpose switch {
        EvidencePurpose.CatalogImage => "evidence.catalog-image.reserve", EvidencePurpose.PaymentImage => "evidence.payment-image.reserve", _ => throw Invalid() };
    private static string ReserveOperation(MongoDB.Bson.BsonDocument reservation) => ReserveOperation(Purpose(ReadTarget(reservation)));
    private static string CompleteOperation(Bundle bundle) => Purpose(ReadTarget(bundle.Reservation)) == EvidencePurpose.PaymentImage ? "evidence.payment-image.complete" : "evidence.catalog-image.complete";
}
