using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Billing.RefundPayoutBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class RefundPayoutDiscoveryTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Approved_refund_and_immutable_draft_summary_survive_expired_recording_policy_but_resumption_denies()
    {
        await using var data = await RefundPayoutTests.Data.CreateAsync(fixture);
        var original = await data.PreservedAsync();
        var filter = new BsonDocument("active", true);
        Assert.Equal(1, (await data.Rows(B.Policies).UpdateOneAsync(filter,
            Builders<BsonDocument>.Update.Set("expiresAt", data.Clock.UtcNow.UtcDateTime))).ModifiedCount);
        var view = await data.Service.ReadApprovedRefundAsync(data.Actor, data.Source.CaseId, data.Source.ReturnId);
        Assert.Equal(data.ApprovalId, view["approval"]["financialApprovalId"].AsInt64);
        Assert.Equal(data.Draft, view["draft"]);
        Assert.True(view["payout"].IsBsonNull);
        Assert.Equal(600m, view["approval"]["reservedRefund"].AsDecimal);
        var failure = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.ReadDraftAsync(data.Actor, data.DraftId));
        Assert.Equal("refund_payout_policy_unavailable", failure.Code);
        Assert.Equal(0, await data.Rows(B.Payouts).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(original, await data.PreservedAsync());
    }
}
