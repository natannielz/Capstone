using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using B = UnitToko.Platform.Billing.ReceivingAccountBson;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class ReceivingAccountTests(MongoFixture fixture)
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Later_person_equivalence_of_observer_and_maker_denies_approval_and_receipt_recovery(bool recovery) {
        await using var data = await ReceivingAccountTestData.CreateAsync(fixture); var proposal = await data.ProposeAsync(); var observed = await data.ObserveAsync(proposal);
        if (recovery) await data.ApproveAsync(proposal, observed);
        var maker = data.Maker.UserId; var observer = data.Checker.UserId;
        // Synthetic reviewed equivalence using the existing shared-head protocol; no real personnel assertion.
        await new MongoUnitOfWork(data.Catalog.Client).ExecuteAsync(async (tx, ct) => {
            await data.Rows("personRecords").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.PersonId(observer)), Builders<BsonDocument>.Update.Set("currentEquivalenceId", PersonTestData.GroupId(maker)).Inc("version", 1L), cancellationToken: ct);
            await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.GroupId(maker)), Builders<BsonDocument>.Update
                .Set("memberPersonIds", new BsonArray { PersonTestData.PersonId(maker), PersonTestData.PersonId(observer) }).Set("memberUserIds", new BsonArray { maker, observer }).Inc("version", 1L), cancellationToken: ct);
            await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.GroupId(observer)), Builders<BsonDocument>.Update.Set("status", "superseded").Inc("version", 1L), cancellationToken: ct);
            return true;
        });
        var before = await data.StateAsync();
        var failure = await Assert.ThrowsAsync<DomainValidationException>(async () => {
            if (recovery) await data.Service.RecoverAsync(data.Approver, B.Prefix + "approve", "approve"); else await data.ApproveAsync(proposal, observed);
        });
        Assert.Equal("independent_person_required", failure.Code); Assert.Equal(before, await data.StateAsync());
    }
    [Fact]
    public async Task Separate_bank_observation_approval_version_history_masked_reads_and_receipt_recovery() {
        await using var data = await ReceivingAccountTestData.CreateAsync(fixture);
        var proposal = await data.ProposeAsync(); var observed = await data.ObserveAsync(proposal); var approved = await data.ApproveAsync(proposal, observed);
        Assert.Equal(1L, approved["accountVersion"].AsInt64);
        Assert.Equal(proposal, await data.ProposeAsync()); Assert.Equal(observed, await data.ObserveAsync(proposal)); Assert.Equal(approved, await data.ApproveAsync(proposal, observed));
        Assert.Equal(approved, await data.Service.RecoverAsync(data.Approver, B.Prefix + "approve", "approve"));
        var view = await data.Service.ReadProposalAsync(data.Checker, proposal["proposalId"].AsInt64);
        Assert.Equal("0000123456789", view["currentVersion"]["details"]["accountNumber"].AsString);
        var previous = view["currentVersion"].DeepClone();
        var second = await data.ProposeAsync("change", proposal["accountId"].AsInt64, 1, ReceivingAccountTestData.Details with { AccountNumber = "0000987654321" });
        var secondObservation = await data.ObserveAsync(second, ReceivingAccountTestData.Details with { AccountNumber = "0000987654321" }, "observe-change");
        await data.ApproveAsync(second, secondObservation, "approve-change");
        Assert.Equal(previous, await data.Rows(B.Versions).Find(new BsonDocument("_id", previous["_id"])).SingleAsync());
        Assert.Equal(2, await data.Rows(B.Versions).CountDocumentsAsync(new BsonDocument()));
        var masked = await data.Service.ListProposalsAsync(data.Masked);
        Assert.Equal(2, masked.Count); Assert.DoesNotContain("0000123456789", new BsonArray(masked).ToJson()); Assert.Contains("*********6789", new BsonArray(masked).ToJson());
        Assert.DoesNotContain("0000123456789", (await data.StateAsync())["auditEvents"].ToJson()); Assert.DoesNotContain("0000123456789", (await data.StateAsync())["outboxEvents"].ToJson());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadProposalAsync(data.Masked, proposal["proposalId"].AsInt64));
        Assert.Empty(await data.Service.ListProposalsAsync(data.Other));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadProposalAsync(data.Other, proposal["proposalId"].AsInt64));
        Assert.Null(await data.Service.RecoverAsync(data.Approver, B.Prefix + "approve", "missing"));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.ProposeAsync(details: ReceivingAccountTestData.Details with { AccountNumber = "1234" }));
    }
    [Fact]
    public async Task Maker_cannot_observe_or_approve_and_mismatch_never_activates_but_can_be_rejected() {
        await using var data = await ReceivingAccountTestData.CreateAsync(fixture); var proposal = await data.ProposeAsync(); var id = proposal["proposalId"].AsInt64;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ObserveAsync(data.Maker, id, 1, ReceivingAccountTestData.Details, "synthetic-bank", true, ReceivingAccountTestData.Meta("self-observe")));
        var observed = await data.ObserveAsync(proposal, ReceivingAccountTestData.Details with { AccountNumber = "0000123456780" }); var before = await data.StateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.DecideAsync(data.Maker, id, 1, ReceivingAccountDecision.Approve, observed["recordId"].AsInt64, ReceivingAccountTestData.Meta("self-approve")));
        Assert.Equal("receiving_account_observation_mismatch", (await Assert.ThrowsAsync<DomainValidationException>(() => data.ApproveAsync(proposal, observed))).Code);
        Assert.Equal(before, await data.StateAsync());
        var rejected = await data.Service.DecideAsync(data.Approver, id, 1, ReceivingAccountDecision.Reject, null, ReceivingAccountTestData.Meta("reject"));
        Assert.Null(await data.Service.RecoverAsync(data.Approver, B.Prefix + "approve", "approve"));
        Assert.Equal(rejected, await data.Service.RecoverAsync(data.Approver, B.Prefix + "reject", "reject"));
        Assert.Equal(0, await data.Rows(B.Versions).CountDocumentsAsync(new BsonDocument()));
    }
    [Theory]
    [InlineData("policy")] [InlineData("scope")] [InlineData("person")] [InlineData("mfa")] [InlineData("session")]
    public async Task Current_authority_is_required_for_new_approval(string fault) {
        await using var data = await ReceivingAccountTestData.CreateAsync(fixture); var proposal = await data.ProposeAsync(); var observed = await data.ObserveAsync(proposal);
        if (fault == "policy") await data.Rows(B.Policies).UpdateOneAsync(new BsonDocument("_id", 810L), Builders<BsonDocument>.Update.Set("active", false));
        if (fault == "scope") await data.Rows("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 101L), Builders<BsonDocument>.Update.Set("status", "suspended"));
        if (fault == "person") await data.Rows("personAccountBindings").UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId), Builders<BsonDocument>.Update.Set("status", "suspended"));
        if (fault == "mfa") data.Catalog.Clock.UtcNow += TimeSpan.FromMinutes(6);
        if (fault == "session") await data.Catalog.Sessions.RevokeAsync(data.Catalog.Issued[data.Approver.UserId].OpaqueToken, "Synthetic revocation");
        var before = await data.StateAsync(); Assert.NotNull(await Record.ExceptionAsync(() => data.ApproveAsync(proposal, observed))); Assert.Equal(before, await data.StateAsync());
    }
    [Theory]
    [InlineData("receipt")] [InlineData("audit")] [InlineData("outbox")] [InlineData("observation")] [InlineData("head")]
    public async Task Missing_provenance_is_not_recreated_by_retry_or_recovery(string fault) {
        await using var data = await ReceivingAccountTestData.CreateAsync(fixture); var proposal = await data.ProposeAsync(); var observed = await data.ObserveAsync(proposal); await data.ApproveAsync(proposal, observed);
        var decision = await data.Rows(B.Decisions).Find(new BsonDocument("proposalId", proposal["proposalId"])).SingleAsync();
        var (collection, filter) = fault switch {
            "receipt" => ("idempotencyRecords", new BsonDocument("key", "approve")), "audit" => ("auditEvents", new BsonDocument("_id", decision["auditId"])),
            "outbox" => ("outboxEvents", new BsonDocument("sourceKey", B.SourceKey(decision))), "observation" => (B.Observations, new BsonDocument("_id", observed["recordId"])),
            _ => (B.Heads, new BsonDocument("_id", proposal["accountId"])) };
        await data.Rows(collection).DeleteOneAsync(filter); var before = await data.StateAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.ApproveAsync(proposal, observed));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.RecoverAsync(data.Approver, B.Prefix + "approve", "approve")); Assert.Equal(before, await data.StateAsync());
    }
    [Fact]
    public async Task Concurrent_decisions_commit_one_version_and_stale_proposals_can_be_withdrawn() {
        await using var data = await ReceivingAccountTestData.CreateAsync(fixture); var proposal = await data.ProposeAsync(); var observed = await data.ObserveAsync(proposal);
        var stale = await data.ProposeAsync("stale", proposal["accountId"].AsInt64);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Record.ExceptionAsync(() => data.ApproveAsync(proposal, observed, "approve-race-" + index))));
        Assert.Single(results, error => error is null); Assert.Single(results, error => error is not null);
        Assert.Equal(1, await data.Rows(B.Versions).CountDocumentsAsync(new BsonDocument()));
        var withdrawn = await data.Service.DecideAsync(data.Maker, stale["proposalId"].AsInt64, 1, ReceivingAccountDecision.Withdraw, null, ReceivingAccountTestData.Meta("withdraw"));
        Assert.Equal(withdrawn, await data.Service.RecoverAsync(data.Maker, B.Prefix + "withdraw", "withdraw"));
    }
}
