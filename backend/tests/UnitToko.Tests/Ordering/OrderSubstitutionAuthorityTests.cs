using System.Runtime.Versioning;
using UnitToko.Tests.Documents;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Inventory;
using S = UnitToko.Platform.Ordering.OrderSubstitutionBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class OrderSubstitutionAuthorityTests(MongoFixture fixture)
{
    [Fact]
    public void Buyer_requester_can_consent_but_historical_proposer_cannot() {
        var evidence = new ApprovalRequest(1, 1, RequestedBy: 21, ProposedBy: 22);
        Assert.True(ApprovalPolicy.Evaluate(21, Permissions.SubstitutionAccept, evidence).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(22, Permissions.SubstitutionAccept, evidence).Allowed);
        Assert.True(PersonApprovalPolicy.RequiresEvidence(Permissions.SubstitutionAccept));
        Assert.Equal(new long[] { 21, 22 }, PersonApprovalPolicy.RequiredAccounts(21, Permissions.SubstitutionAccept, evidence).ToArray());
    }

    [WindowsStorageFact]
    public async Task Actual_buyer_consent_requires_independent_proposer_current_authority_and_exact_receipt_participants() {
        var prepared = await OrderSubstitutionTestData.CreateAsync(fixture, systemClock: false); await using var data = prepared.Data;
        var target = Assert.Single((await data.Orders.ListSubstitutionTargetsAsync(data.Dispatcher)).Items);
        var replacement = Assert.Single((await data.Orders.SubstitutionPackagesAsync(data.Dispatcher, data.OrderId, data.SuborderId)).Items,
            item => item.Definition.SkuId.Value == prepared.ReplacementSku);
        var input = new OrderSubstitutionInput(new(data.OrderId), new(data.SuborderId), target.Revision, target.DispatchSequence,
            new(new(data.SkuId), 12, new(replacement.Definition.SkuId, replacement.Definition.Id, replacement.Definition.Version, replacement.Price.Id, replacement.Price.Version, 1)));
        var offered = await data.Orders.PreviewSubstitutionAsync(data.Dispatcher, input);
        var proposal = await data.Orders.ProposeSubstitutionAsync(data.Dispatcher, input, offered.Review, GenuineFiniteDispatchTestData.Meta("substitution-person-proposal"));
        var review = await data.Orders.PreviewSubstitutionDecisionAsync(data.Buyer, proposal["proposalId"].AsInt64, OrderSubstitutionDecision.Accept);
        var before = await data.Rows("movements").CountDocumentsAsync(new BsonDocument());
        var binding = await data.Rows("personAccountBindings").Find(new BsonDocument("_id", data.Buyer.UserId)).SingleAsync();
        await data.Rows("personAccountBindings").DeleteOneAsync(new BsonDocument("_id", data.Buyer.UserId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Orders.DecideSubstitutionAsync(data.Buyer,
            review.ProposalId.Value, review.Revision, OrderSubstitutionDecision.Accept, review.Review, GenuineFiniteDispatchTestData.Meta("substitution-person-unknown")));
        await data.Rows("personAccountBindings").InsertOneAsync(binding);
        await PersonTestData.SetEquivalenceAsync(data.Unit, data.Database, data.Dispatcher.UserId, data.Buyer.UserId, shared: true);
        var shared = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Orders.DecideSubstitutionAsync(data.Buyer,
            review.ProposalId.Value, review.Revision, OrderSubstitutionDecision.Accept, review.Review, GenuineFiniteDispatchTestData.Meta("substitution-person-shared")));
        Assert.True(shared.Code == "independent_person_required", shared.ToString());
        await PersonTestData.SetEquivalenceAsync(data.Unit, data.Database, data.Dispatcher.UserId, data.Buyer.UserId, shared: false);
        // A staff account having the offer does not acquire buyer consent authority.
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Orders.DecideSubstitutionAsync(data.Dispatcher,
            review.ProposalId.Value, review.Revision, OrderSubstitutionDecision.Accept, review.Review, GenuineFiniteDispatchTestData.Meta("substitution-person-wrong-scope")));
        Assert.Equal(before, await data.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows(S.Decisions).CountDocumentsAsync(new BsonDocument()));
        var result = await data.Orders.DecideSubstitutionAsync(data.Buyer, review.ProposalId.Value, review.Revision, OrderSubstitutionDecision.Accept,
            review.Review, GenuineFiniteDispatchTestData.Meta("substitution-person-accepted"));
        Assert.Equal(result, await data.Orders.RecoverSubstitutionDecisionAsync(data.Buyer, "substitution-person-accepted", OrderSubstitutionDecision.Accept));
        var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument { { "actorId", data.Buyer.UserId },
            { "operation", S.AcceptOperation }, { "key", "substitution-person-accepted" } }).SingleAsync();
        Assert.Equal(new ApprovalRequest(1, 1, RequestedBy: data.Buyer.UserId, ProposedBy: data.Dispatcher.UserId),
            PersonApprovalReceipt.Read(receipt, Permissions.SubstitutionAccept));
        await PersonTestData.SetEquivalenceAsync(data.Unit, data.Database, data.Dispatcher.UserId, data.Buyer.UserId, shared: true);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Orders.RecoverSubstitutionDecisionAsync(data.Buyer,
            "substitution-person-accepted", OrderSubstitutionDecision.Accept));
        await PersonTestData.SetEquivalenceAsync(data.Unit, data.Database, data.Dispatcher.UserId, data.Buyer.UserId, shared: false);
        Assert.Equal(result, await data.Orders.RecoverSubstitutionDecisionAsync(data.Buyer, "substitution-person-accepted", OrderSubstitutionDecision.Accept));
    }
}
