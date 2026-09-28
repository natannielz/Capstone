using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

public sealed partial class PersonWithdrawalServiceTests
{
    private static async Task<PersonWithdrawalService> PreviewServiceAsync(Data data)
    {
        await WorkflowProfilesAsync(data);
        return data.NewService(selection: db => new WorkflowSelection(db, data.GroupId,
            [MakerId, CheckerId, TargetId, AliasId]));
    }

    [Fact]
    public async Task Reviewed_preview_consumption_and_independent_approval_preserve_exact_retry_outcomes()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        var service = await PreviewServiceAsync(data);
        var preview = await service.PreviewProposeAsync(data.Maker, data.GroupId, 1, Audit);
        Assert.Equal("propose", preview.Kind);
        Assert.Equal(2, preview.Target.Accounts.Length);
        Assert.InRange(preview.ExpiresAt, data.Clock.UtcNow.AddSeconds(1), data.Clock.UtcNow.AddMinutes(5));
        var submitted = await service.SubmitProposalAsync(data.Maker, preview.Reference, Metadata("preview-submit"));
        Assert.Equal("pending", submitted.Status);
        var stored = await data.Rows("personReviewPreviews").Find(new BsonDocument("reference", preview.Reference)).SingleAsync();
        Assert.Equal("consumed", stored["status"].AsString);
        Assert.Equal(submitted.ProposalId, stored["consumption"]["resultProposalId"].AsInt64);
        Assert.Equal(submitted, await service.SubmitProposalAsync(data.Maker, preview.Reference, Metadata("preview-submit")));
        var approval = await service.PreviewApproveAsync(data.Checker, submitted.ProposalId, 1, Audit);
        Assert.Equal(MakerId, approval.Proposal!.Requester.UserId);
        var approved = await service.ApproveReviewedAsync(data.Checker, submitted.ProposalId, 1,
            approval.Reference, Metadata("preview-approve"));
        Assert.Equal("approved", approved.Status);
        Assert.Equal(approved, await service.ApproveReviewedAsync(data.Checker, submitted.ProposalId, 1,
            approval.Reference, Metadata("preview-approve")));
        Assert.Equal(2, await data.CountAsync("personReviewHistory"));
        Assert.Null(await data.Sessions.ReadAsync(data.Issued[AliasId].OpaqueToken));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.SubmitProposalAsync(data.Maker,
            preview.Reference, Metadata("preview-submit", "Changed original details")));
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("security")]
    [InlineData("source")]
    [InlineData("policy")]
    [InlineData("alias")]
    [InlineData("expired")]
    [InlineData("actor")]
    public async Task Unchanged_group_version_does_not_authorize_stale_or_foreign_preview(string changed)
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        var service = await PreviewServiceAsync(data);
        var preview = await service.PreviewProposeAsync(data.Maker, data.GroupId, 1, Audit);
        switch (changed)
        {
            case "profile": await data.Rows("accountProfiles").UpdateOneAsync(Id(AliasId), U.Set("displayName", "Changed name").Inc("version", 1L)); break;
            case "security": await data.Rows("identities").UpdateOneAsync(Id(AliasId), U.Inc("securityVersion", 1L)); break;
            case "source": await data.Rows("personEvidenceSources").UpdateOneAsync(Id(TargetSourceId), U.Inc("version", 1L)); break;
            case "policy": await data.Rows("personGovernancePolicies").UpdateOneAsync(Id(PolicyId), U.Inc("version", 1L)); break;
            case "alias": await data.AddTargetAliasAsync(8000); break;
            case "expired":
                await data.Rows("personReviewPreviews").UpdateOneAsync(new BsonDocument("reference", preview.Reference),
                    U.Set("expiresAt", data.Clock.UtcNow.AddSeconds(1).UtcDateTime));
                data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(2); break;
        }
        var error = await Record.ExceptionAsync(() => service.SubmitProposalAsync(changed == "actor" ? data.Checker : data.Maker,
            preview.Reference, Metadata("preview-stale")));
        Assert.True(error is AuthorizationDeniedException or PersistenceConflictException, error?.ToString());
        Assert.Equal(0, await data.CountAsync("personReviewProposals"));
        Assert.Equal(0, await data.CountAsync("idempotencyRecords"));
        Assert.Equal("ready", (await data.Rows("personReviewPreviews").Find(new BsonDocument("reference", preview.Reference)).SingleAsync())["status"].AsString);
    }

    [Fact]
    public async Task Preview_is_bound_to_session_and_cannot_be_consumed_with_a_second_key()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        var service = await PreviewServiceAsync(data);
        var preview = await service.PreviewProposeAsync(data.Maker, data.GroupId, 1, Audit);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.SubmitProposalAsync(
            data.Maker with { Session = data.Maker.Session! with { Version = 2 } }, preview.Reference, Metadata("changed-session")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.SubmitProposalAsync(
            data.Maker with { Session = null }, preview.Reference, Metadata("absent-session")));
        var submitted = await service.SubmitProposalAsync(data.Maker, preview.Reference, Metadata("first-key"));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.SubmitProposalAsync(data.Maker, preview.Reference, Metadata("second-key")));
        Assert.Equal(1, await data.CountAsync("personReviewProposals"));
        Assert.Equal(1, await data.CountAsync("personReviewHistory"));
        Assert.Equal(1, await data.CountAsync("idempotencyRecords"));
    }

    [Fact]
    public async Task Committed_preview_retry_does_not_require_unexpired_preview_or_current_ready_status()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        var service = await PreviewServiceAsync(data);
        var preview = await service.PreviewProposeAsync(data.Maker, data.GroupId, 1, Audit);
        var submitted = await service.SubmitProposalAsync(data.Maker, preview.Reference, Metadata("retry-expired"));
        await data.Rows("personReviewPreviews").UpdateOneAsync(new BsonDocument("reference", preview.Reference),
            U.Set("expiresAt", data.Clock.UtcNow.AddSeconds(1).UtcDateTime));
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(2);
        Assert.Equal(submitted, await service.SubmitProposalAsync(data.Maker, preview.Reference, Metadata("retry-expired")));
        Assert.Equal(submitted, await service.ReadOperationReceiptAsync(data.Maker, "person.review.propose", "retry-expired", RecoveryAudit));
        Assert.Equal(1, await data.CountAsync("personReviewHistory"));
    }

    [Fact]
    public async Task Approval_preview_pins_requester_label_and_original_proposal()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        var service = await PreviewServiceAsync(data);
        var proposed = await data.ProposeAsync();
        var preview = await service.PreviewApproveAsync(data.Checker, proposed.ProposalId, 1, Audit);
        await data.Rows("accountProfiles").UpdateOneAsync(Id(MakerId), U.Set("displayName", "Renamed requester").Inc("version", 1L));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.ApproveReviewedAsync(data.Checker,
            proposed.ProposalId, 1, preview.Reference, Metadata("stale-requester-label")));
        Assert.Equal("pending", (await data.RowAsync("personReviewProposals", proposed.ProposalId))["status"].AsString);
        Assert.Equal("active", (await data.RowAsync("personEquivalenceGroups", data.GroupId))["status"].AsString);
        Assert.Equal(1, await data.CountAsync("personReviewHistory"));
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("session")]
    [InlineData("person")]
    public async Task Preview_deadline_does_not_outlive_current_authority(string boundary)
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        var service = await PreviewServiceAsync(data);
        var end = data.Clock.UtcNow.AddSeconds(45);
        if (boundary == "policy") await data.Rows("personGovernancePolicies").UpdateOneAsync(Id(PolicyId), U.Set("expiresAt", end.UtcDateTime));
        else if (boundary == "session") await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Maker.Session!.Id), U.Set("idleExpiresAt", end.UtcDateTime));
        else await data.Rows("personAccountBindings").UpdateOneAsync(Id(MakerId), U.Set("expiresAt", end.UtcDateTime));
        var preview = await service.PreviewProposeAsync(data.Maker, data.GroupId, 1, Audit);
        Assert.True(preview.ExpiresAt <= end);
    }

    [Fact]
    public async Task Preview_expiry_during_receipt_write_rolls_back_consumption_and_effects()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        var service = await PreviewServiceAsync(data);
        var preview = await service.PreviewProposeAsync(data.Maker, data.GroupId, 1, Audit);
        var deadline = data.Clock.UtcNow.AddSeconds(1);
        await data.Rows("personReviewPreviews").UpdateOneAsync(new BsonDocument("reference", preview.Reference), U.Set("expiresAt", deadline.UtcDateTime));
        var observed = 0;
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => builder.Subscribe<MongoDB.Driver.Core.Events.CommandStartedEvent>(command =>
        {
            if (command.CommandName == "insert" && command.Command.GetValue("insert", BsonNull.Value) == "idempotencyRecords" &&
                Interlocked.Increment(ref observed) == 1) data.Clock.UtcNow = deadline;
        });
        var delayed = data.NewService(new MongoClient(settings), selection: db => new WorkflowSelection(db, data.GroupId,
            [MakerId, CheckerId, TargetId, AliasId]));
        var error = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => delayed.SubmitProposalAsync(data.Maker, preview.Reference, Metadata("late-expiry")));
        Assert.Equal("person_preview_expired", error.Code); Assert.Equal(1, observed);
        Assert.Equal(0, await data.CountAsync("personReviewProposals"));
        Assert.Equal(0, await data.CountAsync("personReviewHistory"));
        Assert.Equal(0, await data.CountAsync("idempotencyRecords"));
        Assert.Equal("ready", (await data.Rows("personReviewPreviews").Find(new BsonDocument("reference", preview.Reference)).SingleAsync())["status"].AsString);
    }

    [Fact]
    public async Task Uncertain_reviewed_commit_recovers_original_preview_receipt_without_a_second_effect()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        var original = await PreviewServiceAsync(data);
        var preview = await original.PreviewProposeAsync(data.Maker, data.GroupId, 1, Audit);
        var appName = "unittoko-preview-fault-" + Guid.NewGuid().ToString("N");
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ApplicationName = appName;
        var service = data.NewService(new MongoClient(settings), selection: db => new WorkflowSelection(db, data.GroupId,
            [MakerId, CheckerId, TargetId, AliasId]));
        const string key = "uncertain-reviewed-submit";
        try
        {
            await workflowFixture.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "configureFailPoint", "failCommand" }, { "mode", "alwaysOn" },
                { "data", new BsonDocument
                    {
                        { "appName", appName }, { "failCommands", new BsonArray { "commitTransaction" } },
                        { "writeConcernError", new BsonDocument { { "code", 64 }, { "errmsg", "injected uncertain preview commit reply" } } },
                        { "errorLabels", new BsonArray { "UnknownTransactionCommitResult" } }
                    }
                }
            });
            await Assert.ThrowsAsync<UncertainCommitException>(() => service.SubmitProposalAsync(data.Maker, preview.Reference, Metadata(key)));
            var receipt = await original.ReadOperationReceiptAsync(data.Maker, "person.review.propose", key, RecoveryAudit);
            Assert.NotNull(receipt); Assert.Equal("pending", receipt.Status);
            Assert.Equal("consumed", (await data.Rows("personReviewPreviews").Find(new BsonDocument("reference", preview.Reference)).SingleAsync())["status"].AsString);
        }
        finally
        {
            await workflowFixture.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "configureFailPoint", "failCommand" }, { "mode", "off" }
            });
        }
        var expected = await original.ReadOperationReceiptAsync(data.Maker, "person.review.propose", key, RecoveryAudit);
        Assert.Equal(expected, await service.SubmitProposalAsync(data.Maker, preview.Reference, Metadata(key)));
        Assert.Equal(1, await data.CountAsync("personReviewProposals"));
        Assert.Equal(1, await data.CountAsync("personReviewHistory"));
        Assert.Equal(1, await data.CountAsync("idempotencyRecords"));
    }
}
