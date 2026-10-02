using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using B = UnitToko.Platform.Billing.BankReceiptBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class BankTransactionClaimPendingReceiptTests(MongoFixture fixture)
{
    [Fact]
    public async Task Retained_committed_empty_credit_receipts_are_never_exempted_by_actor_key_or_body_context() {
        await using var data = await BankReceiptTestData.CreateAsync(fixture);
        await data.ConfirmAsync(await data.PreviewAsync(), "empty-receipt-original-anchor");
        var later = (await data.Statements.StageAsync("empty-receipt-later", BankStatementTestData.Original("0002")))["importId"].AsInt64;
        var saved = await data.ConfirmAsync(await data.PreviewAsync(later), "empty-receipt-real-later", BankReceiptTestData.Observation("0002"));
        var next = (await data.Statements.StageAsync("empty-receipt-next", BankStatementTestData.Original("0003")))["importId"].AsInt64;
        var preview = await data.PreviewAsync(next);
        var receipt = await data.Rows(B.Receipts).Find(new BsonDocument("_id", saved["receiptId"])).SingleAsync();
        var confirmation = await data.Rows(B.Confirmations).Find(new BsonDocument("_id", saved["confirmationId"])).SingleAsync();
        var operation = await data.Rows("idempotencyRecords").Find(new BsonDocument { { "actorId", data.Reviewer.UserId }, { "operation", B.Operation }, { "key", "empty-receipt-real-later" } }).SingleAsync();
        // Preserve the actual independently reviewed receipt, but deliberately remove
        // its later real owner/effects and leave a coherently shaped empty result.
        await data.Rows(BankTransactionClaims.Collection).DeleteOneAsync(new BsonDocument("_id", receipt["canonicalIdentity"]));
        await data.Rows(B.Receipts).DeleteOneAsync(new BsonDocument("_id", receipt["_id"]));
        await data.Rows(B.Confirmations).DeleteOneAsync(new BsonDocument("_id", confirmation["_id"]));
        await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("_id", confirmation["auditId"]));
        await data.Rows("outboxEvents").DeleteOneAsync(new BsonDocument("sourceKey", B.SourceKey(confirmation)));
        await data.Rows("journalEntries").DeleteOneAsync(new BsonDocument("_id", receipt["journalId"]));
        var journalKey = "accounting:journal:" + receipt["journalId"].AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await data.Rows("accountingAuditEvents").DeleteOneAsync(new BsonDocument("eventKey", journalKey));
        await data.Rows("outboxEvents").DeleteOneAsync(new BsonDocument("sourceKey", journalKey));
        foreach (var variant in new[] { "original", "actor", "key", "body" }) {
            var broken = operation.DeepClone().AsBsonDocument; broken["result"] = new BsonDocument();
            if (variant == "actor") broken["actorId"] = data.Other.UserId;
            if (variant == "key") broken["key"] = "unrelated-committed-empty-key";
            if (variant == "body") broken["bodyHash"] = new string('c', 64);
            await data.Rows("idempotencyRecords").ReplaceOneAsync(new BsonDocument("_id", operation["_id"]), broken);
            var before = await data.StateAsync(); var claims = new BsonArray(await data.Rows(BankTransactionClaims.Collection).Find(new BsonDocument()).ToListAsync());
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.PreviewAsync(next));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.ConfirmAsync(preview, "empty-receipt-new-command-" + variant, BankReceiptTestData.Observation("0003")));
            Assert.Equal(before, await data.StateAsync());
            Assert.Equal(claims, new BsonArray(await data.Rows(BankTransactionClaims.Collection).Find(new BsonDocument()).ToListAsync()));
            Assert.Equal(broken, await data.Rows("idempotencyRecords").Find(new BsonDocument("_id", operation["_id"])).SingleAsync());
        }
    }
}
