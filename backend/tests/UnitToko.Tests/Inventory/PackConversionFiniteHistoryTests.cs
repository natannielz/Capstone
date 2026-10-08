using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Inventory.PackConversionBson;
using Prepared = UnitToko.Tests.Inventory.PackConversionServiceTests.PreparedConversion;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PackConversionFiniteHistoryTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Same_finite_handle_reauthenticates_real_conversion_genesis_and_current_destination_without_business_writes() {
        await using var prepared = await Prepared.CreateAsync(fixture);
        var data = prepared.Data; var result = prepared.Result!; var service = prepared.Service();
        var row = await data.Rows(B.Conversions).Find(new BsonDocument("_id", result["conversionId"])).SingleAsync();
        var source = B.Source(row["_id"].AsInt64, row["before"]["physicalPoolId"].AsInt64);
        var before = await prepared.StateAsync(); var accounting = await data.Valuation.StateAsync(); var documents = await data.Valuation.Documents.StateAsync();
        await data.Unit.ExecuteAsync(async (tx, ct) => {
            var handle = service.RetainFiniteHistory(tx);
            for (var read = 0; read < 2; read++) {
                handle.Require(service, data.Database, tx);
                await handle.AuthenticateAsync(data.Database, tx, source, row["causeHash"].AsString, ct);
                var current = await service.ReadCurrentDestinationAsync(tx, result["destinationBalanceId"].AsInt64, ct, retained: handle);
                Assert.Equal(row["destinationAfter"], InventoryBson.Balance(current));
                Assert.Equal((12L, 0L, 0L, 0L, 2L), (current.Balance.OnHand, current.Balance.Reserved, current.Balance.Held, current.Balance.Expired, current.Balance.Revision));
            }
            return true;
        }, default);
        Assert.Equal(before, await prepared.StateAsync()); Assert.Equal(accounting, await data.Valuation.StateAsync()); Assert.Equal(documents, await data.Valuation.Documents.StateAsync());
    }

    [WindowsStorageFact]
    public async Task Actual_native_finite_handle_refuses_foreign_owner_database_client_session_next_attempt_and_clock_regression() {
        await using var prepared = await Prepared.CreateAsync(fixture);
        var data = prepared.Data; var service = prepared.Service(); var before = await prepared.StateAsync();
        var accounting = await data.Valuation.StateAsync(); var documents = await data.Valuation.Documents.StateAsync();
        var row = await data.Rows(B.Conversions).Find(new BsonDocument("_id", prepared.Result!["conversionId"])).SingleAsync();
        var source = B.Source(row["_id"].AsInt64, row["before"]["physicalPoolId"].AsInt64); var refusals = 0;
        void Broken(Action action) { Assert.Equal(B.Broken().Code, Assert.Throws<PersistenceConflictException>(action).Code); refusals++; }
        using var tx = await data.Database.Client.StartSessionAsync(); tx.StartTransaction();
        var handle = service.RetainFiniteHistory(tx);
        try {
            await handle.AuthenticateAsync(data.Database, tx, source, row["causeHash"].AsString, default);
            handle.Require(service, data.Database, tx);
            Broken(() => handle.Require(prepared.Service(), data.Database, tx));
            Broken(() => handle.Require(service, data.Database.Client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName + "-foreign"), tx));
            var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "conversion-finite-history-foreign-" + Guid.NewGuid().ToString("N");
            using var foreignClient = new MongoClient(settings);
            Broken(() => handle.Require(service, foreignClient.GetDatabase(data.Database.DatabaseNamespace.DatabaseName), tx));
            using var otherSession = await data.Database.Client.StartSessionAsync(); otherSession.StartTransaction();
            try { Broken(() => handle.Require(service, data.Database, otherSession)); }
            finally { await otherSession.AbortTransactionAsync(); }
            using var foreignSession = await foreignClient.StartSessionAsync(); foreignSession.StartTransaction();
            try {
                Assert.Equal("inventory_transaction_required", Assert.Throws<DomainValidationException>(() => handle.Require(service, data.Database, foreignSession)).Code);
                refusals++;
            } finally { await foreignSession.AbortTransactionAsync(); }
            var now = data.Clock.UtcNow;
            try { data.Clock.UtcNow = now.AddMilliseconds(-1); Broken(() => handle.Require(service, data.Database, tx)); }
            finally { data.Clock.UtcNow = now; }
            handle.Require(service, data.Database, tx);
            await tx.AbortTransactionAsync(); tx.StartTransaction();
            Broken(() => handle.Require(service, data.Database, tx));
        } finally { if (tx.IsInTransaction) await tx.AbortTransactionAsync(); }
        Assert.Equal(7, refusals); Assert.Equal(before, await prepared.StateAsync());
        Assert.Equal(accounting, await data.Valuation.StateAsync()); Assert.Equal(documents, await data.Valuation.Documents.StateAsync());
    }

    [WindowsStorageFact]
    public async Task Warm_same_handle_rechecks_destination_identity_membership_schema_revision_and_real_rule_source_and_private_pin_owners() {
        await using var prepared = await Prepared.CreateAsync(fixture);
        var data = prepared.Data; var service = prepared.Service(); var result = prepared.Result!;
        var row = await data.Rows(B.Conversions).Find(new BsonDocument("_id", result["conversionId"])).SingleAsync();
        var source = B.Source(row["_id"].AsInt64, row["before"]["physicalPoolId"].AsInt64); var destinationId = result["destinationBalanceId"].AsInt64;
        var destination = await data.Rows("stockBalances").Find(new BsonDocument("_id", destinationId)).SingleAsync();
        var rule = await data.Rows(PackConversionRuleBson.Rules).Find(new BsonDocument("_id", row["input"]["ruleId"])).SingleAsync();
        var sourceLot = await data.Rows("lots").Find(new BsonDocument("_id", row["sourceLot"]["_id"])).SingleAsync();
        var inspection = await data.Rows("procurementInspectionSubmissions").Find(new BsonDocument("_id", data.Valuation.Documents.InspectionId)).SingleAsync();
        var pin = await data.Rows("evidencePins").Find(new BsonDocument("_id", inspection["pinId"])).SingleAsync();
        var before = await prepared.StateAsync(); var accounting = await data.Valuation.StateAsync(); var documents = await data.Valuation.Documents.StateAsync();
        var warmed = 0; var refusals = 0; var deletedPin = 0;
        async Task OwnerRefusal(Func<Task> attempt) {
            var failure = await Record.ExceptionAsync(attempt);
            Assert.True(failure is PersistenceConflictException or DomainValidationException or AuthorizationDeniedException, failure?.ToString());
            refusals++;
        }
        async Task Attempt() => await data.Unit.ExecuteAsync(async (tx, ct) => {
            var handle = service.RetainFiniteHistory(tx);
            Task Authenticate() => handle.AuthenticateAsync(data.Database, tx, source, row["causeHash"].AsString, ct);
            await Authenticate();
            Assert.Equal(destination, InventoryBson.Balance(await service.ReadCurrentDestinationAsync(tx, destinationId, ct, retained: handle))); warmed++;
            foreach (var fault in new[] { "destination-key", "lower-revision", "malformed-quantity" }) {
                // These unowned negative writes are restored in this same native
                // transaction; they never create positive source or stock authority.
                var changed = destination.DeepClone().AsBsonDocument;
                if (fault == "destination-key") changed["skuId"] = destination["skuId"].AsInt64 + 100000;
                else if (fault == "lower-revision") changed["version"] = 1L;
                else changed["reserved"] = destination["onHand"].AsInt64 + 1;
                Assert.Equal(1L, (await data.Rows("stockBalances").ReplaceOneAsync(tx, new BsonDocument("_id", destinationId), changed,
                    new ReplaceOptions { BypassDocumentValidation = true }, ct)).ModifiedCount);
                Assert.Equal(B.Broken().Code, (await Assert.ThrowsAsync<PersistenceConflictException>(Authenticate)).Code); refusals++;
                Assert.True(tx.IsInTransaction);
                Assert.Equal(1L, (await data.Rows("stockBalances").ReplaceOneAsync(tx, new BsonDocument("_id", destinationId), destination, cancellationToken: ct)).ModifiedCount);
            }
            var duplicate = destination.DeepClone().AsBsonDocument;
            duplicate["_id"] = destinationId + 100000; duplicate["skuId"] = destination["skuId"].AsInt64 + 100000;
            // A different SKU evades the unique stock key while the genuine lot
            // remains in the complete reverse membership query.
            await data.Rows("stockBalances").InsertOneAsync(tx, duplicate, new InsertOneOptions { BypassDocumentValidation = true }, ct);
            Assert.Equal(B.Broken().Code, (await Assert.ThrowsAsync<PersistenceConflictException>(Authenticate)).Code); refusals++;
            Assert.True(tx.IsInTransaction);
            Assert.Equal(1L, (await data.Rows("stockBalances").DeleteOneAsync(tx, new BsonDocument("_id", duplicate["_id"]), cancellationToken: ct)).DeletedCount);
            var unownedQuantity = destination.DeepClone().AsBsonDocument;
            unownedQuantity["onHand"] = destination["onHand"].AsInt64 + 1; unownedQuantity["version"] = destination["version"].AsInt64 + 1;
            Assert.Equal(1L, (await data.Rows("stockBalances").ReplaceOneAsync(tx, new BsonDocument("_id", destinationId), unownedQuantity, cancellationToken: ct)).ModifiedCount);
            // Retained immutable genesis can authenticate, but it cannot grant
            // current quantities without the actual complete owning movement chain.
            await Authenticate();
            Assert.Equal(PackConversionService.CurrentDestinationUnavailable().Code, (await Assert.ThrowsAsync<DomainValidationException>(() =>
                service.ReadCurrentDestinationAsync(tx, destinationId, ct, retained: handle))).Code); refusals++;
            Assert.True(tx.IsInTransaction);
            Assert.Equal(1L, (await data.Rows("stockBalances").ReplaceOneAsync(tx, new BsonDocument("_id", destinationId), destination, cancellationToken: ct)).ModifiedCount);
            foreach (var (table, original) in new[] { (PackConversionRuleBson.Rules, rule), ("lots", sourceLot) }) {
                Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(tx, new BsonDocument("_id", original["_id"]), cancellationToken: ct)).DeletedCount);
                await OwnerRefusal(Authenticate);
                Assert.True(tx.IsInTransaction);
                await data.Rows(table).InsertOneAsync(tx, original, cancellationToken: ct);
            }
            await Authenticate();
            Assert.Equal(destination, InventoryBson.Balance(await service.ReadCurrentDestinationAsync(tx, destinationId, ct, retained: handle)));
            Assert.Equal(1L, (await data.Rows("evidencePins").DeleteOneAsync(tx, new BsonDocument("_id", pin["_id"]), cancellationToken: ct)).DeletedCount); deletedPin++;
            // The private owner refusal leaves this last negative write for the
            // genuine unit of work to abort with every physical/value/effect write.
            await Authenticate();
            return true;
        }, default);
        await OwnerRefusal(Attempt);
        Assert.Equal(1, warmed); Assert.Equal(8, refusals); Assert.Equal(1, deletedPin);
        Assert.Equal(destination, await data.Rows("stockBalances").Find(new BsonDocument("_id", destinationId)).SingleAsync());
        Assert.Equal(rule, await data.Rows(PackConversionRuleBson.Rules).Find(new BsonDocument("_id", rule["_id"])).SingleAsync());
        Assert.Equal(sourceLot, await data.Rows("lots").Find(new BsonDocument("_id", sourceLot["_id"])).SingleAsync());
        Assert.Equal(pin, await data.Rows("evidencePins").Find(new BsonDocument("_id", pin["_id"])).SingleAsync());
        Assert.Equal(before, await prepared.StateAsync()); Assert.Equal(accounting, await data.Valuation.StateAsync()); Assert.Equal(documents, await data.Valuation.Documents.StateAsync());
    }
}
