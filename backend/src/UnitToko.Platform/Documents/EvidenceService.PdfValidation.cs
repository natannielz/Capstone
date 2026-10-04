using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Documents;

internal sealed partial class EvidenceService
{
    // This bridge grants no PDF admission. The shared core still requires an exact
    // current admitted source and matching dispatch kind before issuing a capability.
    internal Task<ValidationOriginal> PdfValidationOriginalAsync(IClientSessionHandle tx,
        BsonDocument job, CancellationToken ct) =>
        ValidationOriginalCoreAsync(tx, job, "evidence.pdf-validation.requested", ct);
}
