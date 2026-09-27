using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo.Migrations;

// v4 adds provider identity state without inventing accounts, email addresses, approvals or secrets.
internal sealed class IdentityMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 4;
    private static readonly string[] Sequences =
    [
        "identities", "memberships", "authorizationGrants", "accountInvitations",
        "identityProviderIntents", "identityReadEvents", "authorizationHistory"
    ];

    public async Task ApplyAsync(MigrationLease lease, CancellationToken token)
    {
        var definitions = new List<(string Name, BsonDocument Validator, bool Exists)>();
        foreach (var (name, required) in IdentitySchemas.Collections())
        {
            await lease.RenewAsync(token);
            var previousCollection = await (await database.ListCollectionsAsync(
                new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, token)).SingleOrDefaultAsync(token);
            var previous = previousCollection?.GetValue("options", new BsonDocument()).AsBsonDocument
                .GetValue("validator", new BsonDocument()).AsBsonDocument ?? new BsonDocument();
            var validator = Preserve(previous, required);
            if (await database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority)
                .Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(token))
                throw Incompatible(name);
            definitions.Add((name, validator, previousCollection is not null));
        }

        // Every compatibility, uniqueness and counter check precedes changes to collections/indexes.
        await NoDuplicates("accountInvitations", "userId", false, lease, token);
        await NoDuplicates("accountInvitations", "secretHash", true, lease, token);
        var sequences = new Dictionary<string, long>();
        foreach (var name in Sequences)
        {
            await lease.RenewAsync(token);
            var collection = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var requiredId = new BsonDocument("$jsonSchema", new BsonDocument
            {
                { "required", new BsonArray { "_id" } },
                { "properties", new BsonDocument("_id", new BsonDocument { { "bsonType", "long" }, { "minimum", 1L } }) }
            });
            if (await collection.Find(new BsonDocument("$nor", new BsonArray { requiredId })).Limit(1).AnyAsync(token))
                throw Incompatible(name);
            var row = await collection.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", -1))
                .Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(token);
            var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority)
                .Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(token);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 < 0))
                throw Incompatible("idCounters/" + name);
            sequences[name] = row?["_id"].AsInt64 ?? 0L;
        }
        foreach (var (sequence, reservedField) in new[] { ("identities", "userId"), ("memberships", "membershipId"), ("authorizationGrants", "grantId") })
        {
            await lease.RenewAsync(token);
            var reserved = await database.GetCollection<BsonDocument>("accountInvitations").WithReadConcern(ReadConcern.Majority)
                .Find(new BsonDocument(reservedField, new BsonDocument("$type", "long"))).Sort(new BsonDocument(reservedField, -1))
                .Project(new BsonDocument(reservedField, 1)).FirstOrDefaultAsync(token);
            sequences[sequence] = Math.Max(sequences[sequence], reserved?[reservedField].AsInt64 ?? 0L);
        }

        foreach (var (name, validator, exists) in definitions)
        {
            await lease.RenewAsync(token);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { exists ? "collMod" : "create", name }, { "validator", validator },
                { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() }
            }, cancellationToken: token);
        }
        foreach (var (name, value) in sequences.Where(item => item.Value > 0))
        {
            await lease.RenewAsync(token);
            await database.GetCollection<BsonDocument>("idCounters").WithWriteConcern(WriteConcern.WMajority)
                .UpdateOneAsync(new BsonDocument("_id", name), Builders<BsonDocument>.Update.Max("value", value),
                    new UpdateOptions { IsUpsert = true }, token);
        }

        await Index("identitySessions", ["userId", "status"], lease, token);
        await Index("identitySessions", ["familyId", "status"], lease, token);
        await Index("identitySessions", ["issuer", "providerSid", "status"], lease, token,
            partial: new BsonDocument("providerSid", new BsonDocument("$type", "string")));
        await Index("identitySessions", ["absoluteExpiresAt"], lease, token, expires: TimeSpan.Zero);
        await Index("oidcTransactions", ["expiresAt"], lease, token, expires: TimeSpan.Zero);
        await Index("accountInvitations", ["userId"], lease, token, unique: true);
        await Index("accountInvitations", ["secretHash"], lease, token, unique: true,
            partial: new BsonDocument("secretHash", new BsonDocument("$type", "string")));
        await Index("accountInvitations", ["status", "expiresAt", "_id"], lease, token);
        await Index("accountInvitations", ["scope.organizationId", "scope.branchId", "status", "_id"], lease, token);
        await Index("accountInvitations", ["scope.organizationId", "scope.shopId", "status", "_id"], lease, token);
        await Index("identityProviderIntents", ["userId", "kind", "status"], lease, token);
        await Index("identityReadEvents", ["actorId", "createdAt"], lease, token);
    }

    private async Task NoDuplicates(string name, string field, bool optional, MigrationLease lease, CancellationToken token)
    {
        await lease.RenewAsync(token);
        var pipeline = new List<BsonDocument>();
        if (optional) pipeline.Add(new("$match", new BsonDocument(field, new BsonDocument("$exists", true))));
        pipeline.Add(new("$group", new BsonDocument { { "_id", "$" + field }, { "count", new BsonDocument("$sum", 1) } }));
        pipeline.Add(new("$match", new BsonDocument("count", new BsonDocument("$gt", 1))));
        pipeline.Add(new("$limit", 1));
        if (await database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority)
            .Aggregate<BsonDocument>(pipeline).AnyAsync(token))
            throw new InvalidOperationException($"Identity migration found duplicate {field} in {name}; reconcile approved records before retrying");
    }
    private async Task Index(string collection, string[] fields, MigrationLease lease, CancellationToken token,
        bool unique = false, BsonDocument? partial = null, TimeSpan? expires = null)
    {
        await lease.RenewAsync(token);
        await database.GetCollection<BsonDocument>(collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument(fields.Select(field => new BsonElement(field, 1))),
            new CreateIndexOptions<BsonDocument>
            {
                Name = (unique ? "ux_" : expires.HasValue ? "ttl_" : "ix_") + string.Join('_', fields),
                Unique = unique, PartialFilterExpression = partial, ExpireAfter = expires
            }), cancellationToken: token);
    }
    private static BsonDocument Preserve(BsonDocument previous, BsonDocument required) =>
        previous.ElementCount == 0 ? required : Contains(previous, required) ? previous :
            new BsonDocument("$and", new BsonArray { previous, required });
    private static bool Contains(BsonDocument validator, BsonDocument required) => validator.Equals(required) ||
        validator.TryGetValue("$and", out var value) && value.IsBsonArray &&
        value.AsBsonArray.Any(item => item.IsBsonDocument && Contains(item.AsBsonDocument, required));
    private static InvalidOperationException Incompatible(string name) => new(
        $"Identity migration found incompatible records in {name}; reconcile approved records before retrying");
}
