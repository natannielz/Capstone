using MongoDB.Bson;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Documents;

public sealed class EvidenceAdmissionSchemaTests
{
    internal static BsonDocument Policy(long maximum = 1024, long count = 10, long bytes = 30720) => new()
    {
        { "_id", 101L }, { "schemaVersion", 1 }, { "revision", 1L },
        { "createdAt", DateTime.UnixEpoch.AddDays(1) }, { "updatedAt", DateTime.UnixEpoch.AddDays(1) },
        { "storeId", new string('a', 64) }, { "active", true }, { "policyVersion", 1L },
        { "maximumContentBytes", maximum }, { "maximumHeldReservations", count }, { "maximumHeldBytes", bytes },
        { "reservationDurationMs", 600000L }, { "writerDurationMs", 120000L }, { "ioDurationMs", 30000L },
        { "bufferBytes", 1024L }, { "chargedReservations", 0L }, { "chargedBytes", 0L }, { "fence", 0L }
    };

    [Fact]
    public void Policy_requires_explicit_finite_limits_and_retained_charge_bounds() =>
        Assert.True(EvidenceSchemas.IsValid("evidenceStorePolicies", Policy()));

    [Theory]
    [InlineData("maximumContentBytes", 0L)]
    [InlineData("maximumContentBytes", 9223372036854775807L)]
    [InlineData("maximumHeldReservations", 0L)]
    [InlineData("maximumHeldBytes", 0L)]
    [InlineData("reservationDurationMs", 0L)]
    [InlineData("writerDurationMs", 600001L)]
    [InlineData("ioDurationMs", 4294967295L)]
    [InlineData("bufferBytes", 1048577L)]
    [InlineData("chargedReservations", 11L)]
    [InlineData("chargedBytes", 30721L)]
    public void Policy_rejects_unsafe_numbers(string field, long value)
    {
        var row = Policy(); row[field] = value;
        Assert.False(EvidenceSchemas.IsValid("evidenceStorePolicies", row));
    }

    [Fact]
    public void Policy_rejects_int32_ids_extra_keys_duplicate_keys_and_out_of_range_dates()
    {
        foreach (var change in new Action<BsonDocument>[] {
            row => row["_id"] = 101, row => row["revision"] = 1, row => row["schemaVersion"] = 1L,
            row => row["path"] = "secret", row => row["storeId"] = new string('A', 64),
            row => row["createdAt"] = new BsonDateTime(long.MaxValue),
            row => { row.AllowDuplicateNames = true; row.Add("active", true); } })
        {
            var row = Policy(); change(row); Assert.False(EvidenceSchemas.IsValid("evidenceStorePolicies", row));
        }
    }

    [Fact]
    public void Legacy_version_record_is_not_silently_assigned_a_lineage() =>
        Assert.False(EvidenceSchemas.IsValid("documentVersions", new() { { "_id", 1L }, { "schemaVersion", 1 }, { "documentId", 2L }, { "version", 1L } }));

    [Theory]
    [InlineData("auditEvents")]
    [InlineData("idempotencyRecords")]
    [InlineData("outboxEvents")]
    public void Evidence_conditional_rule_leaves_unrelated_module_rows_to_existing_validators(string collection) =>
        Assert.True(EvidenceSchemas.IsValid(collection, new() { { "_id", 1L }, { "schemaVersion", 1 }, { "kind", "other.module" } }));

    [Fact]
    public void Evidence_markers_cannot_disguise_an_unknown_operation()
    {
        var row = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
            { "operation", "other.module" }, { "evidenceAdmission", new BsonDocument() } };
        Assert.False(EvidenceSchemas.IsValid("idempotencyRecords", row));
    }
}
