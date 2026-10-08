using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Operations;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Operations;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class OperationalAnomalyRefundTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Recorded_refund_uses_original_payout_evidence_and_explicit_policy_without_financial_changes()
    {
        await using var data = await RefundPayoutTests.Data.CreateAsync(fixture);
        await data.UploadAsync(); await data.ValidateAsync();
        _ = await data.RecordAsync(await data.PreviewRecordAsync(), RefundPayoutTests.Data.Meta("anomaly-refund-record"));
        var payout = await data.Rows(RefundPayoutBson.Payouts).Find(new BsonDocument()).SingleAsync();
        await data.Catalog.Base.AddGrantAsync(data.Actor, 987499, CatalogAuthoringTestData.Shop,
            [Permissions.AnomalyRead, Permissions.AnomalyReview, Permissions.AnomalyPolicyConfigure]);
        var unit = new MongoUnitOfWork(data.Catalog.Client);
        var service = new OperationalAnomalyService(data.Database, unit, new(data.Database), data.Clock,
            new(data.Database, unit, data.Clock), new OrderSubmissionService(data.Database, unit, new(data.Database), data.Clock));
        var before = await data.PreservedAsync();
        var absent = await service.EvaluateAsync(data.Actor, "refund", payout["_id"].AsInt64, RefundPayoutTests.Data.Meta("anomaly-refund-absent"));
        Assert.Equal("consistent", absent.Signals.Single(signal => signal.Code == "refund_reserved_amount").State);
        Assert.Equal("policy_needed", absent.Signals.Single(signal => signal.Code == "refund_limit").State);
        await service.ConfigurePolicyAsync(data.Actor, "refund", payout["_id"].AsInt64, 1,
            new("synthetic-refund-v1", "SYNTHETIC-TEST-ONLY", null, null, "IDR", 500m), RefundPayoutTests.Data.Meta("anomaly-refund-policy"));
        var flagged = await service.EvaluateAsync(data.Actor, "refund", payout["_id"].AsInt64, RefundPayoutTests.Data.Meta("anomaly-refund-evaluate"));
        Assert.Equal("flagged", flagged.Signals.Single(signal => signal.Code == "refund_limit").State);
        var reviewed = await service.ReviewAsync(data.Actor, flagged.Id.Value, "acknowledged", RefundPayoutTests.Data.Meta("anomaly-refund-review"));
        Assert.NotNull(reviewed.Disposition); Assert.Equal(before, await data.PreservedAsync());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(data.Actor with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, flagged.Id.Value));
        await data.Rows(RefundPayoutBson.Payouts).UpdateOneAsync(new BsonDocument("_id", payout["_id"]),
            Builders<BsonDocument>.Update.Set("snapshotHash", new string('0', 64)));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.ReadAsync(data.Actor, flagged.Id.Value));
    }
}
