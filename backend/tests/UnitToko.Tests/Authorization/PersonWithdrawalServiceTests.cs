using System.Collections.Immutable;
using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed partial class PersonWithdrawalServiceTests(MongoFixture fixture)
{
    private MongoFixture workflowFixture => fixture;
    private const long MakerId = 3000, CheckerId = 4000, TargetId = 5000, AliasId = 6000, OtherId = 7000;
    private const long PolicyId = 100, TargetSourceId = 810001;
    private static readonly UpdateDefinitionBuilder<BsonDocument> U = Builders<BsonDocument>.Update;
    private static BsonDocument Id(long id) => new("_id", id);
    private static AuthorizationCommandMetadata Metadata(string key, string reason = "Synthetic independent withdrawal review") => new(key, reason, "withdrawal-service-fixture");
    private static AuthorizationReadMetadata Audit => new("access-review", "withdrawal-read-fixture");
    private static AuthorizationReadMetadata RecoveryAudit => new("operation-recovery", "withdrawal-recovery-fixture");

    [Fact]
    public async Task Production_default_denies_even_explicit_app_permissions_and_person_evidence()
    {
        await using var data = await Data.CreateAsync(fixture);
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.NewService(production: true).ProposeAsync(data.Maker, data.GroupId, 1, Metadata("default-deny")));
        Assert.Equal("person_governance_unavailable", denied.Code);
        Assert.Equal(0, await data.CountAsync("personReviewProposals"));
        Assert.Equal(0, await data.CountAsync("personReviewHistory"));
        Assert.Equal(0, await data.CountAsync("idempotencyRecords"));
        Assert.Equal(1L, (await data.RowAsync("identities", TargetId))["grantVersion"].AsInt64);
    }

    [Fact]
    public async Task Independent_approval_atomically_suspends_group_invalidates_all_aliases_and_preserves_original_provenance()
    {
        await using var data = await Data.CreateAsync(fixture);
        var originalGroup = await data.RowAsync("personEquivalenceGroups", data.GroupId);
        var proposed = await data.ProposeAsync();
        Assert.Equal("pending", proposed.Status); Assert.Equal(1L, proposed.Version);
        Assert.Equal(1L, (await data.RowAsync("identities", TargetId))["grantVersion"].AsInt64);

        var approved = await data.Service.ApproveAsync(data.Checker, proposed.ProposalId, 1, Metadata("approve"));
        Assert.Equal("approved", approved.Status); Assert.Equal(2L, approved.Version); Assert.Equal(2L, approved.GroupVersion);
        var group = await data.RowAsync("personEquivalenceGroups", data.GroupId);
        var unchanged = group.DeepClone().AsBsonDocument;
        foreach (var field in new[] { "status", "version", "fence" }) unchanged[field] = originalGroup[field];
        Assert.Equal(originalGroup, unchanged);
        Assert.Equal("suspended", group["status"].AsString);
        foreach (var id in new long[] { TargetId, AliasId })
        {
            var account = await data.RowAsync("identities", id);
            Assert.Equal(2L, account["grantVersion"].AsInt64); Assert.Equal(2L, account["securityVersion"].AsInt64);
            Assert.Equal("active", account["status"].AsString); Assert.Equal("subject-" + id, account["subject"].AsString);
            Assert.Null(await data.Sessions.ReadAsync(data.Issued[id].OpaqueToken));
            Assert.All(await data.Rows("identitySessions").Find(new BsonDocument("userId", id)).ToListAsync(),
                row => Assert.Equal("revoked", row["status"].AsString));
        }
        Assert.Equal(1L, (await data.RowAsync("identities", MakerId))["grantVersion"].AsInt64);
        Assert.Equal(1L, (await data.RowAsync("identities", CheckerId))["securityVersion"].AsInt64);
        var histories = await data.Rows("personReviewHistory").Find(new BsonDocument("proposalId", proposed.ProposalId))
            .Sort(new BsonDocument("transitionVersion", 1)).ToListAsync();
        Assert.Equal(new[] { "proposed", "approved" }, histories.Select(row => row["event"].AsString));
        Assert.Equal(2, histories.Count);
        Assert.Equal(2, (await data.RowAsync("personReviewProposals", proposed.ProposalId))["resolution"]["affectedRevisions"].AsBsonArray.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejection_or_withdrawal_changes_review_only_without_account_epochs(bool withdraw)
    {
        await using var data = await Data.CreateAsync(fixture);
        var proposal = await data.ProposeAsync();
        var result = withdraw
            ? await data.Service.WithdrawAsync(data.Maker, proposal.ProposalId, 1, Metadata("withdraw"))
            : await data.Service.RejectAsync(data.Checker, proposal.ProposalId, 1, Metadata("reject"));
        Assert.Equal(withdraw ? "withdrawn" : "rejected", result.Status);
        Assert.Equal(1L, result.GroupVersion);
        Assert.Equal("active", (await data.RowAsync("personEquivalenceGroups", data.GroupId))["status"].AsString);
        foreach (var id in new long[] { TargetId, AliasId })
        {
            Assert.Equal(1L, (await data.RowAsync("identities", id))["grantVersion"].AsInt64);
            Assert.NotNull(await data.Sessions.ReadAsync(data.Issued[id].OpaqueToken));
        }
        Assert.Equal(2, await data.CountAsync("personReviewHistory"));
        Assert.Empty((await data.RowAsync("personReviewProposals", proposal.ProposalId))["resolution"]["affectedRevisions"].AsBsonArray);
    }

    [Theory]
    [InlineData("same-account")]
    [InlineData("same-person")]
    [InlineData("affected-account")]
    public async Task App_permissions_and_appointments_do_not_allow_self_review(string conflict)
    {
        await using var data = await Data.CreateAsync(fixture);
        if (conflict == "same-person") await data.MergeGroupsAsync(MakerId, CheckerId);
        var proposal = await data.ProposeAsync();
        var actor = conflict == "same-account" ? data.Maker : conflict == "affected-account" ? data.Target : data.Checker;
        // In this fault-injection case the adapter supplies a permit even for the beneficiary;
        // the writer must still enforce the non-delegable independent-review invariant.
        data.Appointments.AllowAffectedReview = true;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.ApproveAsync(actor, proposal.ProposalId, 1, Metadata("self-review")));
        Assert.Equal("pending", (await data.RowAsync("personReviewProposals", proposal.ProposalId))["status"].AsString);
        Assert.Equal(1, await data.CountAsync("personReviewHistory"));
        Assert.Equal("active", (await data.RowAsync("personEquivalenceGroups", data.GroupId))["status"].AsString);
    }

    [Fact]
    public async Task Affected_person_may_propose_only_when_synthetic_policy_explicitly_allows_it()
    {
        await using var data = await Data.CreateAsync(fixture);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.ProposeAsync(data.Target, data.GroupId, 1, Metadata("self-propose-denied")));
        data.Appointments.AllowSelfPropose = true;
        var proposal = await data.Service.ProposeAsync(data.Target, data.GroupId, 1, Metadata("self-propose-allowed"));
        Assert.Equal("pending", proposal.Status);
        var approved = await data.Service.ApproveAsync(data.Checker, proposal.ProposalId, 1, Metadata("self-proposal-independent-check"));
        Assert.Equal("approved", approved.Status);
        Assert.Null(await data.Sessions.ReadAsync(data.Issued[TargetId].OpaqueToken));
    }

    [Theory]
    [InlineData("grant-version")]
    [InlineData("security-version")]
    [InlineData("membership")]
    [InlineData("permission")]
    [InlineData("person")]
    [InlineData("policy")]
    [InlineData("appointment")]
    [InlineData("expired-policy")]
    [InlineData("missing-policy")]
    [InlineData("withdrawn-policy")]
    public async Task Approval_revalidates_original_requester_and_reviewed_policy(string change)
    {
        await using var data = await Data.CreateAsync(fixture);
        var proposal = await data.ProposeAsync();
        switch (change)
        {
            case "grant-version": await data.Rows("identities").UpdateOneAsync(Id(MakerId), U.Inc("grantVersion", 1L)); break;
            case "security-version": await data.Rows("identities").UpdateOneAsync(Id(MakerId), U.Inc("securityVersion", 1L)); break;
            case "membership": await data.Rows("memberships").UpdateOneAsync(Id(MakerId), U.Set("status", "suspended").Inc("version", 1L)); break;
            case "permission": await data.Rows("authorizationGrants").UpdateOneAsync(Id(MakerId),
                U.Set("permissions", new BsonArray { Permissions.PersonReviewRead }).Inc("version", 1L)); break;
            case "person": await data.Rows("personAccountBindings").UpdateOneAsync(Id(MakerId), U.Inc("version", 1L)); break;
            case "policy": await data.Rows("personGovernancePolicies").UpdateOneAsync(Id(PolicyId), U.Inc("version", 1L)); break;
            case "appointment": data.Appointments.Users.Remove(MakerId); break;
            case "missing-policy": await data.Rows("personGovernancePolicies").DeleteOneAsync(Id(PolicyId)); break;
            case "withdrawn-policy": await data.Rows("personGovernancePolicies").UpdateOneAsync(Id(PolicyId), U.Set("status", "revoked")); break;
            case "expired-policy": await data.Rows("personGovernancePolicies").UpdateOneAsync(Id(PolicyId),
                U.Set("expiresAt", data.Clock.UtcNow.AddMilliseconds(-1).UtcDateTime)); break;
        }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.ApproveAsync(data.Checker, proposal.ProposalId, 1, Metadata("stale-requester")));
        Assert.Equal("pending", (await data.RowAsync("personReviewProposals", proposal.ProposalId))["status"].AsString);
        Assert.Equal(1L, (await data.RowAsync("identities", TargetId))["grantVersion"].AsInt64);
        Assert.Equal(1, await data.CountAsync("personReviewHistory"));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("issuer")]
    [InlineData("subject")]
    [InlineData("schema-type")]
    [InlineData("provider-type")]
    public async Task Malformed_requester_identity_cannot_authorize_approval(string defect)
    {
        await using var data = await Data.CreateAsync(fixture);
        var proposal = await data.ProposeAsync();
        var update = defect switch
        {
            "schema" => U.Set("schemaVersion", 2),
            "issuer" => U.Set("issuer", " "),
            "subject" => U.Set("subject", " "),
            "schema-type" => U.Set("schemaVersion", 1L),
            _ => U.Set("issuer", new BsonArray { "https://approved.example/" })
        };
        await data.Rows("identities").UpdateOneAsync(Id(MakerId), update, new UpdateOptions { BypassDocumentValidation = true });
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.ApproveAsync(data.Checker, proposal.ProposalId, 1, Metadata("malformed-requester")));
        Assert.Equal("pending", (await data.RowAsync("personReviewProposals", proposal.ProposalId))["status"].AsString);
        Assert.Equal(1L, (await data.RowAsync("identities", TargetId))["grantVersion"].AsInt64);
        Assert.Equal(1, await data.CountAsync("personReviewHistory"));
    }

    [Fact]
    public async Task Expired_disputed_target_can_be_withdrawn_by_current_independent_reviewers()
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.Rows("personEvidenceSources").UpdateOneAsync(Id(TargetSourceId),
            U.Set("status", "revoked").Inc("version", 1L).Set("adapterId", "synthetic-adapter-v2")
                .Set("expiresAt", data.Clock.UtcNow.AddHours(-1).UtcDateTime));
        await data.Rows("personRecords").UpdateOneAsync(Id(PersonTestData.PersonId(TargetId)), U.Set("status", "disputed"));
        await data.Rows("personEquivalenceGroups").UpdateOneAsync(Id(data.GroupId), U.Set("status", "disputed"));
        await data.Rows("personAccountBindings").UpdateManyAsync(new BsonDocument("personId", PersonTestData.PersonId(TargetId)),
            U.Set("verification", "disputed").Set("status", "suspended").Set("expiresAt", data.Clock.UtcNow.AddHours(-1).UtcDateTime));
        await data.Rows("personEvidenceVersions").UpdateOneAsync(Id(PersonTestData.EvidenceId(TargetId)),
            U.Set("expiresAt", data.Clock.UtcNow.AddHours(-1).UtcDateTime));
        var proposal = await data.ProposeAsync();
        var approved = await data.Service.ApproveAsync(data.Checker, proposal.ProposalId, 1, Metadata("expired-target-approve"));
        Assert.Equal("approved", approved.Status);
        Assert.Equal("suspended", (await data.RowAsync("personEquivalenceGroups", data.GroupId))["status"].AsString);
        Assert.Equal(2L, (await data.RowAsync("identities", AliasId))["securityVersion"].AsInt64);
    }

    [Theory]
    [InlineData("target-identity")]
    [InlineData("target-source")]
    [InlineData("late-alias")]
    public async Task Semantic_target_or_complete_closure_change_requires_new_proposal(string change)
    {
        await using var data = await Data.CreateAsync(fixture);
        var proposal = await data.ProposeAsync();
        if (change == "target-identity") await data.Rows("identities").UpdateOneAsync(Id(AliasId), U.Inc("securityVersion", 1L));
        else if (change == "target-source") await data.Rows("personEvidenceSources").UpdateOneAsync(Id(TargetSourceId), U.Inc("version", 1L));
        else await data.AddTargetAliasAsync(8000);
        await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Service.ApproveAsync(data.Checker, proposal.ProposalId, 1, Metadata("changed-target")));
        Assert.Equal("pending", (await data.RowAsync("personReviewProposals", proposal.ProposalId))["status"].AsString);
        Assert.Equal("active", (await data.RowAsync("personEquivalenceGroups", data.GroupId))["status"].AsString);
        Assert.Equal(1, await data.CountAsync("personReviewHistory"));
    }

    [Fact]
    public async Task Uncertain_approval_commit_recovers_receipt_and_same_key_without_duplicate_withdrawal_effects()
    {
        await using var data = await Data.CreateAsync(fixture);
        var proposal = await data.ProposeAsync();
        const string key = "uncertain-approval";
        var appName = "unittoko-withdrawal-fault-" + Guid.NewGuid().ToString("N");
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ApplicationName = appName;
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
        var commits = 0;
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command =>
        {
            if (command.CommandName == "commitTransaction") Interlocked.Increment(ref commits);
        });
        var service = data.NewService(new MongoClient(settings));
        var expected = new PersonWithdrawalResult(proposal.ProposalId, 2, "approved", data.GroupId, 2, data.Clock.UtcNow);
        BsonDocument committed;
        try
        {
            // As in CommitFaultTests, failCommand is restricted to a unique client appName.
            // The Mongo collection serializes changes to this server-owned failpoint.
            await fixture.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "configureFailPoint", "failCommand" }, { "mode", "alwaysOn" },
                { "data", new BsonDocument
                    {
                        { "appName", appName }, { "failCommands", new BsonArray { "commitTransaction" } },
                        { "writeConcernError", new BsonDocument { { "code", 64 }, { "errmsg", "injected uncertain withdrawal commit reply" } } },
                        { "errorLabels", new BsonArray { "UnknownTransactionCommitResult" } }
                    }
                }
            });
            var error = await Assert.ThrowsAsync<UncertainCommitException>(() =>
                service.ApproveAsync(data.Checker, proposal.ProposalId, 1, Metadata(key)));
            Assert.Contains("same idempotency key", error.Message);
            var uncertainCommits = Volatile.Read(ref commits);
            Assert.True(uncertainCommits >= MongoUnitOfWork.MaximumAttempts,
                "The approval client must exhaust actual uncertain commit replies.");

            // writeConcernError alters the reply after execution. The unaffected fixture
            // client independently proves the transaction committed while the fault is active.
            committed = await CommittedStateAsync();
            Assert.Equal(expected, await data.Service.ReadOperationReceiptAsync(data.Checker,
                "person.review.approve", key, RecoveryAudit));
            Assert.Equal(uncertainCommits, Volatile.Read(ref commits));
            Assert.Equal(committed, await CommittedStateAsync());
            foreach (var id in new long[] { TargetId, AliasId })
                Assert.Null(await data.Sessions.ReadAsync(data.Issued[id].OpaqueToken));
        }
        finally
        {
            // Cleanup also runs after assertion or command failure, before the disposable DB is dropped.
            await fixture.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
                { { "configureFailPoint", "failCommand" }, { "mode", "off" } });
        }

        Assert.Equal(expected, await service.ApproveAsync(data.Checker, proposal.ProposalId, 1, Metadata(key)));
        Assert.Equal(expected, await service.ReadOperationReceiptAsync(data.Checker,
            "person.review.approve", key, RecoveryAudit));
        Assert.Equal(committed, await CommittedStateAsync());

        async Task<BsonDocument> CommittedStateAsync()
        {
            var group = await data.RowAsync("personEquivalenceGroups", data.GroupId);
            var storedProposal = await data.RowAsync("personReviewProposals", proposal.ProposalId);
            var accounts = new BsonArray();
            foreach (var id in new long[] { TargetId, AliasId })
            {
                var account = await data.RowAsync("identities", id);
                Assert.Equal(2L, account["grantVersion"].AsInt64);
                Assert.Equal(2L, account["securityVersion"].AsInt64);
                Assert.Equal("active", account["status"].AsString);
                Assert.Equal("subject-" + id, account["subject"].AsString);
                accounts.Add(account);
            }
            var sessions = await data.Rows("identitySessions").Find(new BsonDocument("userId",
                new BsonDocument("$in", new BsonArray { TargetId, AliasId }))).Sort(new BsonDocument("_id", 1)).ToListAsync();
            var histories = await data.Rows("personReviewHistory").Find(new BsonDocument("proposalId", proposal.ProposalId))
                .Sort(new BsonDocument("transitionVersion", 1)).ToListAsync();
            var receipts = await data.Rows("idempotencyRecords").Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(new BsonDocument("_id", 1)).ToListAsync();
            Assert.Equal("suspended", group["status"].AsString); Assert.Equal(2L, group["version"].AsInt64);
            Assert.Equal("approved", storedProposal["status"].AsString); Assert.Equal(2L, storedProposal["version"].AsInt64);
            Assert.Equal(2, sessions.Count); Assert.All(sessions, row => Assert.Equal("revoked", row["status"].AsString));
            Assert.Equal(new[] { "proposed", "approved" }, histories.Select(row => row["event"].AsString));
            Assert.Equal(2, await data.CountAsync("personReviewHistory"));
            Assert.Equal(2, receipts.Count);
            Assert.Single(receipts, row => row["operation"] == "person.review.approve" && row["key"] == key);
            return new BsonDocument
            {
                { "group", group }, { "proposal", storedProposal }, { "accounts", accounts },
                { "sessions", new BsonArray(sessions) }, { "history", new BsonArray(histories) }, { "receipts", new BsonArray(receipts) }
            };
        }
    }

    [Fact]
    public async Task Same_body_replay_returns_original_transition_after_suspension_without_duplicate_effects()
    {
        await using var data = await Data.CreateAsync(fixture);
        var proposal = await data.ProposeAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Service.ApproveAsync(data.Checker, proposal.ProposalId, 2, Metadata("wrong-expected-version")));
        Assert.Equal(1, await data.CountAsync("personReviewHistory"));
        var approved = await data.Service.ApproveAsync(data.Checker, proposal.ProposalId, 1, Metadata("approve-retry"));
        Assert.Equal(approved, await data.Service.ApproveAsync(data.Checker, proposal.ProposalId, 1, Metadata("approve-retry")));
        Assert.Equal(proposal, await data.Service.ProposeAsync(data.Maker, data.GroupId, 1, Metadata("propose")));
        Assert.Equal(2L, (await data.RowAsync("identities", TargetId))["grantVersion"].AsInt64);
        Assert.Equal(2, await data.CountAsync("personReviewHistory"));
        var conflict = await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Service.ApproveAsync(data.Checker, proposal.ProposalId, 1, Metadata("approve-retry", "Different request reason")));
        Assert.Equal("idempotency_key_reused", conflict.Code);
        await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Service.ApproveAsync(data.Other, proposal.ProposalId, 1, Metadata("duplicate-approval")));
        Assert.Equal(2, await data.CountAsync("personReviewHistory"));
    }

    [Fact]
    public async Task Receipt_recovery_remains_actor_bound_and_requires_current_read_permission_and_appointment()
    {
        await using var data = await Data.CreateAsync(fixture);
        var proposal = await data.ProposeAsync();
        var approved = await data.Service.ApproveAsync(data.Checker, proposal.ProposalId, 1, Metadata("approve-recovery"));
        Assert.Equal(approved, await data.Service.ReadOperationReceiptAsync(data.Checker, "person.review.approve", "approve-recovery", RecoveryAudit));
        Assert.Null(await data.Service.ReadOperationReceiptAsync(data.Other, "person.review.approve", "approve-recovery", RecoveryAudit));
        var detail = await data.Service.ReadAsync(data.Checker, proposal.ProposalId, Audit);
        Assert.Equal(approved, detail.Result); Assert.Equal(new long[] { TargetId, AliasId }, detail.AffectedUserIds);
        var serialized = System.Text.Json.JsonSerializer.Serialize(detail);
        Assert.DoesNotContain("private-person", serialized); Assert.DoesNotContain("private-source", serialized);
        Assert.DoesNotContain("sourceDigest", serialized); Assert.DoesNotContain("manifestDigest", serialized);
        data.Appointments.Users.Remove(CheckerId);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.ReadOperationReceiptAsync(data.Checker, "person.review.approve", "approve-recovery", RecoveryAudit));
        data.Appointments.Users.Add(CheckerId);
        await data.Rows("authorizationGrants").UpdateOneAsync(Id(CheckerId),
            U.Set("permissions", new BsonArray { Permissions.PersonReviewApprove }).Inc("version", 1L));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.ReadOperationReceiptAsync(data.Checker, "person.review.approve", "approve-recovery", RecoveryAudit));
    }

    [Fact]
    public async Task Creating_a_proposal_does_not_grant_private_read_access()
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.Rows("authorizationGrants").UpdateOneAsync(Id(MakerId),
            U.Set("permissions", new BsonArray { Permissions.PersonReviewPropose }).Inc("version", 1L));
        var proposal = await data.ProposeAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Maker, proposal.ProposalId, Audit));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.ReadOperationReceiptAsync(data.Maker, "person.review.propose", "propose", RecoveryAudit));
        Assert.Equal("pending", (await data.RowAsync("personReviewProposals", proposal.ProposalId))["status"].AsString);
    }

    [Theory]
    [InlineData("factor")]
    [InlineData("session")]
    [InlineData("policy")]
    public async Task Expiry_after_successful_receipt_insert_rolls_back_suspension_epochs_history_and_result(string expiry)
    {
        await using var data = await Data.CreateAsync(fixture);
        var expiryAt = data.Clock.UtcNow + (expiry == "factor" ? TimeSpan.FromMinutes(6) : TimeSpan.FromSeconds(1));
        if (expiry == "policy") await data.Rows("personGovernancePolicies").UpdateOneAsync(Id(PolicyId), U.Set("expiresAt", expiryAt.UtcDateTime));
        if (expiry == "session") await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Checker.Session!.Id), U.Set("idleExpiresAt", expiryAt.UtcDateTime));
        var proposal = await data.ProposeAsync();
        var beforeAccounts = await data.Rows("identities").Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray { TargetId, AliasId })))
            .Sort(new BsonDocument("_id", 1)).ToListAsync();
        var beforeGroup = await data.RowAsync("personEquivalenceGroups", data.GroupId);
        var observed = false; var requestId = 0;
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.Command.TryGetValue("insert", out var name) && name == "idempotencyRecords" &&
                    command.Command["documents"].AsBsonArray.Any(row => row["operation"] == "person.review.approve"))
                    requestId = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (requestId == 0 || command.RequestId != requestId || observed) return;
                observed = true; data.Clock.UtcNow = expiryAt;
            });
        };
        var service = data.NewService(new MongoClient(settings));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            service.ApproveAsync(data.Checker, proposal.ProposalId, 1, Metadata("expiry-after-receipt")));
        Assert.True(observed, "The server must accept the receipt insert before the clock expires.");
        Assert.Equal(beforeGroup, await data.RowAsync("personEquivalenceGroups", data.GroupId));
        Assert.Equal(beforeAccounts, await data.Rows("identities").Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray { TargetId, AliasId })))
            .Sort(new BsonDocument("_id", 1)).ToListAsync());
        Assert.Equal("pending", (await data.RowAsync("personReviewProposals", proposal.ProposalId))["status"].AsString);
        Assert.Equal(1, await data.CountAsync("personReviewHistory"));
        Assert.Null(await data.Rows("idempotencyRecords").Find(new BsonDocument("key", "expiry-after-receipt")).FirstOrDefaultAsync());
    }

    [Fact]
    public async Task Concurrent_independent_decisions_commit_only_one_terminal_history_and_one_invalidation()
    {
        await using var data = await Data.CreateAsync(fixture);
        var proposal = await data.ProposeAsync();
        async Task<Exception?> Decide(ActorContext actor, string key)
        {
            try { await data.Service.ApproveAsync(actor, proposal.ProposalId, 1, Metadata(key)); return null; }
            catch (Exception exception) { return exception; }
        }
        var results = await Task.WhenAll(Decide(data.Checker, "concurrent-one"), Decide(data.Other, "concurrent-two"));
        Assert.Single(results, error => error is null);
        Assert.IsType<PersistenceConflictException>(Assert.Single(results, error => error is not null));
        Assert.Equal(2, await data.CountAsync("personReviewHistory"));
        Assert.Equal(2L, (await data.RowAsync("identities", TargetId))["securityVersion"].AsInt64);
        Assert.Equal(2L, (await data.RowAsync("identities", AliasId))["grantVersion"].AsInt64);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sensitive_business_approval_and_withdrawal_serialize_without_retroactively_changing_committed_history(bool withdrawFirst)
    {
        await using var data = await Data.CreateAsync(fixture);
        var shop = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
        await data.Store.AddGrantAsync(data.Target, 9000, shop, [Permissions.PaymentVerify]);
        await data.Rows("payments").InsertOneAsync(new BsonDocument
        {
            { "_id", 1L }, { "schemaVersion", 1 }, { "version", 1L }, { "recordedBy", OtherId },
            { "fulfillmentOrganizationId", 100L }, { "shopId", 101L }, { "channelId", 103L }
        });
        var proposal = await data.ProposeAsync();
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        var business = data.Store.Store.ExecuteAuthorizedAsync(data.Target, "fixture.payment.verify", Permissions.PaymentVerify,
            Metadata("business-approval"), new { Id = 1L, ExpectedVersion = 1L },
            async (tx, ct) =>
            {
                var payment = await data.Rows("payments").Find(tx, Id(1)).SingleAsync(ct);
                read.TrySetResult();
                if (withdrawFirst) await resume.Task.WaitAsync(ct);
                return new(AuthorizationBson.Resource(payment), new(payment["version"].AsInt64, 1, RecordedBy: OtherId));
            },
            async (tx, ct) =>
            {
                writes++;
                await MongoUnitOfWork.RequireVersionAsync(data.Rows("payments"), tx, Id(1), 1, U.Set("approved", true), ct);
                return new BsonDocument("version", 2L);
            });
        await read.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (withdrawFirst)
        {
            try { await data.Service.ApproveAsync(data.Checker, proposal.ProposalId, 1, Metadata("withdraw-before-business")); }
            finally { resume.TrySetResult(); }
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => business);
            Assert.Equal(0, writes);
        }
        else
        {
            await business;
            await data.Service.ApproveAsync(data.Checker, proposal.ProposalId, 1, Metadata("withdraw-after-business"));
            Assert.Equal(1, writes);
        }
        var record = await data.RowAsync("payments", 1);
        Assert.Equal(!withdrawFirst, record.Contains("approved"));
        Assert.Equal(withdrawFirst ? 1L : 2L, record["version"].AsInt64);
        Assert.Equal("suspended", (await data.RowAsync("personEquivalenceGroups", data.GroupId))["status"].AsString);
    }

    [Fact]
    public async Task Private_permit_is_bound_to_actor_action_intent_database_and_transaction_attempt()
    {
        await using var data = await Data.CreateAsync(fixture);
        var proposal = await data.ProposeAsync();
        var row = await data.RowAsync("personReviewProposals", proposal.ProposalId);
        var intent = new PersonWithdrawalIntent(proposal.ProposalId, row["requestDigest"].AsString, row["target"].AsBsonDocument);
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction();
        var policy = await data.Rows("personGovernancePolicies").Find(tx, Id(PolicyId)).SingleAsync();
        var permit = new PersonWithdrawalPermit(data.Database, tx, CheckerId, "approve", intent, policy);
        policy["status"] = "revoked";
        Assert.Throws<AuthorizationDeniedException>(() => permit.RequireCurrent(tx, MakerId, "approve", intent, data.Clock.UtcNow));
        Assert.Throws<AuthorizationDeniedException>(() => permit.RequireCurrent(tx, CheckerId, "reject", intent, data.Clock.UtcNow));
        Assert.Throws<AuthorizationDeniedException>(() => permit.RequireCurrent(tx, CheckerId, "approve", intent with { ProposalId = proposal.ProposalId + 1 }, data.Clock.UtcNow));
        Assert.Throws<AuthorizationDeniedException>(() => permit.RequireCurrent(tx, CheckerId, "approve", intent with { RequestDigest = new string('f', 64) }, data.Clock.UtcNow));
        var otherTarget = intent.Target.DeepClone().AsBsonDocument; otherTarget["groupVersion"] = 99L;
        Assert.Throws<AuthorizationDeniedException>(() => permit.RequireCurrent(tx, CheckerId, "approve", intent with { Target = otherTarget }, data.Clock.UtcNow));
        using var otherTx = await fixture.Client.StartSessionAsync(); otherTx.StartTransaction();
        Assert.Throws<AuthorizationDeniedException>(() => permit.RequireCurrent(otherTx, CheckerId, "approve", intent, data.Clock.UtcNow));
        await otherTx.AbortTransactionAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => permit.TouchAsync(fixture.Client.GetDatabase("wrong_policy_database"),
            tx, CheckerId, "approve", intent, data.Clock, default));
        // Policy input and exposed pin mutations cannot alter the captured proof.
        var pin = permit.Pin; pin["version"] = 99L;
        await permit.TouchAsync(data.Database, tx, CheckerId, "approve", intent, data.Clock, default);
        Assert.Equal(1L, permit.Pin["version"].AsInt64);
        await tx.AbortTransactionAsync();
        tx.StartTransaction();
        Assert.Throws<AuthorizationDeniedException>(() => permit.RequireCurrent(tx, CheckerId, "approve", intent, data.Clock.UtcNow));
        await tx.AbortTransactionAsync();
    }

    [Theory]
    [InlineData("owner-type")]
    [InlineData("link-extra")]
    [InlineData("link-type")]
    [InlineData("link-history")]
    [InlineData("result-extra")]
    [InlineData("result-status")]
    [InlineData("history-request")]
    [InlineData("history-resolution")]
    public async Task Tampered_private_receipt_or_history_cannot_replay_or_recover_an_authorized_result(string mutation)
    {
        await using var data = await Data.CreateAsync(fixture);
        var proposal = await data.ProposeAsync();
        await data.Service.ApproveAsync(data.Checker, proposal.ProposalId, 1, Metadata("tamper-receipt"));
        var filter = new BsonDocument { { "actorId", CheckerId }, { "operation", "person.review.approve" }, { "key", "tamper-receipt" } };
        var receipt = await data.Rows("idempotencyRecords").Find(filter).SingleAsync();
        var historyId = receipt["personWithdrawal"]["historyId"].AsInt64;
        if (mutation == "history-request")
            await data.Rows("personReviewHistory").UpdateOneAsync(Id(historyId), U.Set("proposal.reason", "Tampered original request"),
                new UpdateOptions { BypassDocumentValidation = true });
        else if (mutation == "history-resolution")
            await data.Rows("personReviewHistory").UpdateOneAsync(Id(historyId),
                U.Set("reason", "Tampered terminal decision").Set("proposal.resolution.reason", "Tampered terminal decision"),
                new UpdateOptions { BypassDocumentValidation = true });
        else
        {
            var update = mutation switch
            {
                "owner-type" => U.Set("actorId", new BsonArray { CheckerId }),
                "link-extra" => U.Set("personWithdrawal.untrusted", true),
                "link-type" => U.Set("personWithdrawal.proposalId", checked((int)proposal.ProposalId)),
                "link-history" => U.Set("personWithdrawal.historyId", (await data.Rows("personReviewHistory")
                    .Find(new BsonDocument { { "proposalId", proposal.ProposalId }, { "transitionVersion", 1L } }).SingleAsync())["_id"]),
                "result-extra" => U.Set("result.untrusted", true),
                _ => U.Set("result.status", "rejected")
            };
            await data.Rows("idempotencyRecords").UpdateOneAsync(filter, update, new UpdateOptions { BypassDocumentValidation = true });
        }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.ApproveAsync(data.Checker, proposal.ProposalId, 1, Metadata("tamper-receipt")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.ReadOperationReceiptAsync(data.Checker, "person.review.approve", "tamper-receipt", RecoveryAudit));
        Assert.Equal("approved", (await data.RowAsync("personReviewProposals", proposal.ProposalId))["status"].AsString);
        Assert.Equal(2L, (await data.RowAsync("identities", TargetId))["grantVersion"].AsInt64);
        Assert.Equal(2, await data.CountAsync("personReviewHistory"));
    }

    [Theory]
    [InlineData("replay", "factor")]
    [InlineData("replay", "session")]
    [InlineData("replay", "policy")]
    [InlineData("read", "session")]
    [InlineData("read", "policy")]
    public async Task Expiry_after_final_replay_or_recovery_await_does_not_release_a_result(string operation, string expiry)
    {
        await using var data = await Data.CreateAsync(fixture);
        var expiryAt = data.Clock.UtcNow + (expiry == "factor" ? TimeSpan.FromMinutes(6) : TimeSpan.FromSeconds(1));
        if (expiry == "policy") await data.Rows("personGovernancePolicies").UpdateOneAsync(Id(PolicyId), U.Set("expiresAt", expiryAt.UtcDateTime));
        if (expiry == "session") await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Checker.Session!.Id), U.Set("idleExpiresAt", expiryAt.UtcDateTime));
        var proposal = await data.ProposeAsync();
        await data.Service.ApproveAsync(data.Checker, proposal.ProposalId, 1, Metadata("await-recovery"));
        var beforeGroup = await data.RowAsync("personEquivalenceGroups", data.GroupId);
        var beforeAudits = await data.CountAsync("authorizationReadEvents");
        var beforeIdentity = await data.RowAsync("identities", CheckerId);
        var observed = false; var requestId = 0;
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (operation == "read" && command.Command.TryGetValue("insert", out var inserted) && inserted == "authorizationReadEvents")
                    requestId = command.RequestId;
                if (operation == "replay" && command.Command.TryGetValue("find", out var found) && found == "identitySessions" &&
                    command.Command.TryGetValue("projection", out var projection) &&
                    projection.AsBsonDocument.Contains("idleExpiresAt") && projection.AsBsonDocument.Contains("absoluteExpiresAt"))
                    requestId = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (requestId == 0 || command.RequestId != requestId || observed) return;
                observed = true; data.Clock.UtcNow = expiryAt;
            });
        };
        var service = data.NewService(new MongoClient(settings));
        if (operation == "replay")
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
                service.ApproveAsync(data.Checker, proposal.ProposalId, 1, Metadata("await-recovery")));
        else
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
                service.ReadOperationReceiptAsync(data.Checker, "person.review.approve", "await-recovery", RecoveryAudit));
        Assert.True(observed, "The selected final server operation must complete before expiry.");
        Assert.Equal(beforeGroup, await data.RowAsync("personEquivalenceGroups", data.GroupId));
        Assert.Equal(beforeIdentity, await data.RowAsync("identities", CheckerId));
        Assert.Equal(beforeAudits, await data.CountAsync("authorizationReadEvents"));
        Assert.Equal(2, await data.CountAsync("personReviewHistory"));
        Assert.Equal(2, await data.CountAsync("idempotencyRecords"));
        Assert.Equal(2L, (await data.RowAsync("identities", TargetId))["securityVersion"].AsInt64);
    }

    private sealed class Appointments
    {
        internal HashSet<long> Users { get; } = [MakerId, CheckerId, TargetId, AliasId, OtherId];
        internal bool AllowSelfPropose { get; set; }
        internal bool AllowAffectedReview { get; set; }
    }

    // Synthetic reviewed-policy implementation only. Persisted policy heads alone never appoint anyone.
    private sealed class SyntheticTrust(IMongoDatabase database, Appointments appointments, long targetGroupId) : IPersonWithdrawalTrust
    {
        public async Task<PersonWithdrawalPermit> RequireAsync(IClientSessionHandle tx, long userId, string action,
            PersonWithdrawalIntent intent, CancellationToken ct)
        {
            if (!tx.IsInTransaction || !appointments.Users.Contains(userId) ||
                action is not ("previewPropose" or "propose" or "approve" or "reject" or "withdraw" or "read" or "receiptRead"))
                throw new AuthorizationDeniedException("synthetic_appointment_unavailable");
            if (intent.Target.ElementCount == 0 && action is "read" or "receiptRead")
            {
                // Appointed lookup permission precedes record lookup; actual stored targets
                // are separately checked below when the service asks for their permit.
            }
            else if (intent.ProposalId > 0 || action == "previewPropose")
            {
                if (!intent.Target.TryGetValue("groupId", out var group) || !group.IsInt64 || group.AsInt64 != targetGroupId ||
                    intent.RequestDigest.Length != 64 || intent.RequestDigest.Any(character => !char.IsAsciiHexDigitLower(character)))
                    throw new AuthorizationDeniedException("synthetic_target_unavailable");
                var affected = intent.Target["userIds"].AsBsonArray.Any(id => id.IsInt64 && id.AsInt64 == userId);
                if (affected && (action is "previewPropose" or "propose" && !appointments.AllowSelfPropose ||
                    action is "approve" or "reject" && !appointments.AllowAffectedReview))
                    throw new AuthorizationDeniedException("synthetic_target_conflict");
            }
            else if (action != "receiptRead") throw new AuthorizationDeniedException("synthetic_target_unavailable");
            var policy = await database.GetCollection<BsonDocument>("personGovernancePolicies").Find(tx, Id(PolicyId)).SingleOrDefaultAsync(ct)
                ?? throw new AuthorizationDeniedException("synthetic_policy_unavailable");
            return new(database, tx, userId, action, intent, policy);
        }
    }

    private sealed class Data : IAsyncDisposable
    {
        private readonly MongoClient client;
        internal StoreTestData Store { get; }
        internal IMongoDatabase Database => Store.Database;
        internal MutableAuthorizationClock Clock => Store.Clock;
        internal MongoUnitOfWork Unit { get; }
        internal MongoIdentitySessionStore Sessions { get; }
        internal PersonWithdrawalService Service { get; }
        internal Appointments Appointments { get; } = new();
        internal Dictionary<long, SessionIssue> Issued { get; } = [];
        internal ActorContext Maker { get; private set; } = null!;
        internal ActorContext Checker { get; private set; } = null!;
        internal ActorContext Target { get; private set; } = null!;
        internal ActorContext Other { get; private set; } = null!;
        internal long GroupId => PersonTestData.GroupId(TargetId);
        private SessionPayloadProtector Protector { get; } = SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider());
        private SessionPolicy SessionPolicy { get; } = new("https://approved.example/", "fixture-connection", "fixture-policy");
        private Data(StoreTestData store, MongoFixture fixture)
        {
            Store = store; client = fixture.Client; Unit = new(client);
            Sessions = new(Database, Unit, Clock, SessionPolicy, Protector);
            Service = NewService();
        }
        internal PersonWithdrawalService NewService(MongoClient? selected = null, bool production = false, Func<IMongoDatabase, IPersonReviewSelectionTrust>? selection = null)
        {
            var database = (selected ?? client).GetDatabase(Database.DatabaseNamespace.DatabaseName);
            var unit = new MongoUnitOfWork(selected ?? client);
            var sessions = new MongoIdentitySessionStore(database, unit, Clock, SessionPolicy, Protector);
            return production ? new(database, unit, new(database), Clock, sessions) :
                new(database, unit, new(database), Clock, sessions, new SyntheticTrust(database, Appointments, GroupId), selection?.Invoke(database));
        }
        internal static async Task<Data> CreateAsync(MongoFixture fixture)
        {
            var data = new Data(await StoreTestData.CreateAsync(fixture), fixture);
            data.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var actors = new Dictionary<long, ActorContext>();
            foreach (var id in new long[] { MakerId, CheckerId, TargetId, AliasId, OtherId })
                actors[id] = await data.Store.AddActorAsync(id, new(GrantScopeKind.System),
                    Permissions.PersonReviewPropose, Permissions.PersonReviewApprove, Permissions.PersonReviewRead);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, MakerId, CheckerId, TargetId, OtherId);
            var source = PersonTestData.Source(data.Clock.UtcNow);
            source["_id"] = TargetSourceId; source["key"] = "synthetic-target-source";
            await data.Rows("personEvidenceSources").InsertOneAsync(source);
            await data.Rows("personRecords").UpdateOneAsync(Id(PersonTestData.PersonId(TargetId)), U.Set("sourceId", TargetSourceId));
            await data.Rows("personEvidenceVersions").UpdateOneAsync(Id(PersonTestData.EvidenceId(TargetId)), U.Set("sourceId", TargetSourceId));
            var alias = PersonTestData.Binding(AliasId, data.Clock.UtcNow);
            alias["personId"] = PersonTestData.PersonId(TargetId); alias["evidenceId"] = PersonTestData.EvidenceId(TargetId);
            await data.Rows("personAccountBindings").InsertOneAsync(alias);
            await data.Rows("personEquivalenceGroups").UpdateOneAsync(Id(data.GroupId), U.Set("memberUserIds", new BsonArray { TargetId, AliasId }));
            await data.Rows("personGovernancePolicies").InsertOneAsync(new BsonDocument
            {
                { "_id", PolicyId }, { "schemaVersion", 1 }, { "status", "active" }, { "version", 1L }, { "fence", 0L },
                { "manifestReference", "synthetic-reviewed-manifest" }, { "manifestDigest", new string('a', 64) },
                { "effectiveAt", data.Clock.UtcNow.AddDays(-1).UtcDateTime }, { "expiresAt", data.Clock.UtcNow.AddDays(1).UtcDateTime }
            });
            foreach (var (id, actor) in actors.ToArray())
            {
                await data.Rows("identities").UpdateOneAsync(Id(id), U.Set("securityVersion", 1L));
                var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "fixture@example.invalid", true,
                    data.SessionPolicy.TrustedConnectionId, data.Clock.UtcNow, data.Clock.UtcNow, "otp", data.SessionPolicy.AssurancePolicyVersion);
                var issued = await data.Unit.ExecuteAsync((tx, ct) => data.Sessions.CreateAsync(tx,
                    new(id, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic-private-provider-payload", ct));
                data.Issued[id] = issued; actors[id] = actor with { Session = issued.Session.Reference };
            }
            data.Maker = actors[MakerId]; data.Checker = actors[CheckerId]; data.Target = actors[TargetId]; data.Other = actors[OtherId];
            return data;
        }
        internal Task<PersonWithdrawalResult> ProposeAsync() => Service.ProposeAsync(Maker, GroupId, 1, Metadata("propose"));
        internal IMongoCollection<BsonDocument> Rows(string collection) => Database.GetCollection<BsonDocument>(collection);
        internal Task<BsonDocument> RowAsync(string collection, long id) => Rows(collection).Find(Id(id)).SingleAsync();
        internal Task<long> CountAsync(string collection) => Rows(collection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        internal async Task MergeGroupsAsync(long left, long right)
        {
            await Rows("personRecords").UpdateOneAsync(Id(PersonTestData.PersonId(right)), U.Set("currentEquivalenceId", PersonTestData.GroupId(left)));
            await Rows("personEquivalenceGroups").UpdateOneAsync(Id(PersonTestData.GroupId(right)), U.Set("status", "superseded"));
            await Rows("personEquivalenceGroups").UpdateOneAsync(Id(PersonTestData.GroupId(left)),
                U.Set("memberUserIds", new BsonArray(new[] { left, right }.Order()))
                    .Set("memberPersonIds", new BsonArray(new[] { PersonTestData.PersonId(left), PersonTestData.PersonId(right) }.Order())).Inc("version", 1L));
        }
        internal async Task AddTargetAliasAsync(long userId)
        {
            await Unit.ExecuteAsync(async (tx, ct) =>
            {
                await Rows("identities").InsertOneAsync(tx, new BsonDocument
                {
                    { "_id", userId }, { "schemaVersion", 1 }, { "issuer", "https://approved.example/" }, { "subject", "subject-" + userId },
                    { "status", "active" }, { "grantVersion", 1L }, { "securityVersion", 1L }, { "fence", 0L }
                }, cancellationToken: ct);
                var binding = PersonTestData.Binding(userId, Clock.UtcNow);
                binding["personId"] = PersonTestData.PersonId(TargetId); binding["evidenceId"] = PersonTestData.EvidenceId(TargetId);
                await Rows("personAccountBindings").InsertOneAsync(tx, binding, cancellationToken: ct);
                await Rows("personEquivalenceGroups").UpdateOneAsync(tx, Id(GroupId),
                    U.Set("memberUserIds", new BsonArray(new[] { TargetId, AliasId, userId }.Order())).Inc("version", 1L).Inc("fence", 1L), cancellationToken: ct);
                return true;
            });
        }
        public ValueTask DisposeAsync() => Store.DisposeAsync();
    }
}
