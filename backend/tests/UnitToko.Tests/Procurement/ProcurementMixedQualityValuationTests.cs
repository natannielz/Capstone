using System.Runtime.Versioning;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using D = UnitToko.Tests.Procurement.ProcurementStockValuationTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ProcurementMixedQualityValuationTests(MongoFixture fixture)
{
    [Fact]
    public void Clean_match_input_keeps_original_canonical_json_and_bson_without_a_quality_key() {
        var clean = new SupplierDocumentMatchLineInput(1, 1, 2, 1, null, null,
            new Money(10000), new Money(1000), new Money(250), null, null);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.DoesNotContain("qualityAllocationId", JsonSerializer.Serialize(clean, json), StringComparison.Ordinal);
        Assert.False(SupplierDocumentBson.MatchLine(clean).Contains("qualityAllocationId"));
        var linked = clean with { QualityAllocationId = 9 };
        Assert.Contains("\"qualityAllocationId\":9", JsonSerializer.Serialize(linked, json), StringComparison.Ordinal);
        Assert.Equal(9L, SupplierDocumentBson.MatchLine(linked)["qualityAllocationId"].AsInt64);
    }

    [WindowsStorageFact]
    public async Task Genuine_mixed_quality_match_and_invoice_linked_claim_preserve_exact_partial_valuation_and_held_stock() {
        await using var data = await D.CreateAsync(fixture, mixedQuality: true, receiptPackages: 2);
        Assert.NotNull(data.Quality);
        Assert.NotNull(data.MixedMatchInput);
        var match = await data.Rows(SupplierDocumentBson.Matches).Find(new BsonDocument("_id", data.MatchId)).SingleAsync();
        Assert.Equal(2, match["schemaVersion"].AsInt32);
        Assert.Equal(data.Quality!["allocationId"], match["lines"][0]["qualityAllocationId"]);
        Assert.Equal(match["lines"][0]["quality"], match["links"][0]["quality"]);

        var claims = data.MixedQuality!.Stock.Claims;
        var original = claims.Input("SYNTHETIC-MIXED-INVOICE-LINKED-CLAIM");
        var claim = original with { Lines = [new SupplierClaimLineInput(SupplierClaimBasis.PhysicalReceived,
            claims.InspectionId, 1, 1, "damaged", data.Documents.DocumentId, 1, data.MatchId, 1)] };
        var requested = await claims.RequestAsync("mixed-invoice-linked-claim", claim);
        await claims.DecideAsync(requested["claimId"].AsInt64, "mixed-invoice-linked-claim-approved");

        var proposal = await data.ProposeAsync("mixed-quality-valued-one");
        var stored = await data.Rows(D.Proposals).Find(new BsonDocument("_id", proposal["proposalId"])).SingleAsync();
        Assert.Equal(2, stored["schemaVersion"].AsInt32);
        Assert.Equal(data.Quality["allocationId"], stored["quality"]["allocationId"]);
        Assert.Equal(2L, stored["qualityCapacity"]["acceptedPackages"].AsInt64);
        var approved = await data.DecideAsync(proposal["proposalId"].AsInt64, "mixed-quality-valued-one-approved");
        Assert.Equal("approved", approved["status"].AsString);
        await ProcurementStockValuationTests.JournalAsync(data, approved["journalId"].AsInt64, 11250m);
        var balance = await data.Rows("stockBalances").Find(new BsonDocument("_id", data.Receipt["balanceId"])).SingleAsync();
        Assert.Equal(2L, balance["held"].AsInt64);
        Assert.Equal(2L, balance["onHand"].AsInt64);
        var journals = await data.Rows("journalEntries").Find(new BsonDocument()).ToListAsync();
        Assert.Single(journals);
    }

    [WindowsStorageFact]
    public async Task Quality_allocation_cannot_be_matched_beyond_accepted_packages_or_reused_after_proof_loss() {
        await using var data = await D.CreateAsync(fixture, mixedQuality: true, matchInvoice: false);
        var baseInput = Assert.IsType<SupplierDocumentMatchInput>(data.MixedMatchInput);
        var line = baseInput.Lines[0] with { Packages = 1, NetAmount = new Money(10000), TaxAmount = new Money(1000), ChargeAmount = new Money(200) };
        var input = baseInput with { Lines = [line] };
        await data.Documents.MatchAsync("mixed-quality-first", input);
        var contenders = await Task.WhenAll(new[] { "mixed-quality-second-a", "mixed-quality-second-b" }.Select(async key => {
            try { await data.Documents.MatchAsync(key, input); return true; }
            catch (Exception error) when (error is UnitToko.Platform.Mongo.PersistenceConflictException or MongoException) { return false; }
        }));
        Assert.Single(contenders, succeeded => succeeded);
        Assert.Equal(2L, await data.Rows(SupplierDocumentBson.Matches).CountDocumentsAsync(new BsonDocument()));
        await Assert.ThrowsAnyAsync<Exception>(() => data.Documents.MatchAsync("mixed-quality-over-capacity", input));
        var allocationId = data.Quality!["allocationId"].AsInt64;
        var collection = data.Rows(ProcurementQualityAcceptanceBson.Allocations);
        var original = await collection.Find(new BsonDocument("_id", allocationId)).SingleAsync();
        await collection.DeleteOneAsync(new BsonDocument("_id", allocationId));
        try {
            await Assert.ThrowsAnyAsync<Exception>(() => data.Documents.Service.MatchesAsync(data.Documents.Matcher, data.Documents.DocumentId));
        } finally { await collection.InsertOneAsync(original); }
    }
}
