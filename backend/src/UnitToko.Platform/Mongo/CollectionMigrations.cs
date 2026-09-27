using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo;

// Run explicitly during deployment, never from an API request or application startup.
public sealed class CollectionMigrations(IMongoDatabase database, IClock clock)
{
    public const int CurrentVersion = 1;
    private readonly IMongoDatabase durableDatabase = database.WithWriteConcern(WriteConcern.WMajority);
    private static readonly string[] BusinessCollections =
    [
        "identities", "memberships", "organizations", "branches", "divisions", "shops", "warehouses",
        "productFamilies", "skus", "priceVersions", "lots", "stockBalances", "movements",
        "reservations", "orders", "shipments", "receipts", "cases", "purchaseOrders",
        "goodsReceipts", "supplierInvoices", "supplierClaims", "invoices", "payments",
        "allocations", "credits", "refunds", "journalEntries", "periods", "documentVersions",
        "auditEvents", "importBatches"
    ];

    public Task<bool> IsCurrentAsync(CancellationToken cancellationToken = default) =>
        new MigrationGate(database, clock).IsCurrentAsync(cancellationToken);

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        var gate = new MigrationGate(database, clock);
        var lease = await gate.AcquireAsync(cancellationToken);
        if (lease is null) return;
        try
        {
            await ApplyVersionOneAsync(lease, cancellationToken);
            await lease.CompleteAsync(cancellationToken);
        }
        catch
        {
            try { await lease.ReleaseAsync(); }
            catch (MongoException) { /* Preserve the migration failure; lease expiry enables recovery. */ }
            throw;
        }
    }

    private async Task ApplyVersionOneAsync(MigrationLease lease, CancellationToken cancellationToken)
    {
        foreach (var name in BusinessCollections)
        {
            await lease.RenewAsync(cancellationToken);
            await EnsureCollectionAsync(name, BusinessValidator(), cancellationToken);
        }
        await EnsureCollectionAsync("idCounters", JsonSchema(Schema(["value"], new BsonDocument
        {
            { "value", new BsonDocument("bsonType", "long") }
        })), cancellationToken);
        await EnsureCollectionAsync("idempotencyRecords", JsonSchema(Schema(
            ["schemaVersion", "actorId", "operation", "key", "bodyHash", "result", "createdAt"],
            new BsonDocument
            {
                { "schemaVersion", new BsonDocument("enum", new BsonArray { CurrentVersion }) },
                { "actorId", new BsonDocument { { "bsonType", "long" }, { "minimum", 1L } } }, { "operation", String(128) }, { "key", String(200) },
                { "bodyHash", String(64) }, { "result", new BsonDocument("bsonType", "object") },
                { "createdAt", new BsonDocument("bsonType", "date") }
            })), cancellationToken);
        await EnsureCollectionAsync("outboxEvents", JsonSchema(Schema(
            ["schemaVersion", "sourceKey", "kind", "payload", "status", "attempts", "dueAt", "createdAt"],
            new BsonDocument
            {
                { "schemaVersion", new BsonDocument("enum", new BsonArray { CurrentVersion }) },
                { "sourceKey", String(200) }, { "kind", String(100) },
                { "payload", new BsonDocument("bsonType", "object") },
                { "status", new BsonDocument("enum", new BsonArray { "pending", "leased", "retry", "completed", "dead" }) },
                { "attempts", new BsonDocument("bsonType", "int") },
                { "dueAt", new BsonDocument("bsonType", "date") },
                { "createdAt", new BsonDocument("bsonType", "date") }
            })), cancellationToken);

        await lease.RenewAsync(cancellationToken);
        await lease.RenewAsync(cancellationToken);
        await Unique("identities", "issuer", "subject", cancellationToken);
        await lease.RenewAsync(cancellationToken);
        await Unique("organizations", "normalizedBusinessId", cancellationToken);
        await lease.RenewAsync(cancellationToken);
        await Unique("skus", "organizationId", "sku", cancellationToken);
        await lease.RenewAsync(cancellationToken);
        await Unique("documentVersions", "documentId", "version", cancellationToken);
        await lease.RenewAsync(cancellationToken);
        await Unique("journalEntries", "sourceEvent", cancellationToken);
        await lease.RenewAsync(cancellationToken);
        await Unique("idempotencyRecords", "actorId", "operation", "key", cancellationToken);
        await lease.RenewAsync(cancellationToken);
        await Unique("outboxEvents", "sourceKey", cancellationToken);
        await lease.RenewAsync(cancellationToken);
        await Unique("importBatches", "sourceSystem", "externalTransactionId", "lineId", cancellationToken);
        await lease.RenewAsync(cancellationToken);
        await Unique("invoices", "sourceReceiptLineId", "sourceAllocationId", cancellationToken);
        await lease.RenewAsync(cancellationToken);
        await Unique("invoices", "numberEntity", "numberChannel", "numberYear", "number", cancellationToken);
        await lease.RenewAsync(cancellationToken);
        await Unique("shipments", "numberEntity", "numberChannel", "numberYear", "number", cancellationToken);
        await lease.RenewAsync(cancellationToken);
        await Index("orders", "branchId", "orderedAt", cancellationToken);
        await lease.RenewAsync(cancellationToken);
        await Index("orders", "organizationId", "status", "orderedAt", cancellationToken);
        await lease.RenewAsync(cancellationToken);
        await Index("lots", "skuId", "warehouseId", "expiresAt", cancellationToken);
        await lease.RenewAsync(cancellationToken);
        await Index("invoices", "organizationId", "status", "dueAt", cancellationToken);
        await lease.RenewAsync(cancellationToken);
        await Index("shipments", "courierId", "deliveryDate", cancellationToken);
        await lease.RenewAsync(cancellationToken);
        await Index("importBatches", "sourceSystem", "status", cancellationToken);
        await lease.RenewAsync(cancellationToken);
        await Index("outboxEvents", "status", "dueAt", cancellationToken);
    }

    private async Task EnsureCollectionAsync(string name, BsonDocument schema, CancellationToken token)
    {
        var exists = await (await durableDatabase.ListCollectionNamesAsync(
            new ListCollectionNamesOptions { Filter = new BsonDocument("name", name) }, token)).AnyAsync(token);
        if (exists)
            await durableDatabase.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "collMod", name }, { "validator", schema },
                { "validationLevel", "strict" }, { "validationAction", "error" }
            }, cancellationToken: token);
        else
            await durableDatabase.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "create", name }, { "validator", schema },
                { "validationLevel", "strict" }, { "validationAction", "error" }
            }, cancellationToken: token);
    }

    private static BsonDocument JsonSchema(BsonDocument schema) => new("$jsonSchema", schema);

    private static BsonDocument BusinessValidator()
    {
        var precisionChecks = new BsonArray();
        foreach (var name in new[] { "unitPrice", "lineTotal", "tax", "documentTotal", "journalAmount", "rate" })
        {
            var pattern = name == "rate"
                ? @"^(?:0|[1-9][0-9]*)(?:\.[0-9]{1,4})?$"
                : name == "journalAmount"
                    ? @"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]{1,2})?$"
                    : @"^(?:0|[1-9][0-9]*)(?:\.[0-9]{1,2})?$";
            precisionChecks.Add(new BsonDocument("$or", new BsonArray
            {
                new BsonDocument("$eq", new BsonArray
                { new BsonDocument("$type", "$" + name), "missing" }),
                new BsonDocument("$regexMatch", new BsonDocument
                {
                    { "input", new BsonDocument("$convert", new BsonDocument
                    { { "input", "$" + name }, { "to", "string" }, { "onError", "" }, { "onNull", "" } }) },
                    { "regex", pattern }
                })
            }));
        }
        return new BsonDocument("$and", new BsonArray
        {
            JsonSchema(Schema(["schemaVersion"], BusinessProperties())),
            new BsonDocument("$expr", new BsonDocument("$and", precisionChecks))
        });
    }
    private static BsonDocument BusinessProperties()
    {
        var maximumMoney = new BsonDecimal128(999_999_999_999_999.99m);
        var minimumSignedMoney = new BsonDecimal128(-999_999_999_999_999.99m);
        BsonDocument Money(bool signed) => new()
        {
            { "bsonType", "decimal" }, { "minimum", signed ? minimumSignedMoney : new BsonDecimal128(0m) },
            { "maximum", maximumMoney }
        };
        return new BsonDocument
        {
            { "schemaVersion", new BsonDocument("enum", new BsonArray { CurrentVersion }) },
            { "id", new BsonDocument { { "bsonType", "long" }, { "minimum", 1L } } },
            { "version", new BsonDocument { { "bsonType", "long" }, { "minimum", 0L } } },
            { "quantity", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 1_000_000 } } },
            { "unitPrice", Money(false) }, { "lineTotal", Money(false) },
            { "tax", Money(false) }, { "documentTotal", Money(false) },
            { "journalAmount", Money(true) },
            { "rate", new BsonDocument { { "bsonType", "decimal" }, { "minimum", new BsonDecimal128(0m) }, { "maximum", new BsonDecimal128(100m) } } }
        };
    }
    private static BsonDocument Schema(string[] required, BsonDocument properties) =>
        new() { { "bsonType", "object" }, { "required", new BsonArray(required) }, { "properties", properties } };
    private static BsonDocument String(int max) => new() { { "bsonType", "string" }, { "maxLength", max }, { "minLength", 1 } };

    private Task Unique(string collection, string field, CancellationToken token) => Unique(collection, [field], token);
    private Task Unique(string collection, string first, string second, CancellationToken token) => Unique(collection, [first, second], token);
    private Task Unique(string collection, string first, string second, string third, CancellationToken token) => Unique(collection, [first, second, third], token);
    private Task Unique(string collection, string first, string second, string third, string fourth, CancellationToken token) => Unique(collection, [first, second, third, fourth], token);
    private Task Unique(string collection, string[] fields, CancellationToken token) =>
        CreateIndex(collection, fields, true, token);
    private Task Index(string collection, string first, string second, CancellationToken token) => CreateIndex(collection, [first, second], false, token);
    private Task Index(string collection, string first, string second, string third, CancellationToken token) => CreateIndex(collection, [first, second, third], false, token);
    private Task CreateIndex(string collection, string[] fields, bool unique, CancellationToken token)
    {
        var keys = new BsonDocument(fields.Select(field => new BsonElement(field, 1)));
        var options = new CreateIndexOptions<BsonDocument>
        {
            Name = $"{(unique ? "uq" : "ix")}_{string.Join('_', fields)}",
            Unique = unique
        };
        // Future modules make these keys mandatory in their own schema migration.
        // Until then, absent keys must not make otherwise valid documents collide.
        if (unique)
            options.PartialFilterExpression = new BsonDocument("$and", new BsonArray(
                fields.Select(field => new BsonDocument(field, new BsonDocument("$exists", true)))));
        return durableDatabase.GetCollection<BsonDocument>(collection).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(keys, options), cancellationToken: token);
    }
}
