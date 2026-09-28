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
    public async Task Missing_person_operation_remains_unconfirmed_until_explicitly_appointed_resolution()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        const string operation = "person.review.propose", key = "reload-body-lost";
        var missing = await data.Service.ReadOperationOutcomeAsync(data.Maker, operation, key, RecoveryAudit);
        Assert.Equal("not_confirmed", missing.State); Assert.Null(missing.Result); Assert.Null(missing.RecordedAt);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ResolveOperationAsync(data.Maker, operation, Metadata(key)));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.NewService(production: true)
            .ResolveOperationAsync(data.Maker, operation, Metadata(key)));
        Assert.Equal(0, await data.CountAsync("idempotencyRecords"));

        data.Appointments.ResolveUsers.Add(MakerId);
        var beforeEffects = await RecoveryBusinessStateAsync(data); beforeEffects.Remove("idempotencyRecords");
        var closed = await data.Service.ResolveOperationAsync(data.Maker, operation, Metadata(key, "Lost reply after reload"));
        Assert.Equal("closed_no_effect", closed.State); Assert.Null(closed.Result); Assert.Equal(data.Clock.UtcNow, closed.RecordedAt);
        var first = await RecoveryReceiptAsync(data, MakerId, operation, key);
        Assert.False(first.Contains("personWithdrawal"));
        Assert.Equal(new BsonDocument("receiptState", "closed_no_effect"), first["result"]);
        Assert.Equal("Lost reply after reload", first["personReviewClosure"]["reason"].AsString);
        Assert.Equal(1L, first["personReviewClosure"]["actorGrantVersion"].AsInt64);
        Assert.Equal(1L, first["personReviewClosure"]["actorSecurityVersion"].AsInt64);
        Assert.Equal(PolicyId, first["personReviewClosure"]["policy"]["id"].AsInt64);
        Assert.Equal(0, await data.CountAsync("personReviewProposals"));
        Assert.Equal(0, await data.CountAsync("personReviewHistory"));
        var afterEffects = await RecoveryBusinessStateAsync(data); afterEffects.Remove("idempotencyRecords");
        Assert.Equal(beforeEffects, afterEffects);
        Assert.Null(await data.Service.ReadOperationReceiptAsync(data.Maker, operation, key, RecoveryAudit));

        data.Clock.UtcNow += TimeSpan.FromSeconds(1);
        data.Appointments.ResolveUsers.Clear();
        await data.Rows("authorizationGrants").UpdateOneAsync(Id(MakerId),
            U.Set("permissions", new BsonArray { Permissions.PersonReviewRead }).Inc("version", 1L));
        Assert.Equal(closed, await data.Service.ResolveOperationAsync(data.Maker, operation, Metadata(key, "Later reason must not replace evidence")));
        Assert.Equal(closed, await data.Service.ReadOperationOutcomeAsync(data.Maker, operation, key, RecoveryAudit));
        Assert.Equal(first, await RecoveryReceiptAsync(data, MakerId, operation, key));
        Assert.Equal(1, await data.CountAsync("idempotencyRecords"));
        var reads = await data.Rows("authorizationReadEvents").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        Assert.DoesNotContain(reads, row => row.ToJson().Contains("Lost reply after reload", StringComparison.Ordinal));
        Assert.DoesNotContain(reads, row => row.ToJson().Contains(key, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("propose", false)]
    [InlineData("propose", true)]
    [InlineData("approve", false)]
    [InlineData("approve", true)]
    [InlineData("reject", false)]
    [InlineData("withdraw", false)]
    public async Task Closed_key_blocks_every_original_command_before_body_hash_and_without_business_effects(string action, bool reviewed)
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        var service = await PreviewServiceAsync(data);
        PersonWithdrawalResult? proposed = action == "propose" ? null : await data.ProposeAsync();
        var actor = action is "approve" or "reject" ? data.Checker : data.Maker;
        PersonReviewPreview? preview = reviewed ? action == "propose"
            ? await service.PreviewProposeAsync(actor, data.GroupId, 1, Audit)
            : await service.PreviewApproveAsync(actor, proposed!.ProposalId, 1, Audit) : null;
        data.Appointments.ResolveUsers.Add(actor.UserId);
        const string key = "permanently-closed-original-key";
        var closed = await service.ResolveOperationAsync(actor, "person.review." + action, Metadata(key));
        Assert.Equal("closed_no_effect", closed.State);
        var before = await PreviewRaceStateAsync(data);
        foreach (var reason in new[] { "Exact original reason", "A changed body must still be closed" })
        {
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => CommandAsync(Metadata(key, reason)));
            Assert.Equal("operation_closed", error.Code);
            Assert.Equal(before, await PreviewRaceStateAsync(data));
        }

        Task<PersonWithdrawalResult> CommandAsync(AuthorizationCommandMetadata metadata) => action switch
        {
            "propose" when reviewed => service.SubmitProposalAsync(actor, preview!.Reference, metadata),
            "propose" => service.ProposeAsync(actor, data.GroupId, 1, metadata),
            "approve" when reviewed => service.ApproveReviewedAsync(actor, proposed!.ProposalId, 1, preview!.Reference, metadata),
            "approve" => service.ApproveAsync(actor, proposed!.ProposalId, 1, metadata),
            "reject" => service.RejectAsync(actor, proposed!.ProposalId, 1, metadata),
            _ => service.WithdrawAsync(actor, proposed!.ProposalId, 1, metadata)
        };
    }

    [Fact]
    public async Task Committed_recovery_keeps_original_result_and_target_appointment_after_preview_expiry_and_suspension()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        var service = await PreviewServiceAsync(data);
        var proposed = await data.ProposeAsync();
        var preview = await service.PreviewApproveAsync(data.Checker, proposed.ProposalId, 1, Audit);
        const string operation = "person.review.approve", key = "committed-original";
        var approved = await service.ApproveReviewedAsync(data.Checker, proposed.ProposalId, 1, preview.Reference, Metadata(key));
        await data.Rows("personReviewPreviews").UpdateOneAsync(new BsonDocument("reference", preview.Reference),
            U.Set("expiresAt", data.Clock.UtcNow.AddSeconds(1).UtcDateTime));
        // The receipt timestamp belongs to the original business event, even if receipt insertion was later.
        await data.Rows("idempotencyRecords").UpdateOneAsync(new BsonDocument("key", key),
            U.Set("createdAt", data.Clock.UtcNow.AddMilliseconds(1).UtcDateTime));
        data.Clock.UtcNow += TimeSpan.FromSeconds(2);
        var receipt = await RecoveryReceiptAsync(data, CheckerId, operation, key);
        var outcome = await service.ResolveOperationAsync(data.Checker, operation, Metadata(key, "Lookup after reload"));
        Assert.Equal("committed", outcome.State); Assert.Equal(approved, outcome.Result); Assert.Equal(approved.RecordedAt, outcome.RecordedAt);
        Assert.Equal(outcome, await service.ReadOperationOutcomeAsync(data.Checker, operation, key, RecoveryAudit));
        Assert.Equal(approved, await service.ApproveReviewedAsync(data.Checker, proposed.ProposalId, 1, preview.Reference, Metadata(key)));
        var reused = await Assert.ThrowsAsync<PersistenceConflictException>(() => service.ApproveReviewedAsync(data.Checker,
            proposed.ProposalId, 1, preview.Reference, Metadata(key, "Different original command body")));
        Assert.Equal("idempotency_key_reused", reused.Code);
        Assert.Equal(receipt, await RecoveryReceiptAsync(data, CheckerId, operation, key));
        Assert.Equal("suspended", (await data.RowAsync("personEquivalenceGroups", data.GroupId))["status"].AsString);
        data.Appointments.DenyTargetReceiptReads = true;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ResolveOperationAsync(data.Checker, operation, Metadata(key)));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadOperationOutcomeAsync(data.Checker, operation, key, RecoveryAudit));
        Assert.Equal(receipt, await RecoveryReceiptAsync(data, CheckerId, operation, key));
    }

    [Theory]
    [InlineData("read-permission")]
    [InlineData("write-permission")]
    [InlineData("factor")]
    [InlineData("person")]
    [InlineData("session")]
    [InlineData("appointment")]
    public async Task Absent_operation_closure_requires_all_current_authority_and_rolls_back_on_denial(string missing)
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        data.Appointments.ResolveUsers.Add(MakerId);
        switch (missing)
        {
            case "read-permission": await data.Rows("authorizationGrants").UpdateOneAsync(Id(MakerId),
                U.Set("permissions", new BsonArray { Permissions.PersonReviewPropose }).Inc("version", 1L)); break;
            case "write-permission": await data.Rows("authorizationGrants").UpdateOneAsync(Id(MakerId),
                U.Set("permissions", new BsonArray { Permissions.PersonReviewRead }).Inc("version", 1L)); break;
            case "factor": data.Clock.UtcNow += TimeSpan.FromMinutes(6); break;
            case "person": await data.Rows("personAccountBindings").UpdateOneAsync(Id(MakerId), U.Set("status", "suspended")); break;
            case "session": await data.Sessions.RevokeAsync(data.Issued[MakerId].OpaqueToken, "synthetic_recovery_sign_out"); break;
            case "appointment": data.Appointments.ResolveUsers.Clear(); break;
        }
        var before = await PreviewRaceStateAsync(data);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ResolveOperationAsync(data.Maker,
            "person.review.propose", Metadata("authority-" + missing)));
        Assert.Equal(before, await PreviewRaceStateAsync(data));
    }

    [Fact]
    public async Task Receipt_only_resolution_binds_actor_operation_and_key_without_expanding_selection_or_disclosure()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        await WorkflowProfilesAsync(data);
        const string operation = "person.review.propose", key = "appointed-exact-key";
        data.Appointments.ResolveUsers.Add(MakerId);
        var selection = new BsonDocument { { "operation", operation }, { "keyHash", RecoveryHash(key) } };
        data.Appointments.RecoveryIntentDigests = [PersonWithdrawalTarget.Digest(selection, false)];
        var service = data.NewService(selection: db => new WorkflowSelection(db, data.GroupId, [], groups: false, proposals: false));
        var closed = await service.ResolveOperationAsync(data.Maker, operation, Metadata(key));
        Assert.Equal("closed_no_effect", closed.State);
        foreach (var changed in new[] { (Operation: "person.review.approve", Key: key), (Operation: operation, Key: "different-key") })
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ResolveOperationAsync(data.Maker, changed.Operation, Metadata(changed.Key)));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ResolveOperationAsync(data.Other, operation, Metadata(key)));
        var foreign = await service.ReadOperationOutcomeAsync(data.Other, operation, key, RecoveryAudit);
        Assert.Equal("not_confirmed", foreign.State); Assert.Null(foreign.Result); Assert.Null(foreign.RecordedAt);
        var targets = await service.SearchTargetsAsync(data.Maker, "Petugas", 0, 25, Audit);
        Assert.Equal(0, targets.Total); Assert.Empty(targets.Items);
        var proposals = await service.ListReviewsAsync(data.Maker, null, 0, 25, Audit);
        Assert.Equal(0, proposals.Total); Assert.Empty(proposals.Items);
        Assert.Equal(1, await data.CountAsync("idempotencyRecords"));
        Assert.Equal(0, await data.CountAsync("personReviewProposals"));
        Assert.Equal(0, await data.CountAsync("personReviewHistory"));
    }

    [Theory]
    [InlineData("outer-schema-type")]
    [InlineData("outer-schema-version")]
    [InlineData("outer-id-type")]
    [InlineData("outer-extra")]
    [InlineData("actor-type")]
    [InlineData("operation-type")]
    [InlineData("key-type")]
    [InlineData("date-type")]
    [InlineData("date-future")]
    [InlineData("date-range")]
    [InlineData("hash")]
    [InlineData("payload-missing")]
    [InlineData("payload-schema")]
    [InlineData("payload-schema-type")]
    [InlineData("grant-type")]
    [InlineData("security-zero")]
    [InlineData("policy-id-type")]
    [InlineData("policy-version-zero")]
    [InlineData("policy-digest")]
    [InlineData("policy-extra")]
    [InlineData("reason")]
    [InlineData("correlation")]
    [InlineData("payload-extra")]
    [InlineData("result-extra")]
    [InlineData("hash-alone")]
    [InlineData("access-marker")]
    [InlineData("mixed-committed-marker")]
    public async Task Malformed_person_closure_never_confirms_or_reopens_an_original_key(string defect)
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        const string operation = "person.review.propose", key = "tampered-closure";
        data.Appointments.ResolveUsers.Add(MakerId);
        await data.Service.ResolveOperationAsync(data.Maker, operation, Metadata(key));
        var receipt = await RecoveryReceiptAsync(data, MakerId, operation, key);
        var originalId = receipt["_id"];
        var payload = receipt["personReviewClosure"].AsBsonDocument;
        switch (defect)
        {
            case "outer-schema-type": receipt["schemaVersion"] = 1L; break;
            case "outer-schema-version": receipt["schemaVersion"] = 2; break;
            case "outer-id-type": receipt["_id"] = 123L; break;
            case "outer-extra": receipt["proposalId"] = 99L; break;
            case "actor-type": receipt["actorId"] = (int)MakerId; break;
            case "operation-type": receipt["operation"] = new BsonArray { operation }; break;
            case "key-type": receipt["key"] = new BsonArray { key }; break;
            case "date-type": receipt["createdAt"] = data.Clock.UtcNow.ToString("O"); break;
            case "date-future": receipt["createdAt"] = data.Clock.UtcNow.AddMinutes(1).UtcDateTime; break;
            case "date-range": receipt["createdAt"] = new BsonDateTime(long.MaxValue); break;
            case "hash": receipt["bodyHash"] = new string('a', 64); break;
            case "payload-missing": receipt.Remove("personReviewClosure"); break;
            case "payload-schema": payload["schemaVersion"] = 2; break;
            case "payload-schema-type": payload["schemaVersion"] = 1L; break;
            case "grant-type": payload["actorGrantVersion"] = 1; break;
            case "security-zero": payload["actorSecurityVersion"] = 0L; break;
            case "policy-id-type": payload["policy"]["id"] = (int)PolicyId; break;
            case "policy-version-zero": payload["policy"]["version"] = 0L; break;
            case "policy-digest": payload["policy"]["digest"] = new string('A', 64); break;
            case "policy-extra": payload["policy"]["appointed"] = true; break;
            case "reason": payload["reason"] = " untrimmed reason "; break;
            case "correlation": payload["correlationId"] = "unsafe\ncorrelation"; break;
            case "payload-extra": payload["proposalId"] = 99L; break;
            case "result-extra": receipt["result"]["proposalId"] = 99L; break;
            case "hash-alone": receipt.Remove("personReviewClosure"); receipt["result"] = new BsonDocument(); break;
            case "access-marker": receipt["result"]["receiptState"] = "closed"; break;
            case "mixed-committed-marker": receipt["personWithdrawal"] = new BsonDocument { { "schemaVersion", 1 }, { "proposalId", 99L } }; break;
        }
        if (defect == "outer-id-type")
        {
            await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("_id", originalId));
            await data.Rows("idempotencyRecords").InsertOneAsync(receipt, new InsertOneOptions { BypassDocumentValidation = true });
        }
        else await data.Rows("idempotencyRecords").ReplaceOneAsync(new BsonDocument("_id", originalId), receipt,
            new ReplaceOptions { BypassDocumentValidation = true });
        var before = await PreviewRaceStateAsync(data);
        foreach (var call in new Func<Task>[]
        {
            async () => { await data.Service.ReadOperationOutcomeAsync(data.Maker, operation, key, RecoveryAudit); },
            async () => { await data.Service.ResolveOperationAsync(data.Maker, operation, Metadata(key)); },
            async () => { await data.Service.ProposeAsync(data.Maker, data.GroupId, 1, Metadata(key)); }
        })
        {
            var error = await Assert.ThrowsAsync<AuthorizationDeniedException>(call);
            Assert.Equal("person_receipt_invalid", error.Code);
        }
        Assert.Equal(before, await PreviewRaceStateAsync(data));
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("result-state")]
    [InlineData("fixed-hash")]
    public async Task Committed_person_receipt_mixed_with_closure_evidence_never_becomes_a_terminal_outcome(string mixed)
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        await data.ProposeAsync();
        data.Appointments.ResolveUsers.Add(MakerId);
        await data.Service.ResolveOperationAsync(data.Maker, "person.review.propose", Metadata("closure-template"));
        var template = await RecoveryReceiptAsync(data, MakerId, "person.review.propose", "closure-template");
        var receipt = await RecoveryReceiptAsync(data, MakerId, "person.review.propose", "propose");
        if (mixed == "payload") receipt["personReviewClosure"] = template["personReviewClosure"].DeepClone();
        if (mixed == "result-state") receipt["result"]["receiptState"] = "closed_no_effect";
        if (mixed == "fixed-hash") receipt["bodyHash"] = template["bodyHash"];
        await data.Rows("idempotencyRecords").ReplaceOneAsync(new BsonDocument("_id", receipt["_id"]), receipt,
            new ReplaceOptions { BypassDocumentValidation = true });
        var before = await PreviewRaceStateAsync(data);
        foreach (var call in new Func<Task>[]
        {
            async () => { await data.Service.ReadOperationOutcomeAsync(data.Maker, "person.review.propose", "propose", RecoveryAudit); },
            async () => { await data.Service.ResolveOperationAsync(data.Maker, "person.review.propose", Metadata("propose")); },
            async () => { await data.ProposeAsync(); }
        })
            Assert.Equal("person_receipt_invalid", (await Assert.ThrowsAsync<AuthorizationDeniedException>(call)).Code);
        Assert.Equal(before, await PreviewRaceStateAsync(data));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Original_command_and_closure_race_converges_after_real_stale_snapshot_conflict(bool closureWins)
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        var ordinary = await PreviewServiceAsync(data);
        var proposed = await data.ProposeAsync();
        var preview = await ordinary.PreviewApproveAsync(data.Checker, proposed.ProposalId, 1, Audit);
        data.Appointments.ResolveUsers.Add(CheckerId);
        const string operation = "person.review.approve", key = "same-racing-original-key";
        using var snapshotReady = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var started = 0; var selectedRequest = 0; var observed = 0;
        var settings = RecoverySettings();
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.Command.GetValue("find", BsonNull.Value) == "identities" &&
                    command.Command.GetValue("startTransaction", BsonBoolean.False) == BsonBoolean.True)
                {
                    if (Interlocked.Increment(ref started) == 1) selectedRequest = command.RequestId;
                }
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (selectedRequest == 0 || command.RequestId != selectedRequest || Interlocked.CompareExchange(ref observed, 1, 0) != 0) return;
                snapshotReady.Set();
                if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("The winning transaction must complete before the stale snapshot resumes.");
            });
        };
        var delayed = data.NewService(new MongoClient(settings), selection: db => new WorkflowSelection(db, data.GroupId, WorkflowDisclosed()));
        Task<object> loser = Task.Run(async () =>
        {
            try
            {
                return closureWins
                    ? (object)await delayed.ApproveReviewedAsync(data.Checker, proposed.ProposalId, 1, preview.Reference, Metadata(key))
                    : await delayed.ResolveOperationAsync(data.Checker, operation, Metadata(key));
            }
            catch (PersistenceConflictException error) { return error; }
        });
        object winner;
        BsonDocument afterWinner;
        try
        {
            Assert.True(snapshotReady.Wait(TimeSpan.FromSeconds(15)), "The losing request must establish an actual Mongo snapshot.");
            winner = closureWins
                ? (object)await ordinary.ResolveOperationAsync(data.Checker, operation, Metadata(key))
                : await ordinary.ApproveReviewedAsync(data.Checker, proposed.ProposalId, 1, preview.Reference, Metadata(key));
            afterWinner = await RecoveryBusinessStateAsync(data);
        }
        finally { release.Set(); }
        var lost = await loser.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, Volatile.Read(ref observed));
        Assert.True(Volatile.Read(ref started) >= 2, "The losing transaction must retry with a fresh snapshot.");
        if (closureWins)
        {
            Assert.Equal("closed_no_effect", Assert.IsType<PersonReviewOperationOutcome>(winner).State);
            Assert.Equal("operation_closed", Assert.IsType<PersistenceConflictException>(lost).Code);
            Assert.Equal("pending", (await data.RowAsync("personReviewProposals", proposed.ProposalId))["status"].AsString);
            Assert.Equal("ready", (await data.Rows("personReviewPreviews").Find(new BsonDocument("reference", preview.Reference)).SingleAsync())["status"].AsString);
            foreach (var actorId in new[] { TargetId, AliasId }) Assert.Equal(1L, (await data.RowAsync("identities", actorId))["securityVersion"].AsInt64);
        }
        else
        {
            var recovered = Assert.IsType<PersonReviewOperationOutcome>(lost);
            Assert.Equal("committed", recovered.State); Assert.Equal(Assert.IsType<PersonWithdrawalResult>(winner), recovered.Result);
            Assert.Equal("approved", (await data.RowAsync("personReviewProposals", proposed.ProposalId))["status"].AsString);
        }
        Assert.Equal(afterWinner, await RecoveryBusinessStateAsync(data));
        Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", key)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Actual_uncertain_commit_reply_recovers_one_original_person_outcome(bool closing)
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        data.Appointments.ResolveUsers.Add(MakerId);
        const string operation = "person.review.propose", key = "uncertain-person-outcome";
        var settings = RecoverySettings(); settings.ApplicationName = "unittoko-person-recovery-" + Guid.NewGuid().ToString("N");
        var commits = 0;
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command =>
        {
            if (command.CommandName == "commitTransaction") Interlocked.Increment(ref commits);
        });
        var uncertain = data.NewService(new MongoClient(settings));
        PersonReviewOperationOutcome recovered;
        BsonDocument receipt;
        try
        {
            await workflowFixture.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "configureFailPoint", "failCommand" }, { "mode", "alwaysOn" },
                { "data", new BsonDocument
                    {
                        { "appName", settings.ApplicationName }, { "failCommands", new BsonArray { "commitTransaction" } },
                        { "writeConcernError", new BsonDocument { { "code", 64 }, { "errmsg", "injected uncertain person recovery reply" } } },
                        { "errorLabels", new BsonArray { "UnknownTransactionCommitResult" } }
                    }
                }
            });
            await Assert.ThrowsAsync<UncertainCommitException>(async () =>
            {
                if (closing) await uncertain.ResolveOperationAsync(data.Maker, operation, Metadata(key));
                else await uncertain.ProposeAsync(data.Maker, data.GroupId, 1, Metadata(key));
            });
            Assert.True(Volatile.Read(ref commits) >= MongoUnitOfWork.MaximumAttempts, "Actual uncertain replies must exhaust the commit retry budget.");
            var before = Volatile.Read(ref commits);
            recovered = await data.Service.ResolveOperationAsync(data.Maker, operation, Metadata(key));
            Assert.Equal(closing ? "closed_no_effect" : "committed", recovered.State);
            Assert.Equal(before, Volatile.Read(ref commits));
            receipt = await RecoveryReceiptAsync(data, MakerId, operation, key);
        }
        finally
        {
            await workflowFixture.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
                { { "configureFailPoint", "failCommand" }, { "mode", "off" } });
        }
        Assert.Equal(recovered, await uncertain.ResolveOperationAsync(data.Maker, operation, Metadata(key)));
        Assert.Equal(recovered, await uncertain.ReadOperationOutcomeAsync(data.Maker, operation, key, RecoveryAudit));
        Assert.Equal(receipt, await RecoveryReceiptAsync(data, MakerId, operation, key));
        Assert.Equal(1, await data.CountAsync("idempotencyRecords"));
        Assert.Equal(closing ? 0 : 1, await data.CountAsync("personReviewProposals"));
        Assert.Equal(closing ? 0 : 1, await data.CountAsync("personReviewHistory"));
    }

    [Theory]
    [InlineData("receipt", "policy")]
    [InlineData("audit", "permission")]
    [InlineData("session", "factor")]
    [InlineData("session", "person")]
    [InlineData("session", "session")]
    [InlineData("session", "policy")]
    public async Task Successful_final_closure_await_followed_by_authority_expiry_rolls_back_tombstone_audit_and_fences(string stage, string expiry)
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        data.Appointments.ResolveUsers.Add(MakerId);
        var deadline = data.Clock.UtcNow + (expiry == "factor" ? TimeSpan.FromMinutes(6) : TimeSpan.FromSeconds(1));
        switch (expiry)
        {
            case "policy": await data.Rows("personGovernancePolicies").UpdateOneAsync(Id(PolicyId), U.Set("expiresAt", deadline.UtcDateTime)); break;
            case "permission": await data.Rows("authorizationGrants").UpdateOneAsync(Id(MakerId), U.Set("expiresAt", deadline.UtcDateTime)); break;
            case "person": await data.Rows("personAccountBindings").UpdateOneAsync(Id(MakerId), U.Set("expiresAt", deadline.UtcDateTime)); break;
            case "session": await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Maker.Session!.Id), U.Set("idleExpiresAt", deadline.UtcDateTime)); break;
        }
        var before = await PreviewRaceStateAsync(data);
        var selectedRequest = 0; var receiptRequest = 0; var observed = 0; var successfulReceipts = 0;
        var settings = RecoverySettings();
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.Command.GetValue("insert", BsonNull.Value) == "idempotencyRecords")
                {
                    receiptRequest = command.RequestId;
                    if (stage == "receipt") selectedRequest = command.RequestId;
                }
                if (stage == "audit" && command.Command.GetValue("insert", BsonNull.Value) == "authorizationReadEvents") selectedRequest = command.RequestId;
                if (stage == "session" && command.Command.GetValue("find", BsonNull.Value) == "identitySessions" &&
                    command.Command.TryGetValue("projection", out var projection) && projection.AsBsonDocument.Contains("idleExpiresAt") &&
                    projection.AsBsonDocument.Contains("absoluteExpiresAt")) selectedRequest = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (receiptRequest != 0 && command.RequestId == receiptRequest) Interlocked.Increment(ref successfulReceipts);
                if (selectedRequest != 0 && command.RequestId == selectedRequest && Interlocked.CompareExchange(ref observed, 1, 0) == 0)
                    data.Clock.UtcNow = deadline;
            });
        };
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.NewService(new MongoClient(settings))
            .ResolveOperationAsync(data.Maker, "person.review.propose", Metadata("expiry-after-" + stage)));
        Assert.Equal(1, Volatile.Read(ref observed));
        Assert.Equal(1, Volatile.Read(ref successfulReceipts));
        Assert.Equal(before, await PreviewRaceStateAsync(data));
    }

    private static MongoClientSettings RecoverySettings()
    {
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10); return settings;
    }
    private static string RecoveryHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static Task<BsonDocument> RecoveryReceiptAsync(Data data, long actorId, string operation, string key) =>
        data.Rows("idempotencyRecords").Find(new BsonDocument { { "actorId", actorId }, { "operation", operation }, { "key", key } }).SingleAsync();
    private static async Task<BsonDocument> RecoveryBusinessStateAsync(Data data)
    {
        var state = await PreviewRaceStateAsync(data);
        foreach (var collection in new[] { "personGovernancePolicies", "authorizationReadEvents", "authorizationGrants", "memberships" }) state.Remove(collection);
        // Successful recovery takes current authority fences without changing the original business state.
        foreach (var collection in new[] { "identities", "identitySessions", "personAccountBindings", "personRecords", "personEquivalenceGroups", "personEvidenceSources" })
        foreach (var row in state[collection].AsBsonArray) row.AsBsonDocument.Remove("fence");
        return state;
    }
}
