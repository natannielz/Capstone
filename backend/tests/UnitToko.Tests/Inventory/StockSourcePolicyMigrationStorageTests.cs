using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourcePolicyMigrationTests
{
    [Theory]
    [InlineData("view")] [InlineData("capped")] [InlineData("timeseries")] [InlineData("collation")]
    public async Task Unsafe_policy_collection_options_fail_before_upgrade_writes(string option)
    {
        await using var data = await StoreTestData.CreateForStockSourcePolicyMigrationAsync(fixture);
        var command = new BsonDocument("create", StockSourcePolicyBson.Policies);
        switch (option)
        {
            case "view": command["viewOn"] = "stockSourceProposals"; command["pipeline"] = new BsonArray(); break;
            case "capped": command["capped"] = true; command["size"] = 1048576L; break;
            case "timeseries": command["timeseries"] = new BsonDocument("timeField", "recordedAt"); break;
            case "collation": command["collation"] = new BsonDocument { { "locale", "en" }, { "strength", 2 } }; break;
        }
        await data.Database.RunCommandAsync<BsonDocument>(command);
        await Reject(data);
    }

    [Theory]
    [InlineData("mapping-audit")] [InlineData("mapping-receipt")]
    [InlineData("suspension-audit")] [InlineData("suspension-receipt")]
    [InlineData("missing-suspension-lookup")] [InlineData("hidden-suspension-lookup")]
    [InlineData("unknown-source-operation")] [InlineData("disguised-source-audit")]
    [InlineData("disguised-suspension-audit")]
    public async Task Policy_upgrade_preserves_prerequisite_provenance_and_family_ownership(string fault)
    {
        await using var data = await StoreTestData.CreateForStockSourcePolicyMigrationAsync(fixture);
        await InstallDraft(data); var actors = await Seed(data); await Link(data, actors);
        var service = Service(data); var preview = await service.PreviewSuspensionAsync(actors.Maker, actors.Pool);
        await service.SuspendAsync(actors.Maker, actors.Pool, new(preview["poolRevision"].AsInt64,
            preview["suspensionRevision"].AsInt64, preview["configurationHash"].AsString), Meta("stop"));
        if (fault is "mapping-audit" or "mapping-receipt" or "suspension-audit" or "suspension-receipt")
        {
            var name = fault.EndsWith("audit", StringComparison.Ordinal) ? "auditEvents" : "idempotencyRecords";
            var operation = fault.StartsWith("mapping", StringComparison.Ordinal) ? "stock.source.alias.approve" : "stock.source.suspend";
            Assert.Equal(1L, (await Rows(data, name).DeleteOneAsync(new BsonDocument("operation", operation))).DeletedCount);
        }
        else if (fault is "missing-suspension-lookup" or "hidden-suspension-lookup")
        {
            var lookup = StockSourceSuspensionSchemas.LookupIndexes()[0];
            await Rows(data, lookup.Collection).Indexes.DropOneAsync(lookup.Name);
            if (fault == "hidden-suspension-lookup")
                await Rows(data, lookup.Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(lookup.Key,
                    new CreateIndexOptions { Name = lookup.Name, Hidden = true }));
        }
        else
        {
            var operation = fault == "disguised-suspension-audit" ? "stock.source.suspend" : "stock.source.approve";
            var audit = await Rows(data, "auditEvents").Find(new BsonDocument("operation", operation)).SingleAsync();
            audit["_id"] = 99000L;
            if (fault == "unknown-source-operation") audit["operation"] = "stock.source.unknown";
            else
            {
                // These fields trigger the forward family partition but cannot turn an older record into a policy.
                audit["record"]["policyRevision"] = 1L;
                audit["kind"] = "stock.source.policy.audit";
            }
            await Rows(data, "auditEvents").InsertOneAsync(audit);
        }
        await Reject(data);
    }

    [Theory]
    [InlineData("hash")] [InlineData("result")] [InlineData("extra")] [InlineData("future-proposal")]
    public async Task Exact_policy_receipt_and_proposal_time_are_required(string fault)
    {
        await using var data = await StoreTestData.CreateForStockSourcePolicyMigrationAsync(fixture);
        await InstallDraft(data); await Seed(data);
        if (fault == "future-proposal")
        {
            var proposal = await Rows(data, StockSourcePolicyBson.Proposals).Find(new BsonDocument()).SingleAsync();
            proposal["proposedAt"] = data.Clock.UtcNow.AddSeconds(1).UtcDateTime;
            await ReplaceProposal(data, proposal);
        }
        else
        {
            var update = fault switch
            {
                "hash" => Builders<BsonDocument>.Update.Set("bodyHash", new string('f', 64)),
                "result" => Builders<BsonDocument>.Update.Set("result.policyId", 88888L),
                _ => Builders<BsonDocument>.Update.Set("extra", "unreviewed")
            };
            await Rows(data, "idempotencyRecords").UpdateOneAsync(new BsonDocument("operation", "stock.source.policy.approve"), update);
        }
        await Reject(data);
    }
}
