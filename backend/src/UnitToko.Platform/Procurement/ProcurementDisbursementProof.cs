using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Procurement;
using B = UnitToko.Platform.Procurement.ProcurementDisbursementBson;

namespace UnitToko.Platform.Procurement;

// One command's already verified source graph. It cannot survive or move to another transaction.
internal sealed class ProcurementDisbursementProof(object owner, IClientSessionHandle tx, BsonDocument source, DateTimeOffset earliest, Action current)
{
    private readonly long transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
    private readonly string hash = B.Hash(source);
    internal bool Matches(BsonDocument value) => B.Hash(value) == hash;
    internal void Require(object caller, IClientSessionHandle session, ProcurementDisbursementInput input, BsonDocument value, DateTimeOffset at) {
        if (!ReferenceEquals(owner, caller) || !ReferenceEquals(tx, session) || !session.IsInTransaction || session.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction || !Matches(value) || at < earliest) throw B.Broken();
        // Current admission proves the observation is still fresh now; the immutable approval's
        // effectiveAt is at or after its observation. Together with the caller's at <= now check,
        // the lower bound preserves the same historical observation window without re-reading it.
        current(); RequireLinks(input, value, at);
    }
    internal static void RequireLinks(ProcurementDisbursementInput input, BsonDocument source, DateTimeOffset at) {
        var funding = source["funding"]; var beneficiary = source["beneficiary"]; var bank = source["payingBank"]; var policy = source["policy"];
        foreach (var (actual, expected) in new (BsonValue, BsonValue)[] {
            (funding["request"]["_id"], input.FundingRequestId), (funding["decision"]["_id"], input.FundingDecisionId), (funding["authorization"]["_id"], input.AuthorizationId),
            (funding["request"]["location"], ProcurementRequestBson.Location(input.Location)), (funding["request"]["orderSource"]["proposal"]["input"]["supplierId"], input.SupplierId),
            (beneficiary["supplierId"], input.SupplierId), (beneficiary["versionId"], input.BeneficiaryVersionId), (beneficiary["version"], input.BeneficiaryVersion),
            (beneficiary["owner"], SupplierBson.Owner(new SupplierOwner(input.Location.OrganizationId, input.Location.ShopId, input.Location.ChannelId))),
            (beneficiary["supplier"]["version"]["_id"], funding["request"]["orderSource"]["proposal"]["input"]["supplierVersionId"]),
            (beneficiary["supplier"]["version"]["version"], funding["request"]["orderSource"]["proposal"]["input"]["supplierVersion"]),
            (bank["accountId"], input.PayingAccountId), (bank["versionId"], input.PayingAccountVersionId), (bank["book"]["organizationId"], input.Location.OrganizationId),
            (bank["book"]["shopId"], input.Location.ShopId), (bank["book"]["channelId"], input.Location.ChannelId), (bank["book"]["currency"], "IDR"),
            (policy["_id"], input.PolicyId), (policy["revision"], input.PolicyRevision), (policy["location"], ProcurementRequestBson.Location(input.Location)),
            (policy["payingAccountId"], input.PayingAccountId), (policy["payingAccountVersionId"], input.PayingAccountVersionId) }) ProcurementDisbursementRecords.Equal(actual, expected);
        if (funding["decision"]["recordedAt"].ToUniversalTime() > at.UtcDateTime || beneficiary["effectiveAt"].ToUniversalTime() > at.UtcDateTime) throw B.Broken();
        ProcurementDisbursementRecords.PolicyAt(policy.AsBsonDocument, at); SupplierBankRecords.PolicyAt(beneficiary["policy"].AsBsonDocument, at, true);
    }
}
