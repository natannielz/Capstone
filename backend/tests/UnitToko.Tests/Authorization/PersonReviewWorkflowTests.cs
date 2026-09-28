using MongoDB.Bson;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Identity;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;

namespace UnitToko.Tests.Authorization;

public sealed partial class PersonWithdrawalServiceTests
{
    [Fact]
    public async Task Governance_discovery_requires_separately_appointed_visibility_even_for_system_grantees()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.NewService(production: true).SearchTargetsAsync(data.Maker, "Petugas", 0, 25, Audit));
        Assert.Equal("person_governance_unavailable", denied.Code);
        Assert.Equal(0, await data.CountAsync("personReviewPreviews"));
    }

    [Fact]
    public async Task Governance_search_filters_scope_and_all_aliases_before_counts_and_pages()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        await WorkflowProfilesAsync(data);
        var disclosed = new HashSet<long> { MakerId, CheckerId, TargetId, AliasId };
        var service = data.NewService(selection: db => new WorkflowSelection(db, data.GroupId, disclosed));
        var page = await service.SearchTargetsAsync(data.Maker, "Petugas", 0, 1, Audit);
        Assert.Equal(1L, page.Total);
        var target = Assert.Single(page.Items);
        Assert.Equal(data.GroupId, target.GroupId);
        Assert.Equal(new long[] { TargetId, AliasId }, target.Accounts.Select(account => account.UserId));
        Assert.DoesNotContain(target.Accounts, account => account.UserId == MakerId);
        Assert.Empty((await service.SearchTargetsAsync(data.Maker, "Petugas", 1, 1, Audit)).Items);
        disclosed.Remove(AliasId);
        page = await service.SearchTargetsAsync(data.Maker, "Petugas", 0, 25, Audit);
        Assert.Equal(0L, page.Total); Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Governance_queue_and_detail_need_proposal_group_and_complete_profile_visibility()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        await WorkflowProfilesAsync(data);
        var proposed = await data.ProposeAsync();
        var disclosed = new HashSet<long> { MakerId, CheckerId, TargetId, AliasId };
        var service = data.NewService(selection: db => new WorkflowSelection(db, data.GroupId, disclosed));
        var page = await service.ListReviewsAsync(data.Checker, "pending", 0, 25, Audit);
        Assert.Equal(proposed.ProposalId, Assert.Single(page.Items).Result.ProposalId);
        var detail = await service.ReadReviewAsync(data.Checker, proposed.ProposalId, Audit);
        Assert.Equal("Petugas " + MakerId, detail.Summary.Requester.DisplayName);
        Assert.Equal(2, detail.Accounts.Length);
        Assert.Contains("approve", detail.Summary.AllowedActions);
        await data.Service.ApproveAsync(data.Checker, proposed.ProposalId, 1, Metadata("discovery-approve"));
        detail = await service.ReadReviewAsync(data.Checker, proposed.ProposalId, Audit);
        Assert.Equal("approved", detail.Summary.Result.Status);
        Assert.Equal(CheckerId, detail.Decision!.Actor.UserId);
        Assert.Empty(detail.Summary.AllowedActions);
        disclosed.Remove(AliasId);
        Assert.Equal(0L, (await service.ListReviewsAsync(data.Checker, null, 0, 25, Audit)).Total);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadReviewAsync(data.Checker, proposed.ProposalId, Audit));
    }


    [Theory]
    [InlineData("missing")]
    [InlineData("version-int32")]
    [InlineData("blank-name")]
    [InlineData("control-name")]
    [InlineData("unknown-field")]
    [InlineData("unverified-date")]
    [InlineData("future-date")]
    [InlineData("uncanonical-email")]
    [InlineData("invalid-email")]
    [InlineData("malformed-identity")]
    public async Task Governance_discovery_requires_complete_strict_profiles_before_disclosure(string damage)
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        await WorkflowProfilesAsync(data);
        if (damage == "missing") await data.Rows("accountProfiles").DeleteOneAsync(Id(AliasId));
        else if (damage == "malformed-identity")
            await data.Rows("identities").UpdateOneAsync(Id(AliasId), U.Set("issuer", new BsonArray { "https://approved.example/" }),
                new UpdateOptions { BypassDocumentValidation = true });
        else
        {
            var update = damage switch
            {
                "version-int32" => U.Set("version", 1),
                "blank-name" => U.Set("displayName", " "),
                "control-name" => U.Set("displayName", "Petugas\nHidden"),
                "unknown-field" => U.Set("privateSource", "private-raw-source"),
                "unverified-date" => U.Set("verifiedAt", BsonNull.Value),
                "future-date" => U.Set("updatedAt", data.Clock.UtcNow.AddDays(1).UtcDateTime),
                "uncanonical-email" => U.Set("verifiedEmail", "PERSON@example.invalid"),
                _ => U.Set("verifiedEmail", "invalid-address")
            };
            await data.Rows("accountProfiles").UpdateOneAsync(Id(AliasId), update, new UpdateOptions { BypassDocumentValidation = true });
        }
        var before = await data.RowAsync("personEquivalenceGroups", data.GroupId);
        var service = data.NewService(selection: db => new WorkflowSelection(db, data.GroupId, WorkflowDisclosed()));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.SearchTargetsAsync(data.Maker, "Petugas", 0, 25, Audit));
        Assert.Equal(before, await data.RowAsync("personEquivalenceGroups", data.GroupId));
        Assert.Equal(0, await data.CountAsync("authorizationReadEvents"));
    }

    [Theory]
    [InlineData("requester")]
    [InlineData("alias")]
    [InlineData("decision")]
    [InlineData("group")]
    [InlineData("proposal")]
    public async Task Governance_hidden_reference_or_participant_is_filtered_before_profile_join_and_count(string hidden)
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        // Intentionally no profiles: denied scope must not join, require or expose them.
        var proposed = await data.ProposeAsync();
        if (hidden == "decision")
            await data.Service.ApproveAsync(data.Checker, proposed.ProposalId, 1, Metadata("hidden-decision"));
        var disclosed = WorkflowDisclosed();
        disclosed.Remove(hidden switch { "requester" => MakerId, "alias" => AliasId, "decision" => CheckerId, _ => 0 });
        var profileRead = false;
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command =>
        {
            if (command.Command.TryGetValue("find", out var found) && found == "accountProfiles") profileRead = true;
        });
        var service = data.NewService(new MongoClient(settings), selection: db =>
            new WorkflowSelection(db, data.GroupId, disclosed, groups: hidden != "group", proposals: hidden != "proposal"));
        var page = await service.ListReviewsAsync(data.Other, null, 0, 25, Audit);
        Assert.Equal(0L, page.Total); Assert.Empty(page.Items);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadReviewAsync(data.Other, proposed.ProposalId, Audit));
        Assert.False(profileRead, "Hidden requests must be removed before all profile joins and counts.");
    }

    [Fact]
    public async Task Governance_target_search_keeps_expired_disputed_structure_inspectable_and_uses_verified_names()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        await WorkflowProfilesAsync(data);
        await data.Rows("personEvidenceSources").UpdateOneAsync(Id(TargetSourceId),
            U.Set("status", "revoked").Set("expiresAt", data.Clock.UtcNow.AddHours(-1).UtcDateTime));
        await data.Rows("personRecords").UpdateOneAsync(Id(PersonTestData.PersonId(TargetId)), U.Set("status", "disputed"));
        await data.Rows("personEquivalenceGroups").UpdateOneAsync(Id(data.GroupId), U.Set("status", "disputed"));
        await data.Rows("personAccountBindings").UpdateManyAsync(new BsonDocument("personId", PersonTestData.PersonId(TargetId)),
            U.Set("verification", "disputed").Set("status", "suspended").Set("expiresAt", data.Clock.UtcNow.AddHours(-1).UtcDateTime));
        await data.Rows("personEvidenceVersions").UpdateOneAsync(Id(PersonTestData.EvidenceId(TargetId)),
            U.Set("expiresAt", data.Clock.UtcNow.AddHours(-1).UtcDateTime));
        await data.Rows("identities").UpdateOneAsync(Id(AliasId), U.Set("status", "suspended"));
        await data.Rows("accountProfiles").UpdateOneAsync(Id(AliasId), U.Set("displayName", BsonNull.Value));
        var service = data.NewService(selection: db => new WorkflowSelection(db, data.GroupId, WorkflowDisclosed()));
        var page = await service.SearchTargetsAsync(data.Maker, "PERSON-6000@EXAMPLE.INVALID", 0, 25, Audit);
        var target = Assert.Single(page.Items);
        Assert.Equal("disputed", target.Status);
        var alias = Assert.Single(target.Accounts, account => account.UserId == AliasId);
        Assert.Null(alias.DisplayName); Assert.Equal("suspended", alias.Status);
        Assert.Equal(0L, (await service.SearchTargetsAsync(data.Maker, data.GroupId.ToString(), 0, 25, Audit)).Total);
        Assert.Equal(0L, (await service.SearchTargetsAsync(data.Maker, ".*", 0, 25, Audit)).Total);
        var response = System.Text.Json.JsonSerializer.Serialize(page);
        Assert.DoesNotContain("private-source", response);
        Assert.DoesNotContain("sourceSubjectKey", response);
        Assert.DoesNotContain("policyReference", response);
        var auditText = (await data.Rows("authorizationReadEvents").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync()).ToJson();
        Assert.DoesNotContain("PERSON-6000", auditText);
        Assert.DoesNotContain("private-source", auditText);
    }

    [Theory]
    [InlineData("grant-version")]
    [InlineData("security-version")]
    [InlineData("membership")]
    [InlineData("permission")]
    [InlineData("person")]
    [InlineData("session")]
    public async Task Governance_discovery_requires_current_acting_authority(string change)
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        await WorkflowProfilesAsync(data);
        switch (change)
        {
            case "grant-version": await data.Rows("identities").UpdateOneAsync(Id(MakerId), U.Inc("grantVersion", 1L)); break;
            case "security-version": await data.Rows("identities").UpdateOneAsync(Id(MakerId), U.Inc("securityVersion", 1L)); break;
            case "membership": await data.Rows("memberships").UpdateOneAsync(Id(MakerId), U.Set("status", "suspended")); break;
            case "permission": await data.Rows("authorizationGrants").UpdateOneAsync(Id(MakerId), U.Set("permissions", new BsonArray { Permissions.PersonReviewPropose })); break;
            case "person": await data.Rows("personAccountBindings").UpdateOneAsync(Id(MakerId), U.Set("status", "suspended")); break;
            case "session": await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Maker.Session!.Id), U.Set("status", "revoked").Set("revokedAt", data.Clock.UtcNow.UtcDateTime).Set("revocationReason", "synthetic-review-revocation")); break;
        }
        var service = data.NewService(selection: db => new WorkflowSelection(db, data.GroupId, WorkflowDisclosed()));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.SearchTargetsAsync(data.Maker, "Petugas", 0, 25, Audit));
        Assert.Equal(0, await data.CountAsync("authorizationReadEvents"));
    }

    [Theory]
    [InlineData("audit", "policy")]
    [InlineData("audit", "selection")]
    [InlineData("audit", "person")]
    [InlineData("audit", "grant")]
    [InlineData("session", "idle")]
    [InlineData("session", "absolute")]
    public async Task Governance_discovery_expiry_after_final_await_rolls_back_result_and_fences(string stage, string expiry)
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        await WorkflowProfilesAsync(data);
        var expiryAt = data.Clock.UtcNow.AddSeconds(1);
        if (expiry == "policy") await data.Rows("personGovernancePolicies").UpdateOneAsync(Id(PolicyId), U.Set("expiresAt", expiryAt.UtcDateTime));
        if (expiry == "person") await data.Rows("personAccountBindings").UpdateOneAsync(Id(MakerId), U.Set("expiresAt", expiryAt.UtcDateTime));
        if (expiry == "grant") await data.Rows("authorizationGrants").UpdateOneAsync(Id(MakerId), U.Set("expiresAt", expiryAt.UtcDateTime));
        if (expiry is "idle" or "absolute") await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Maker.Session!.Id),
            U.Set(expiry == "idle" ? "idleExpiresAt" : "absoluteExpiresAt", expiryAt.UtcDateTime));
        var beforeGroup = await data.RowAsync("personEquivalenceGroups", data.GroupId);
        var beforePolicy = await data.RowAsync("personGovernancePolicies", PolicyId);
        var beforeIdentity = await data.RowAsync("identities", MakerId);
        var observed = false; var requestId = 0;
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (stage == "audit" && command.Command.TryGetValue("insert", out var inserted) && inserted == "authorizationReadEvents")
                    requestId = command.RequestId;
                if (stage == "session" && command.Command.TryGetValue("find", out var found) && found == "identitySessions" &&
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
        var service = data.NewService(new MongoClient(settings), selection: db =>
            new WorkflowSelection(db, data.GroupId, WorkflowDisclosed(), until: expiry == "selection" ? expiryAt : null));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.SearchTargetsAsync(data.Maker, "Petugas", 0, 25, Audit));
        Assert.True(observed, "The selected final server operation must complete before expiry.");
        Assert.Equal(beforeGroup, await data.RowAsync("personEquivalenceGroups", data.GroupId));
        Assert.Equal(beforePolicy, await data.RowAsync("personGovernancePolicies", PolicyId));
        Assert.Equal(beforeIdentity, await data.RowAsync("identities", MakerId));
        Assert.Equal(0, await data.CountAsync("authorizationReadEvents"));
    }

    [Fact]
    public async Task Governance_pending_actions_follow_current_independent_appointments_but_history_survives_withdrawal()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        await WorkflowProfilesAsync(data);
        var proposal = await data.ProposeAsync();
        var service = data.NewService(selection: db => new WorkflowSelection(db, data.GroupId, WorkflowDisclosed()));
        Assert.Equal(new[] { "withdraw" }, (await service.ReadReviewAsync(data.Maker, proposal.ProposalId, Audit)).Summary.AllowedActions);
        data.Appointments.Users.Remove(CheckerId);
        Assert.Empty((await service.ReadReviewAsync(data.Checker, proposal.ProposalId, Audit)).Summary.AllowedActions);
        data.Appointments.Users.Add(CheckerId);
        data.Appointments.Users.Remove(MakerId);
        var unavailableRequester = (await service.ReadReviewAsync(data.Checker, proposal.ProposalId, Audit)).Summary;
        Assert.DoesNotContain("approve", unavailableRequester.AllowedActions);
        Assert.Contains("reject", unavailableRequester.AllowedActions);
        data.Appointments.Users.Add(MakerId);
        await data.Service.WithdrawAsync(data.Maker, proposal.ProposalId, 1, Metadata("withdraw-history"));
        await data.Rows("personAccountBindings").UpdateOneAsync(Id(MakerId), U.Set("status", "suspended"));
        var history = await service.ReadReviewAsync(data.Checker, proposal.ProposalId, Audit);
        Assert.Equal("withdrawn", history.Summary.Result.Status);
        Assert.Equal(MakerId, history.Decision!.Actor.UserId);
        Assert.Empty(history.Summary.AllowedActions);
    }

    private static HashSet<long> WorkflowDisclosed() => [MakerId, CheckerId, TargetId, AliasId, OtherId];

    private static async Task WorkflowProfilesAsync(Data data)
    {
        foreach (var id in new long[] { MakerId, CheckerId, TargetId, AliasId, OtherId })
            await data.Rows("accountProfiles").InsertOneAsync(new MongoDB.Bson.BsonDocument
            {
                { "_id", id }, { "schemaVersion", 1 }, { "version", 1L }, { "displayName", "Petugas " + id },
                { "verifiedEmail", "person-" + id + "@example.invalid" },
                { "verifiedAt", data.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Clock.UtcNow.UtcDateTime }
            });
    }

    private sealed class WorkflowSelection(MongoDB.Driver.IMongoDatabase database, long groupId, HashSet<long> disclosed,
        bool groups = true, bool proposals = true, DateTimeOffset? until = null)
        : UnitToko.Platform.Identity.IPersonReviewSelectionTrust
    {
        public async Task<UnitToko.Platform.Identity.PersonReviewSelectionPermit> RequireAsync(
            MongoDB.Driver.IClientSessionHandle tx, UnitToko.Modules.Identity.Contracts.ActorContext actor,
            UnitToko.Platform.Identity.PersonReviewSelectionRequest request, CancellationToken ct)
        {
            var policy = await database.GetCollection<MongoDB.Bson.BsonDocument>("personGovernancePolicies")
                .Find(tx, Id(PolicyId)).SingleAsync(ct);
            var group = await database.GetCollection<MongoDB.Bson.BsonDocument>("personEquivalenceGroups")
                .Find(tx, Id(groupId)).SingleAsync(ct);
            var proposalRows = await database.GetCollection<MongoDB.Bson.BsonDocument>("personReviewProposals")
                .Find(tx, new MongoDB.Bson.BsonDocument("target.groupId", groupId)).Sort(new MongoDB.Bson.BsonDocument("_id", 1)).ToListAsync(ct);
            return new(database, tx, actor, request, policy, proposals ? [.. proposalRows.Select(row => row["_id"].AsInt64)] : [],
                groups ? [groupId] : [], [.. disclosed.Order()], [new("personEquivalenceGroups", groupId, group["version"].AsInt64,
                    UnitToko.Platform.Identity.PersonWithdrawalTarget.Digest(group, true), group)], decisionValidUntil: until);
        }
    }
}
