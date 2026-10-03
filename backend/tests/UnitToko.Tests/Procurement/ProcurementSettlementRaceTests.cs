using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using D = UnitToko.Tests.Procurement.ProcurementSettlementTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ProcurementSettlementRaceTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Verification_cannot_commit_when_its_captured_grant_or_selected_policy_is_withdrawn()
    {
        await using var data = await D.CreateAsync(fixture);
        var preview = await data.PreviewAsync();
        var before = await StateAsync(data);
        var grants = await data.Rows("authorizationGrants").Find(new BsonDocument("userId", data.Verifier.UserId)).ToListAsync();
        Assert.Contains(grants, row => row["permissions"].AsBsonArray.Contains(Permissions.DisbursementVerify));
        var policy = await data.Rows(D.Policies).Find(new BsonDocument("_id", D.PolicyId)).SingleAsync();

        foreach (var change in new[] { "grant", "policy" })
        {
            using var release = new ManualResetEventSlim();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestId = 0; var observed = 0; var reads = 0;
            var settings = Settings();
            settings.ClusterConfigurator = builder => {
                builder.Subscribe<CommandStartedEvent>(command => {
                    if (command.DatabaseNamespace.DatabaseName == data.Database.DatabaseNamespace.DatabaseName &&
                        command.Command.GetValue("find", BsonNull.Value) == D.Policies && Interlocked.Increment(ref reads) == 1)
                        Volatile.Write(ref requestId, command.RequestId);
                });
                builder.Subscribe<CommandSucceededEvent>(command => {
                    if (Volatile.Read(ref requestId) != 0 && command.RequestId == Volatile.Read(ref requestId) &&
                        Interlocked.CompareExchange(ref observed, 1, 0) == 0)
                    {
                        // The genuine selected policy and authority are now in the transaction's
                        // snapshot, before their write fences. The other client changes live state.
                        entered.TrySetResult();
                        if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Settlement source race was not released");
                    }
                });
            };
            var service = data.NewService(new MongoClient(settings));
            var key = "settlement-inflight-" + change;
            var pending = Task.Run(() => service.VerifyAsync(data.Verifier, data.DisbursementId, 1, data.Input(),
                preview["binding"].AsString, true, D.Meta(key), timeout.Token));
            try
            {
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
                    if (change == "grant")
                    {
                        var result = await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", data.Verifier.UserId),
                            Builders<BsonDocument>.Update.Pull("permissions", Permissions.DisbursementVerify), cancellationToken: timeout.Token);
                        Assert.True(result.ModifiedCount > 0);
                    }
                    else
                        Assert.Equal(1, (await data.Rows(D.Policies).UpdateOneAsync(new BsonDocument("_id", D.PolicyId),
                            Builders<BsonDocument>.Update.Set("active", false), cancellationToken: timeout.Token)).ModifiedCount);
                }
                finally { release.Set(); }

                var error = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(60)));
                Assert.Equal(1, observed);
                Assert.True(error is AuthorizationDeniedException or PersistenceConflictException, error?.ToString());
                Assert.Equal(before, await StateAsync(data));
            }
            finally
            {
                release.Set();
                await timeout.CancelAsync();
                // Drain the real transaction before restoring its authoritative inputs.
                _ = await Record.ExceptionAsync(async () => await pending);
                foreach (var grant in grants)
                    await data.Rows("authorizationGrants").ReplaceOneAsync(new BsonDocument("_id", grant["_id"]), grant);
                await data.Rows(D.Policies).ReplaceOneAsync(new BsonDocument("_id", D.PolicyId), policy);
            }
            Assert.Null(await data.Service.LookupAsync(data.Verifier, D.Operation, key));
        }
    }

    [WindowsStorageFact]
    public async Task Rejected_final_bank_claim_rolls_back_the_staged_journal_and_all_effects_then_the_original_key_can_retry()
    {
        await using var data = await D.CreateAsync(fixture);
        var preview = await data.PreviewAsync(); var before = await StateAsync(data);
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions {
            Filter = new BsonDocument("name", "bankTransactionClaims") })).SingleAsync();
        var original = definition["options"]["validator"].DeepClone().AsBsonDocument;
        var stagedJournal = 0; var journalRequestId = 0; var attemptedClaim = 0;
        var settings = Settings();
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (command.DatabaseNamespace.DatabaseName != data.Database.DatabaseNamespace.DatabaseName) return;
                if (command.Command.GetValue("insert", BsonNull.Value) == "journalEntries" && command.Command["documents"].AsBsonArray
                    .Any(row => row["source"]["namespace"] == D.Operation)) Volatile.Write(ref journalRequestId, command.RequestId);
                if (command.Command.GetValue("insert", BsonNull.Value) == "bankTransactionClaims" && command.Command["documents"].AsBsonArray
                    .Any(row => row["schemaVersion"] == 2 && row["ownerKind"] == "procurementDisbursementSettlement")) Interlocked.Increment(ref attemptedClaim);
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (Volatile.Read(ref journalRequestId) != 0 && command.RequestId == Volatile.Read(ref journalRequestId) &&
                    command.Reply.GetValue("n", 0).ToInt32() == 1 && !command.Reply.Contains("writeErrors")) Interlocked.Increment(ref stagedJournal);
            });
        };
        var service = data.NewService(new MongoClient(settings));
        const string key = "settlement-final-claim-rollback";
        try
        {
            // A real database validation failure at the last financial insert. Existing v1
            // credit/debit rows and every original storage guard remain accepted and intact.
            await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
                { "collMod", "bankTransactionClaims" }, { "validator", new BsonDocument("$and", new BsonArray {
                    original, new BsonDocument("ownerKind", new BsonDocument("$ne", "procurementDisbursementSettlement")) }) },
                { "validationLevel", "strict" }, { "validationAction", "error" }
            });
            var error = await Assert.ThrowsAsync<MongoWriteException>(() => service.VerifyAsync(data.Verifier, data.DisbursementId, 1,
                data.Input(), preview["binding"].AsString, true, D.Meta(key)));
            Assert.Equal(121, error.WriteError.Code);
            Assert.Equal(1, stagedJournal); Assert.Equal(1, attemptedClaim);
            Assert.Equal(before, await StateAsync(data));
        }
        finally
        {
            await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
                { "collMod", "bankTransactionClaims" }, { "validator", original },
                { "validationLevel", "strict" }, { "validationAction", "error" }
            });
        }

        Assert.Null(await data.Service.LookupAsync(data.Verifier, D.Operation, key));
        var result = await data.VerifyAsync(preview, key);
        Assert.Equal(result, await data.Service.LookupAsync(data.Verifier, D.Operation, key));
        Assert.Equal(result, await data.VerifyAsync(preview, key));
        Assert.Equal("bank_debit_verified", result["status"].AsString);
        Assert.Single(await data.Rows(D.Events).Find(new BsonDocument()).ToListAsync());
        Assert.Single(await data.Rows("bankTransactionClaims").Find(new BsonDocument("ownerKind", "procurementDisbursementSettlement")).ToListAsync());
        Assert.Single(await data.Rows("journalEntries").Find(new BsonDocument("source.namespace", D.Operation)).ToListAsync());
        Assert.Single(await data.Rows("idempotencyRecords").Find(new BsonDocument("operation", D.Operation)).ToListAsync());
    }

    private static MongoClientSettings Settings() => MongoClientSettings.FromConnectionString(
        Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");

    private static async Task<BsonDocument> StateAsync(D data)
    {
        var state = new BsonDocument();
        // Allocator gaps are intentional; all transactional financial, period, owner and
        // generic effect rows must be byte-for-byte unchanged after a failed settlement.
        foreach (var table in new[] { D.Events, "bankTransactionClaims", "journalEntries", "accountingAuditEvents",
            "accountingPeriodHistory", "periods", "accountingBooks", "accountingChartVersions", "auditEvents", "outboxEvents", "idempotencyRecords" })
            state[table] = new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        state["disbursement"] = await data.Disbursement.StateAsync();
        return state;
    }
}
