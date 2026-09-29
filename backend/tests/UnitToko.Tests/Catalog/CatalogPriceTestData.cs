using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Catalog;

internal sealed class CatalogPriceTestData(CatalogAuthoringTestData catalog) : IAsyncDisposable
{
    internal CatalogAuthoringTestData Catalog { get; } = catalog;
    internal ActorContext Maker => Catalog.Maker;
    internal ActorContext Checker { get; private set; } = null!;
    internal long SkuId { get; private set; }
    internal CatalogPriceService Service => NewService();
    internal CatalogPriceService NewService(MongoClient? client = null)
    {
        var selected = client ?? Catalog.Client;
        var database = selected.GetDatabase(Catalog.Database.DatabaseNamespace.DatabaseName);
        return new(database, new(selected), new(database), Catalog.Clock);
    }
    internal static async Task<CatalogPriceTestData> CreateAsync(MongoFixture fixture, bool install = true, bool beforeMigration = false)
    {
        var data = new CatalogPriceTestData(await CatalogAuthoringTestData.CreateAsync(fixture, beforeMigration || !install));
        try
        {
            // Draft storage only for predecessor upgrade fixtures. Current schema uses the real migration.
            var installed = await (await data.Catalog.Database.ListCollectionNamesAsync()).ToListAsync();
            if (install && !installed.Contains("catalogPriceProposals")) foreach (var name in CatalogPriceSchemas.Collections)
            {
                if (name == "priceVersions")
                    await data.Catalog.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
                        { "collMod", name }, { "validator", CatalogPriceSchemas.Validator(name) },
                        { "validationLevel", "strict" }, { "validationAction", "error" } });
                else await data.Catalog.Database.CreateCollectionAsync(name, new CreateCollectionOptions<BsonDocument> {
                    Validator = CatalogPriceSchemas.Validator(name), ValidationLevel = DocumentValidationLevel.Strict,
                    ValidationAction = DocumentValidationAction.Error });
                var key = CatalogPriceSchemas.Key(name);
                if (!key.Equals(new BsonDocument("_id", 1)))
                    await data.Rows(name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key,
                        new CreateIndexOptions { Unique = true, Name = "ux_catalog_price_" + name }));
            }
            await data.Catalog.Base.AddGrantAsync(data.Maker, 3100, CatalogAuthoringTestData.Shop, [Permissions.PricingManage]);
            data.Checker = await data.Catalog.ActorAsync(7000, CatalogAuthoringTestData.Shop, Permissions.PricingApprove);
            await PersonTestData.SeedDistinctPeopleAsync(data.Catalog.Database, data.Catalog.Clock, data.Maker.UserId, data.Checker.UserId);
            data.SkuId = (await data.Catalog.SkuAsync()).Id.Value;
            return data;
        }
        catch { await data.DisposeAsync(); throw; }
    }
    internal async Task<BsonDocument> ProposeAsync(string key = "price-propose", string amount = "12000.25")
    {
        var preview = await Service.PreviewAsync(Maker, SkuId, CatalogPriceAccess.Prepare);
        return await Service.ProposeAsync(Maker, SkuId, preview.Expected,
            new("IDR", UnitToko.Modules.Accounting.Money.Parse(amount)), Meta(key));
    }
    internal Task<BsonDocument> ApproveAsync(BsonDocument proposal, string key = "price-approve") =>
        Service.DecideAsync(Checker, proposal["proposalId"].AsInt64, 1, CatalogPriceDecision.Approve,
            "Synthetic independent price review", Meta(key));
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic price review", "price-service-tests");
    internal IMongoCollection<BsonDocument> Rows(string name) => Catalog.Rows(name);
    internal async Task<BsonDocument> StateAsync()
    {
        var result = new BsonDocument();
        foreach (var name in CatalogPriceSchemas.Collections.Concat(["auditEvents", "outboxEvents", "idempotencyRecords",
            "skus", "productFamilies", "catalogCategories", "identities", "identitySessions", "authorizationGrants", "memberships", "authorizationScopes", "personRecords", "personAccountBindings",
            "personEquivalenceGroups", "personEvidenceSources", "personEvidenceVersions"]))
            result[name] = new BsonArray(await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    public ValueTask DisposeAsync() => Catalog.DisposeAsync();
}
