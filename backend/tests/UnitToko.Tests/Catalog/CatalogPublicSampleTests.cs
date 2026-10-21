using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Identity;
using T = UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Catalog;

/// <summary>Guests see only the products staff chose as samples under maker/checker, and only while they are published.</summary>
[Collection("Mongo")]
public sealed class CatalogPublicSampleTests(MongoFixture fixture)
{
    private const string Origin = "https://portal.unit-toko.co.id";
    private const long MakerId = 9811, CheckerId = 9812, BothId = 9813;

    private sealed record Setup(T Data, CatalogPublicSampleService Service, ActorContext Maker, ActorContext Checker) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Data.DisposeAsync();
    }
    private sealed record Product(long FamilyId, long SkuId, string PublicId, string FamilyPublicId, string Name);

    private static async Task<Setup> CreateAsync(MongoFixture fixture)
    {
        var data = await T.CreateAsync(fixture);
        await data.AssortmentsAsync();
        var maker = await data.ActorAsync(MakerId, T.Shop, Permissions.CatalogPublicPropose);
        var checker = await data.ActorAsync(CheckerId, T.Shop, Permissions.CatalogPublicApprove);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, checker.UserId);
        return new(data, new CatalogPublicSampleService(data.Database, new MongoUnitOfWork(data.Client), new(data.Database), data.Clock), maker, checker);
    }

    /// <summary>One product (catalog family) with one package, published in the given assortments.</summary>
    private static async Task<Product> ProductAsync(T data, string category, string name, string code, params long[] assortments)
    {
        var created = await data.CategoryAsync(code: category);
        var family = await data.Service.CreateFamilyAsync(data.Maker, new(T.Shop, created.Id, name, "Deskripsi " + name, []), T.Metadata("family-" + code));
        var sku = await data.Service.CreateSkuAsync(data.Maker, new(family.Id, family.Version, code, "base-" + code.ToLowerInvariant(), T.Definition()), T.Metadata("sku-" + code));
        if (assortments.Length > 0) await PublishAsync(data, sku.Id.Value, assortments);
        return new(family.Id.Value, sku.Id.Value, (await data.RowAsync("skus", sku.Id.Value))["publicId"].AsString,
            (await data.RowAsync("productFamilies", family.Id.Value))["publicId"].AsString, name);
    }

    private static async Task PublishAsync(T data, long skuId, params long[] assortments)
    {
        var row = await data.RowAsync("skus", skuId);
        var touched = row["catalogListings"].AsBsonArray.Select(item => item["assortmentId"].AsInt64).Concat(assortments).Distinct().Order().ToArray();
        var expected = new List<CatalogAssortmentVersion>();
        foreach (var id in touched) expected.Add(new(new(id), (await data.RowAsync("catalogAssortments", id))["version"].AsInt64));
        await data.Service.SetPublicationsAsync(data.Maker, new(new(skuId), row["revision"].AsInt64, [.. assortments.Select(id => new LongId(id))], [.. expected]),
            T.Metadata("publish-" + skuId + "-" + row["revision"].AsInt64.ToString(CultureInfo.InvariantCulture)));
    }

    private static async Task ApproveAsync(Setup setup, ImmutableArray<long> adds, ImmutableArray<long> removes, string key)
    {
        var proposal = await setup.Service.ProposeAsync(setup.Maker, adds, removes, T.Metadata(key, "Contoh barang untuk tamu"));
        await setup.Service.DecideAsync(setup.Checker, proposal["proposalId"].AsInt64, CatalogPublicSampleDecision.Approve,
            T.Metadata(key + "-approve", "Diperiksa: barang umum tanpa harga"));
    }

    [Fact]
    public async Task Guest_sees_only_approved_samples_without_prices()
    {
        await using var setup = await CreateAsync(fixture);
        var data = setup.Data;
        var pencil = await ProductAsync(data, "ATK", "Pensil", "PENCIL-12", 501);
        var coffee = await ProductAsync(data, "KOPI", "Kopi bubuk", "KOPI-12", 501);
        var paper = await ProductAsync(data, "KERTAS", "Kertas A4", "KERTAS-12", 501);
        Assert.Equal(0, (await data.BuyerService.QueryPublicAsync(0, 24, null)).Total);

        var proposal = await setup.Service.ProposeAsync(setup.Maker, [pencil.FamilyId, coffee.FamilyId], [], T.Metadata("sample-open", "Contoh barang untuk tamu"));
        Assert.Equal(proposal, await setup.Service.ProposeAsync(setup.Maker, [pencil.FamilyId, coffee.FamilyId], [], T.Metadata("sample-open", "Contoh barang untuk tamu")));
        Assert.Equal(0, (await data.BuyerService.QueryPublicAsync(0, 24, null)).Total);
        var reviewed = await setup.Service.QueryAsync(setup.Checker, null, 0, 50);
        Assert.Equal(3, reviewed.Total);
        var pending = Assert.Single(reviewed.Items, item => item.FamilyId == pencil.FamilyId).Pending!;
        Assert.True(pending.Adding); Assert.False(pending.OwnProposal); Assert.Equal(proposal["proposalId"].AsInt64, pending.ProposalId);
        Assert.Null(Assert.Single(reviewed.Items, item => item.FamilyId == paper.FamilyId).Pending);
        Assert.True(Assert.Single((await setup.Service.QueryAsync(setup.Maker, "pensil", 0, 50)).Items).Pending!.OwnProposal);
        Assert.Equal(new[] { coffee.FamilyId, pencil.FamilyId }.Order(), Assert.Single(reviewed.Proposals).Adds.Select(item => item.FamilyId).Order());
        Assert.DoesNotContain(typeof(CatalogPublicSamplePending).GetProperties(), property => property.Name.Contains("ProposedBy", StringComparison.Ordinal));

        await setup.Service.DecideAsync(setup.Checker, proposal["proposalId"].AsInt64, CatalogPublicSampleDecision.Approve,
            T.Metadata("sample-open-approve", "Diperiksa: barang umum tanpa harga"));
        var samples = await data.CountAsync("catalogPublicSamples");
        var page = await data.BuyerService.QueryPublicAsync(0, 24, null);
        Assert.Equal(2, samples);
        Assert.Equal(samples, page.Total);
        Assert.Equal(["Kopi bubuk", "Pensil"], page.Items.Select(item => item.Name).Order());
        Assert.DoesNotContain(page.Items, item => item.PublicId == paper.FamilyPublicId);

        using var http = new Http(data);
        using var guest = http.Anonymous();
        using var response = await guest.GetAsync("/api/v1/public/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // G-003: no shared cache may keep a guest answer, so a withdrawn sample is gone on the very next request.
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.CacheControl?.Public);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(samples.ToString(CultureInfo.InvariantCulture), body.GetProperty("total").GetString());
        foreach (var item in body.GetProperty("items").EnumerateArray())
            Assert.DoesNotContain(item.EnumerateObject(), property => property.Name.Contains("price", StringComparison.OrdinalIgnoreCase));
        Assert.True(Assert.Single((await setup.Service.QueryAsync(setup.Checker, "kopi", 0, 50)).Items).IsSample);
        // G-027: the guest search takes the same 128 characters as the store search box, and refuses more.
        Assert.Equal(0, (await data.BuyerService.QueryPublicAsync(0, 24, new string('x', 128))).Total);
        Assert.Equal("public_catalog_query_invalid", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            data.BuyerService.QueryPublicAsync(0, 24, new string('x', 129)))).Code);
        using var long128 = await guest.GetAsync("/api/v1/public/catalog?q=" + new string('x', 128));
        Assert.Equal(HttpStatusCode.OK, long128.StatusCode);
    }

    [Fact]
    public async Task Sample_product_shows_once_with_all_published_packages()
    {
        await using var setup = await CreateAsync(fixture);
        var data = setup.Data;
        var pencil = await ProductAsync(data, "ATK", "Pensil", "PENCIL-12", 501);
        var family = await data.RowAsync("productFamilies", pencil.FamilyId);
        var box = await data.Service.CreateSkuAsync(data.Maker, new(new(pencil.FamilyId), family["revision"].AsInt64, "PENCIL-24", "base-pencil-24", T.Definition(24, "Pak")),
            T.Metadata("sku-PENCIL-24"));
        await PublishAsync(data, box.Id.Value, 501);
        family = await data.RowAsync("productFamilies", pencil.FamilyId);
        var draft = await data.Service.CreateSkuAsync(data.Maker, new(new(pencil.FamilyId), family["revision"].AsInt64, "PENCIL-48", "base-pencil-48", T.Definition(48, "Karton")),
            T.Metadata("sku-PENCIL-48"));
        await ApproveAsync(setup, [pencil.FamilyId], [], "sample-packages");

        // G-004 (D1): one guest product per sample, listing only its currently published packages.
        var page = await data.BuyerService.QueryPublicAsync(0, 24, null);
        var product = Assert.Single(page.Items);
        Assert.Equal(1, page.Total);
        Assert.Equal(pencil.FamilyPublicId, product.PublicId);
        Assert.Equal(["PENCIL-12", "PENCIL-24"], product.Packages.Select(item => item.Code).Order());
        Assert.Equal(1, Assert.Single(page.Categories).Count);
        Assert.Equal(1, (await data.BuyerService.QueryPublicAsync(0, 24, "pak")).Total);
        // A shared package link still opens its product; the product link opens the same product.
        var publicId = (await data.RowAsync("skus", box.Id.Value))["publicId"].AsString;
        Assert.Equal(product, await data.BuyerService.ReadPublicItemAsync(publicId));
        Assert.Equal(product, await data.BuyerService.ReadPublicItemAsync(pencil.FamilyPublicId));
        // An unpublished package's link opens its sample product, which still lists only the published packages.
        Assert.Equal(product, await data.BuyerService.ReadPublicItemAsync((await data.RowAsync("skus", draft.Id.Value))["publicId"].AsString));
        Assert.DoesNotContain(product.Packages, item => item.Code == "PENCIL-48");
        using var http = new Http(data);
        using var guest = http.Anonymous();
        var body = await (await guest.GetAsync("/api/v1/public/catalog/" + publicId)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(pencil.FamilyPublicId, body.GetProperty("publicId").GetString());
        Assert.Equal(2, body.GetProperty("packages").GetArrayLength());
    }

    [Fact]
    public async Task Non_sample_product_by_direct_reference_is_unavailable()
    {
        await using var setup = await CreateAsync(fixture);
        var data = setup.Data;
        var pencil = await ProductAsync(data, "ATK", "Pensil", "PENCIL-12", 501);
        var coffee = await ProductAsync(data, "KOPI", "Kopi bubuk", "KOPI-12", 501);
        await ApproveAsync(setup, [pencil.FamilyId], [], "sample-pencil");

        Assert.NotNull(await data.BuyerService.ReadPublicItemAsync(pencil.PublicId));
        Assert.Null(await data.BuyerService.ReadPublicItemAsync(coffee.PublicId));
        using var http = new Http(data);
        using var guest = http.Anonymous();
        using var hidden = await guest.GetAsync("/api/v1/public/catalog/" + coffee.PublicId);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.True(hidden.Headers.CacheControl?.NoStore);
        var text = await hidden.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Kopi", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KOPI-12", text, StringComparison.OrdinalIgnoreCase);
        using var shown = await guest.GetAsync("/api/v1/public/catalog/" + pencil.PublicId);
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.True(shown.Headers.CacheControl?.NoStore);
        // An unknown id answers exactly like a non-sample one.
        using var unknown = await guest.GetAsync("/api/v1/public/catalog/" + Guid.NewGuid().ToString("D"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        static string Shape(string body) {
            if (body.Length == 0) return body;
            var node = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject(); node.Remove("traceId"); return node.ToJsonString();
        }
        Assert.Equal(Shape(text), Shape(await unknown.Content.ReadAsStringAsync()));
        // A non-sample product never surfaces in a search either.
        Assert.Equal(0, (await data.BuyerService.QueryPublicAsync(0, 24, "kopi")).Total);
    }

    [Fact]
    public async Task Proposer_cannot_approve_own_proposal()
    {
        await using var setup = await CreateAsync(fixture);
        var data = setup.Data;
        var both = await data.ActorAsync(BothId, T.Shop, Permissions.CatalogPublicPropose, Permissions.CatalogPublicApprove);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, both.UserId);
        var pencil = await ProductAsync(data, "ATK", "Pensil", "PENCIL-12", 501);
        var proposal = await setup.Service.ProposeAsync(both, [pencil.FamilyId], [], T.Metadata("sample-self", "Contoh barang"));
        var id = proposal["proposalId"].AsInt64;

        var refused = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => setup.Service.DecideAsync(both, id, CatalogPublicSampleDecision.Approve,
            T.Metadata("sample-self-approve", "Menyetujui sendiri")));
        Assert.Equal("self_approval", refused.Code);
        using (var http = new Http(data))
        {
            using var response = await http.PostAsync(both.UserId, "/api/v1/catalog/public-samples/proposals/" + id.ToString(CultureInfo.InvariantCulture) + "/approve",
                new { reason = "Menyetujui sendiri" }, "sample-self-approve-http");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("self_approval", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.True(Assert.Single((await setup.Service.QueryAsync(both, null, 0, 50)).Items).Pending!.OwnProposal);
        Assert.Equal(0, (await data.BuyerService.QueryPublicAsync(0, 24, null)).Total);
        // A family already waiting for review cannot be proposed again until that proposal is decided.
        Assert.Equal("catalog_public_sample_pending", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            setup.Service.ProposeAsync(setup.Maker, [pencil.FamilyId], [], T.Metadata("sample-second", "Usulan kedua")))).Code);
        await setup.Service.DecideAsync(setup.Checker, id, CatalogPublicSampleDecision.Approve, T.Metadata("sample-self-checker", "Diperiksa orang kedua"));
        Assert.Equal(1, (await data.BuyerService.QueryPublicAsync(0, 24, null)).Total);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => setup.Service.QueryAsync(data.Buyer, null, 0, 50));
    }

    [Fact]
    public async Task Proposal_beyond_two_hundred_samples_is_refused()
    {
        await using var setup = await CreateAsync(fixture);
        var data = setup.Data;
        var pencil = await ProductAsync(data, "ATK", "Pensil", "PENCIL-12", 501);
        var owner = (await data.RowAsync("productFamilies", pencil.FamilyId))["authoringOwner"].AsBsonDocument;
        await data.Rows("catalogPublicSamples").InsertManyAsync(Enumerable.Range(0, 200).Select(index => new BsonDocument {
            { "_id", 900000L + index }, { "schemaVersion", 1 }, { "ownerScope", owner.DeepClone() }, { "revision", 1L },
            { "addedAt", data.Clock.UtcNow.UtcDateTime }, { "approvedProposalId", BsonNull.Value },
            { "migratedFrom", new BsonDocument { { "assortmentId", 501L }, { "showcaseDecisionId", 1L } } } }));
        Assert.Equal("public_sample_limit", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            setup.Service.ProposeAsync(setup.Maker, [pencil.FamilyId], [], T.Metadata("sample-over", "Satu lagi")))).Code);
        // Narrowing the set is always allowed, even at the limit.
        var narrowed = await setup.Service.ProposeAsync(setup.Maker, [pencil.FamilyId], [900000L], T.Metadata("sample-swap", "Tukar contoh"));
        Assert.True(narrowed["proposalId"].AsInt64 > 0);
    }

    /// <summary>Approval is where the limit and the proposed state really bind: both are checked again under the approval counter.</summary>
    [Fact]
    public async Task Approval_rechecks_the_limit_and_the_proposed_products()
    {
        await using var setup = await CreateAsync(fixture);
        var data = setup.Data;
        var a = await ProductAsync(data, "ATK", "Pensil", "PENCIL-12", 501);
        var b = await ProductAsync(data, "KOPI", "Kopi bubuk", "KOPI-12", 501);
        var c = await ProductAsync(data, "KERTAS", "Kertas A4", "KERTAS-12", 501);
        var d = await ProductAsync(data, "MAP", "Map plastik", "MAP-12", 501);
        var owner = (await data.RowAsync("productFamilies", a.FamilyId))["authoringOwner"].AsBsonDocument;
        BsonDocument Sample(long id) => new() { { "_id", id }, { "schemaVersion", 1 }, { "ownerScope", owner.DeepClone() }, { "revision", 1L },
            { "addedAt", data.Clock.UtcNow.UtcDateTime }, { "approvedProposalId", BsonNull.Value },
            { "migratedFrom", new BsonDocument { { "assortmentId", 501L }, { "showcaseDecisionId", 1L } } } };
        await data.Rows("catalogPublicSamples").InsertManyAsync(Enumerable.Range(0, 199).Select(index => Sample(900000L + index)));
        // Each proposal passes on its own at 199 samples.
        async Task<long> Propose(Product product, string key) =>
            (await setup.Service.ProposeAsync(setup.Maker, [product.FamilyId], [], T.Metadata(key, "Contoh barang"))) ["proposalId"].AsInt64;
        var first = await Propose(a, "cap-a"); var second = await Propose(b, "cap-b");
        var deactivated = await Propose(c, "cap-c"); var already = await Propose(d, "cap-d");
        Task<BsonDocument> Approve(long id, string key) => setup.Service.DecideAsync(setup.Checker, id, CatalogPublicSampleDecision.Approve, T.Metadata(key, "Diperiksa"));

        await data.Rows("productFamilies").UpdateOneAsync(new BsonDocument("_id", c.FamilyId), Builders<BsonDocument>.Update.Set("active", false));
        Assert.Equal("catalog_public_sample_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() => Approve(deactivated, "cap-c-approve"))).Code);
        await data.Rows("catalogPublicSamples").InsertOneAsync(Sample(d.FamilyId));
        Assert.Equal("catalog_public_sample_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() => Approve(already, "cap-d-approve"))).Code);
        await data.Rows("catalogPublicSamples").DeleteOneAsync(new BsonDocument("_id", d.FamilyId));

        await Approve(first, "cap-a-approve");
        Assert.Equal(200L, await data.CountAsync("catalogPublicSamples"));
        Assert.Equal("public_sample_limit", (await Assert.ThrowsAsync<PersistenceConflictException>(() => Approve(second, "cap-b-approve"))).Code);
        Assert.Equal(200L, await data.CountAsync("catalogPublicSamples"));
        Assert.Equal("pending", (await data.RowAsync("catalogPublicSampleProposals", second))["state"].AsString);
    }

    [Fact]
    public async Task Approval_requires_fresh_multi_factor_sign_in()
    {
        await using var setup = await CreateAsync(fixture);
        var data = setup.Data;
        var pencil = await ProductAsync(data, "ATK", "Pensil", "PENCIL-12", 501);
        var id = (await setup.Service.ProposeAsync(setup.Maker, [pencil.FamilyId], [], T.Metadata("fresh-open", "Contoh barang")))["proposalId"].AsInt64;
        // Signed in with MFA more than five minutes ago, or without MFA at all: the approval asks for a fresh factor first.
        foreach (var (actor, key) in new[] { (setup.Checker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-10) }, "fresh-stale"),
            (setup.Checker with { Assurance = FactorAssurance.SingleFactor }, "fresh-single") })
            Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
                setup.Service.DecideAsync(actor, id, CatalogPublicSampleDecision.Approve, T.Metadata(key, "Diperiksa")))).Code);
        Assert.Equal(0L, await data.CountAsync("catalogPublicSamples"));
        await setup.Service.DecideAsync(setup.Checker, id, CatalogPublicSampleDecision.Approve, T.Metadata("fresh-approve", "Diperiksa"));
        Assert.Equal(1L, await data.CountAsync("catalogPublicSamples"));
    }

    [Fact]
    public async Task A_product_lists_a_bounded_number_of_packages()
    {
        await using var setup = await CreateAsync(fixture);
        var data = setup.Data;
        Assert.Equal(64, CatalogDefinitionQueryService.PublicPackageLimit);
        var pencil = await ProductAsync(data, "ATK", "Pensil", "PENCIL-12", 501);
        foreach (var (code, contents) in new[] { ("PENCIL-24", 24), ("PENCIL-48", 48) })
        {
            var family = await data.RowAsync("productFamilies", pencil.FamilyId);
            var sku = await data.Service.CreateSkuAsync(data.Maker, new(new(pencil.FamilyId), family["revision"].AsInt64, code, "base-" + code.ToLowerInvariant(),
                T.Definition(contents, "Pak " + contents)), T.Metadata("sku-" + code));
            await PublishAsync(data, sku.Id.Value, 501);
        }
        await ApproveAsync(setup, [pencil.FamilyId], [], "sample-bounded");
        Assert.Equal(3, Assert.Single((await data.BuyerService.QueryPublicAsync(0, 24, null)).Items).Packages.Length);
        // With a lower bound the product keeps its first packages instead of failing the page.
        var bounded = new CatalogDefinitionQueryService(data.Database, new(data.Database, new(data.Client), data.Clock), data.Clock) { PackageLimit = 2 };
        var item = Assert.Single((await bounded.QueryPublicAsync(0, 24, null)).Items);
        Assert.Equal(["PENCIL-12", "PENCIL-24"], item.Packages.Select(package => package.Code));
        Assert.Equal(2, (await bounded.ReadPublicItemAsync(pencil.PublicId))!.Packages.Length);
    }

    [Fact]
    public async Task Unpublished_sample_disappears_immediately()
    {
        await using var setup = await CreateAsync(fixture);
        var data = setup.Data;
        var pencil = await ProductAsync(data, "ATK", "Pensil", "PENCIL-12", 501);
        var coffee = await ProductAsync(data, "KOPI", "Kopi bubuk", "KOPI-12", 502);
        await ApproveAsync(setup, [pencil.FamilyId, coffee.FamilyId], [], "sample-both");
        Assert.Equal(2, (await data.BuyerService.QueryPublicAsync(0, 24, null)).Total);

        await PublishAsync(data, pencil.SkuId);
        var page = await data.BuyerService.QueryPublicAsync(0, 24, null);
        Assert.Equal(1, page.Total);
        Assert.Equal(coffee.FamilyPublicId, Assert.Single(page.Items).PublicId);
        Assert.Null(await data.BuyerService.ReadPublicItemAsync(pencil.PublicId));

        // Its assortment changes: a suspended assortment no longer publishes the product to anyone.
        await data.Rows("catalogAssortments").UpdateOneAsync(new BsonDocument("_id", 502L), Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
        Assert.Equal(0, (await data.BuyerService.QueryPublicAsync(0, 24, null)).Total);
        Assert.Null(await data.BuyerService.ReadPublicItemAsync(coffee.PublicId));
        // Staff still see both as samples, with no published package, so they can remove them.
        Assert.All((await setup.Service.QueryAsync(setup.Checker, null, 0, 50)).Items, item => { Assert.True(item.IsSample); Assert.Equal(0, item.Packages); });
    }

    [Fact]
    public async Task Hidden_photo_never_falls_back_to_private_original()
    {
        await using var setup = await CreateAsync(fixture);
        var data = setup.Data;
        var pencil = await ProductAsync(data, "ATK", "Pensil", "PENCIL-12", 501);
        await ApproveAsync(setup, [pencil.FamilyId], [], "sample-photo");
        using var http = new Http(data);
        using var guest = http.Anonymous();
        // No clean public copy exists, so the guest item carries no photo of any kind: no original, evidence or hash reference.
        foreach (var path in new[] { "/api/v1/public/catalog", "/api/v1/public/catalog/" + pencil.PublicId })
        {
            using var response = await guest.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            foreach (var word in new[] { "photo", "image", "original", "evidence", "sha256", "foto" })
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(typeof(PublicCatalogItem).GetProperties(), property =>
            new[] { "Photo", "Image", "Original", "Evidence" }.Any(word => property.Name.Contains(word, StringComparison.OrdinalIgnoreCase)));
        // The private original stays behind sign-in, also for a sample product.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/catalog/variants/" + pencil.PublicId + "/primary-photo/query") { Content = JsonContent.Create(new { purpose = "catalog-photo" }) };
        request.Headers.Add("Origin", Origin);
        using var original = await guest.SendAsync(request);
        Assert.Contains(original.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.DoesNotContain("image", original.Content.Headers.ContentType?.MediaType ?? "", StringComparison.OrdinalIgnoreCase);
        // The only anonymous catalog routes are the two public reads; none serves photo bytes.
        var anonymous = http.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null && endpoint.RoutePattern.RawText!.Contains("catalog", StringComparison.OrdinalIgnoreCase))
            .Select(endpoint => endpoint.RoutePattern.RawText!.TrimEnd('/')).Order().ToArray();
        Assert.Equal(["/api/v1/public/catalog", "/api/v1/public/catalog/{publicId}"], anonymous);
        // Unpublishing hides the item at once; there is no cached photo path that could outlive it.
        await PublishAsync(data, pencil.SkuId);
        using var gone = await guest.GetAsync("/api/v1/public/catalog/" + pencil.PublicId);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Migration_122_converts_public_assortments_to_samples_of_their_published_products()
    {
        await using var setup = await CreateAsync(fixture);
        var data = setup.Data;
        var pencil = await ProductAsync(data, "ATK", "Pensil", "PENCIL-12", 501);
        var coffee = await ProductAsync(data, "KOPI", "Kopi bubuk", "KOPI-12", 501);
        var paper = await ProductAsync(data, "KERTAS", "Kertas A4", "KERTAS-12", 502);
        var draft = await ProductAsync(data, "MAP", "Map plastik", "MAP-12");
        var at = DateTime.SpecifyKind(new DateTime(2026, 10, 20, 3, 0, 0), DateTimeKind.Utc);
        // Version 121 state: 501 was approved public; 502 was made public and later private again.
        await data.Rows("catalogPublicShowcaseHeads").InsertManyAsync([
            new BsonDocument { { "_id", 501L }, { "schemaVersion", 1 }, { "isPublic", true }, { "revision", 1L }, { "decisionId", 71L }, { "updatedAt", at }, { "fence", 0L } },
            new BsonDocument { { "_id", 502L }, { "schemaVersion", 1 }, { "isPublic", false }, { "revision", 2L }, { "decisionId", 72L }, { "updatedAt", at }, { "fence", 1L } }]);
        Assert.Equal(0L, await data.CountAsync("catalogPublicSamples"));

        // Re-run exactly the last step, as a deployment upgrading a version 121 database does.
        Assert.Equal(1, (await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", CollectionMigrations.CurrentVersion))).DeletedCount);
        Assert.Equal(122, CollectionMigrations.CurrentVersion);
        var migrations = new CollectionMigrations(data.Database, data.Clock);
        await migrations.ApplyAsync();
        Assert.True(await migrations.IsCurrentAsync());

        var rows = await data.Rows("catalogPublicSamples").Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.Equal(new[] { pencil.FamilyId, coffee.FamilyId }.Order(), rows.Select(row => row["_id"].AsInt64));
        Assert.All(rows, row => {
            Assert.Equal(501L, row["migratedFrom"]["assortmentId"].AsInt64); Assert.Equal(71L, row["migratedFrom"]["showcaseDecisionId"].AsInt64);
            Assert.True(row["approvedProposalId"].IsBsonNull);
        });
        Assert.DoesNotContain(rows, row => row["_id"].AsInt64 == paper.FamilyId || row["_id"].AsInt64 == draft.FamilyId);
        // Everything a guest could see before still shows, and nothing more.
        var page = await data.BuyerService.QueryPublicAsync(0, 24, null);
        Assert.Equal(["Kopi bubuk", "Pensil"], page.Items.Select(item => item.Name).Order());
        Assert.Equal(0L, await data.CountAsync("catalogPublicSampleProposals"));
        // Applying again changes nothing.
        await migrations.ApplyAsync();
        Assert.Equal(2L, await data.CountAsync("catalogPublicSamples"));
    }

    /// <summary>The real API host on the fixture database: anonymous reads, and signed-in commands through a verified session.</summary>
    private sealed class Http : IDisposable
    {
        private readonly IdentityRuntimeTests.RuntimeKeys keys = new();
        private readonly T data;
        internal WebApplicationFactory<Program> Factory { get; }
        internal Http(T data)
        {
            this.data = data;
            var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
            Factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
                services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
                services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Sessions);
            }));
        }
        internal HttpClient Anonymous() => Factory.CreateClient(new() { BaseAddress = new(Origin), AllowAutoRedirect = false });
        internal async Task<HttpResponseMessage> PostAsync(long actorId, string path, object body, string key)
        {
            using var client = Factory.CreateClient(new() { BaseAddress = new(Origin), AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Issued[actorId].OpaqueToken);
            var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", Origin); request.Headers.Add("X-CSRF-Token", csrf);
            request.Headers.Add("X-Expected-Actor-Id", actorId.ToString(CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            return await client.SendAsync(request);
        }
        public void Dispose() { Factory.Dispose(); keys.Dispose(); }
    }
}
