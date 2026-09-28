using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

public sealed partial class PersonWithdrawalServiceTests
{
    [Fact]
    public async Task Reviewed_preview_concurrent_different_keys_commit_one_consumption_and_one_effect()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        var original = await PreviewServiceAsync(data);
        var preview = await original.PreviewProposeAsync(data.Maker, data.GroupId, 1, Audit);
        using var firstReads = new Barrier(2);
        var arrivals = 0;
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command =>
        {
            // Synchronize the two first transactional identity reads, before either transaction
            // takes the common actor/policy fences. Later retry attempts must not re-enter.
            if (command.Command.TryGetValue("find", out var found) && found == "identities" &&
                command.Command.GetValue("startTransaction", BsonBoolean.False) == BsonBoolean.True &&
                Interlocked.Increment(ref arrivals) <= 2 &&
                !firstReads.SignalAndWait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Both independent preview submissions must start their transactions.");
        });
        var service = data.NewService(new MongoClient(settings), selection: db =>
            new WorkflowSelection(db, data.GroupId, WorkflowDisclosed()));
        var attempts = await Task.WhenAll(Submit("preview-concurrent-one"), Submit("preview-concurrent-two"));
        Assert.True(Volatile.Read(ref arrivals) >= 2, "Both commands must reach their initial transactional read.");
        var winner = Assert.Single(attempts, attempt => attempt.Result is not null);
        var loser = Assert.Single(attempts, attempt => attempt.Conflict is not null);
        var committed = Assert.IsType<PersonWithdrawalResult>(winner.Result);
        Assert.Equal("version_conflict", loser.Conflict!.Code);
        var stored = await data.Rows("personReviewPreviews").Find(new BsonDocument("reference", preview.Reference)).SingleAsync();
        Assert.Equal("consumed", stored["status"].AsString);
        Assert.Equal(2L, stored["version"].AsInt64);
        Assert.Equal(committed.ProposalId, stored["consumption"]["resultProposalId"].AsInt64);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(winner.Key))).ToLowerInvariant(),
            stored["consumption"]["keyHash"].AsString);
        var history = Assert.Single(await data.Rows("personReviewHistory").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());
        Assert.Equal(history["_id"], stored["consumption"]["resultHistoryId"]);
        Assert.Equal(committed.ProposalId, history["proposalId"].AsInt64);
        var receipt = Assert.Single(await data.Rows("idempotencyRecords").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());
        Assert.Equal(winner.Key, receipt["key"].AsString);
        Assert.Equal(1, await data.CountAsync("personReviewProposals"));
        Assert.Equal(committed, await original.SubmitProposalAsync(data.Maker, preview.Reference, Metadata(winner.Key)));
        await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            original.SubmitProposalAsync(data.Maker, preview.Reference, Metadata(loser.Key)));
        Assert.Equal(1, await data.CountAsync("personReviewProposals"));
        Assert.Equal(1, await data.CountAsync("personReviewHistory"));
        Assert.Equal(1, await data.CountAsync("idempotencyRecords"));

        Task<(string Key, PersonWithdrawalResult? Result, PersistenceConflictException? Conflict)> Submit(string key) =>
            Task.Run(async () =>
            {
                try
                {
                    return (key, (PersonWithdrawalResult?)await service.SubmitProposalAsync(data.Maker, preview.Reference, Metadata(key)),
                        (PersistenceConflictException?)null);
                }
                catch (PersistenceConflictException error)
                {
                    return (key, (PersonWithdrawalResult?)null, (PersistenceConflictException?)error);
                }
            });
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("session")]
    public async Task Reviewed_approval_expiry_after_successful_final_await_rolls_back_complete_consumption(string stage)
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        var original = await PreviewServiceAsync(data);
        var proposed = await data.ProposeAsync();
        var preview = await original.PreviewApproveAsync(data.Checker, proposed.ProposalId, 1, Audit);
        var deadline = data.Clock.UtcNow.AddSeconds(1);
        await data.Rows("personReviewPreviews").UpdateOneAsync(new BsonDocument("reference", preview.Reference),
            U.Set("expiresAt", deadline.UtcDateTime));
        var before = await PreviewRaceStateAsync(data);
        var observed = 0;
        var selectedRequest = 0;
        var receiptRequest = 0;
        var successfulReceipts = 0;
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.Command.TryGetValue("insert", out var inserted) && inserted == "idempotencyRecords")
                {
                    receiptRequest = command.RequestId;
                    if (stage == "receipt") selectedRequest = command.RequestId;
                }
                if (stage == "session" && command.Command.TryGetValue("find", out var found) && found == "identitySessions" &&
                    command.Command.TryGetValue("projection", out var projection) &&
                    projection.AsBsonDocument.Contains("idleExpiresAt") && projection.AsBsonDocument.Contains("absoluteExpiresAt"))
                    selectedRequest = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (receiptRequest != 0 && command.RequestId == receiptRequest) Interlocked.Increment(ref successfulReceipts);
                if (selectedRequest != 0 && command.RequestId == selectedRequest && Interlocked.CompareExchange(ref observed, 1, 0) == 0)
                    data.Clock.UtcNow = deadline;
            });
        };
        var service = data.NewService(new MongoClient(settings), selection: db =>
            new WorkflowSelection(db, data.GroupId, WorkflowDisclosed()));
        var error = await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            service.ApproveReviewedAsync(data.Checker, proposed.ProposalId, 1, preview.Reference, Metadata("approval-final-" + stage)));
        Assert.Equal("person_preview_expired", error.Code);
        Assert.Equal(1, Volatile.Read(ref observed));
        Assert.Equal(1, Volatile.Read(ref successfulReceipts));
        // This compares affected account/security epochs, every persisted session, group state
        // and fences, proposal/history, ready preview, receipt, policy, grants and read audit.
        Assert.Equal(before, await PreviewRaceStateAsync(data));
        Assert.Equal("pending", (await data.RowAsync("personReviewProposals", proposed.ProposalId))["status"].AsString);
        Assert.Equal("active", (await data.RowAsync("personEquivalenceGroups", data.GroupId))["status"].AsString);
        Assert.Equal("ready", (await data.Rows("personReviewPreviews")
            .Find(new BsonDocument("reference", preview.Reference)).SingleAsync())["status"].AsString);
    }

    [Theory]
    [InlineData("propose", "alias")]
    [InlineData("propose", "group")]
    [InlineData("approve", "alias")]
    [InlineData("approve", "group")]
    [InlineData("approve", "proposal")]
    public async Task Reviewed_preview_requires_current_disclosure_when_action_appointment_is_unchanged(string kind, string hidden)
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        await WorkflowProfilesAsync(data);
        var scope = new PreviewRaceSelectionScope();
        var service = data.NewService(selection: db => new PreviewRaceSelection(db, data.GroupId, scope));
        var actor = kind == "propose" ? data.Maker : data.Checker;
        PersonWithdrawalResult? proposal = null;
        if (kind == "approve") proposal = await data.ProposeAsync();
        var preview = kind == "propose"
            ? await service.PreviewProposeAsync(actor, data.GroupId, 1, Audit)
            : await service.PreviewApproveAsync(actor, proposal!.ProposalId, 1, Audit);
        var before = await PreviewRaceStateAsync(data);
        var beforeSelections = scope.Selections;
        if (hidden == "alias") scope.Disclosed.Remove(AliasId);
        else if (hidden == "group") scope.Groups = false;
        else scope.Proposals = false;
        Assert.Contains(actor.UserId, data.Appointments.Users);
        var error = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => kind == "propose"
            ? service.SubmitProposalAsync(actor, preview.Reference, Metadata("removed-visibility-" + hidden))
            : service.ApproveReviewedAsync(actor, proposal!.ProposalId, 1, preview.Reference, Metadata("removed-visibility-" + hidden)));
        Assert.Equal("person_review_unavailable", error.Code);
        Assert.True(scope.Selections > beforeSelections, "Consuming a preview must resolve the current selection again.");
        Assert.Equal(before, await PreviewRaceStateAsync(data));
        Assert.Equal("ready", (await data.Rows("personReviewPreviews")
            .Find(new BsonDocument("reference", preview.Reference)).SingleAsync())["status"].AsString);
    }

    private sealed class PreviewRaceSelectionScope
    {
        internal HashSet<long> Disclosed { get; } = WorkflowDisclosed();
        internal bool Groups { get; set; } = true;
        internal bool Proposals { get; set; } = true;
        internal int Selections;
    }

    private sealed class PreviewRaceSelection(IMongoDatabase database, long groupId, PreviewRaceSelectionScope scope)
        : IPersonReviewSelectionTrust
    {
        public Task<PersonReviewSelectionPermit> RequireAsync(IClientSessionHandle tx, ActorContext actor,
            PersonReviewSelectionRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref scope.Selections);
            // Synthetic selection changes only; the separately appointed command adapter remains unchanged.
            return new WorkflowSelection(database, groupId, scope.Disclosed, scope.Groups, scope.Proposals)
                .RequireAsync(tx, actor, request, ct);
        }
    }

    private static async Task<BsonDocument> PreviewRaceStateAsync(Data data)
    {
        var snapshot = new BsonDocument();
        foreach (var collection in new[]
        {
            "personEquivalenceGroups", "identities", "identitySessions", "personReviewProposals",
            "personReviewHistory", "personReviewPreviews", "idempotencyRecords", "personGovernancePolicies",
            "personAccountBindings", "personRecords", "personEvidenceSources", "authorizationGrants",
            "memberships", "authorizationReadEvents"
        })
            snapshot.Add(collection, new BsonArray(await data.Rows(collection)
                .Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        // ID reservations happen before the transaction and may leave harmless gaps after denial.
        return snapshot;
    }
}
