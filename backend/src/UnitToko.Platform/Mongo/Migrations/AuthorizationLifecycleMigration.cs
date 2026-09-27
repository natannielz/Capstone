using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo.Migrations;

// Frozen v3 contract: lifecycle metadata extends the v1 authorization document format.
internal sealed class AuthorizationLifecycleMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 3;

    public async Task ApplyAsync(MigrationLease lease, CancellationToken token)
    {
        var definitions = new List<(string Name, BsonDocument Validator, bool Exists)>();
        foreach (var (name, required) in LifecycleSchemas.Collections())
        {
            await lease.RenewAsync(token);
            var existing = await (await database.ListCollectionsAsync(
                new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, token))
                .SingleOrDefaultAsync(token);
            var previous = existing?.GetValue("options", new BsonDocument()).AsBsonDocument
                .GetValue("validator", new BsonDocument()).AsBsonDocument ?? new BsonDocument();
            var validator = Preserve(previous, required);
            definitions.Add((name, validator, existing is not null));
            if (await database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority)
                    .Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(token))
                throw Incompatible(name);
        }

        // All preflight checks precede schema/index/counter mutations. No record is
        // repaired automatically and no names, approvals, scopes or accounts are seeded.
        await lease.RenewAsync(token);
        var duplicate = await database.GetCollection<BsonDocument>("organizationProfiles")
            .WithReadConcern(ReadConcern.Majority).Aggregate<BsonDocument>(new BsonDocument[]
            {
                new("$group", new BsonDocument
                {
                    { "_id", new BsonDocument { { "kind", "$kind" }, { "parentId", "$parentId" },
                        { "normalizedCode", "$normalizedCode" } } }, { "count", new BsonDocument("$sum", 1) }
                }),
                new("$match", new BsonDocument("count", new BsonDocument("$gt", 1))),
                new("$limit", 1)
            }).AnyAsync(token);
        if (duplicate)
            throw new InvalidOperationException("Lifecycle migration found duplicate scoped codes in organizationProfiles; reconcile approved records before retrying");

        var sequences = new Dictionary<string, long>();
        foreach (var name in new[] { "authorizationScopes", "organizationProposals", "authorizationReadEvents", "authorizationHistory" })
        {
            await lease.RenewAsync(token);
            var collection = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var validId = new BsonDocument("$jsonSchema", new BsonDocument
            {
                { "required", new BsonArray { "_id" } },
                { "properties", new BsonDocument("_id", LifecycleSchemas.Long(1)) }
            });
            if (await collection.Find(new BsonDocument("$nor", new BsonArray { validId })).Limit(1).AnyAsync(token))
                throw Incompatible(name);
            var maximum = await collection.Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(new BsonDocument("_id", -1)).Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(token);
            var counter = await database.GetCollection<BsonDocument>("idCounters")
                .WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(token);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 < 0))
                throw new InvalidOperationException($"Lifecycle migration found an incompatible idCounters sequence for {name}; reconcile its high-water mark before retrying");
            sequences.Add(name, maximum?["_id"].AsInt64 ?? 0L);
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

        foreach (var (name, maximum) in sequences.Where(item => item.Value > 0))
        {
            await lease.RenewAsync(token);
            await database.GetCollection<BsonDocument>("idCounters").WithWriteConcern(WriteConcern.WMajority)
                .UpdateOneAsync(new BsonDocument("_id", name), Builders<BsonDocument>.Update.Max("value", maximum),
                    new UpdateOptions { IsUpsert = true }, token);
        }

        await Index("organizationProfiles", ["kind", "parentId", "normalizedCode"], true, lease, token);
        await Index("organizationProfiles", ["kind", "parentId", "_id"], false, lease, token);
        await Index("organizationProposals", ["status", "createdAt", "_id"], false, lease, token);
        await Index("organizationProposals", ["scopeId", "status", "_id"], false, lease, token);
        await Index("organizationProposals", ["requestedBy", "createdAt", "_id"], false, lease, token);
        await Index("authorizationHistory", ["scope.kind", "scope.organizationId", "createdAt", "_id"], false, lease, token);
        await Index("authorizationReadEvents", ["actorId", "createdAt"], false, lease, token);
        await Index("authorizationReadEvents", ["operation", "createdAt"], false, lease, token);
    }

    private async Task Index(string collection, string[] fields, bool unique, MigrationLease lease, CancellationToken token)
    {
        await lease.RenewAsync(token);
        await database.GetCollection<BsonDocument>(collection).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument(fields.Select(field => new BsonElement(field, 1))),
                new CreateIndexOptions { Name = (unique ? "ux_" : "ix_") + string.Join('_', fields), Unique = unique }),
            cancellationToken: token);
    }

    private static InvalidOperationException Incompatible(string collection) => new(
        $"Lifecycle migration found incompatible records in {collection}; reconcile approved records before retrying");

    private static BsonDocument Preserve(BsonDocument previous, BsonDocument required) =>
        previous.ElementCount == 0 ? required : Contains(previous, required) ? previous :
            new BsonDocument("$and", new BsonArray { previous, required });

    private static bool Contains(BsonDocument validator, BsonDocument required) =>
        validator.Equals(required) || validator.TryGetValue("$and", out var conjunction) &&
        conjunction.IsBsonArray && conjunction.AsBsonArray.Any(item => item.IsBsonDocument && Contains(item.AsBsonDocument, required));
}
