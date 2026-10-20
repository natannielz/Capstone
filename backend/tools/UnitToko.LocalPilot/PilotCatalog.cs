using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;

namespace UnitToko.LocalPilot;

/// <summary>Demo catalog with real variety for a cooperative shop serving bank branches. Every item is SYNTHETIC and
/// goes through the real authoring, publication and maker/checker price services; nothing is inserted directly.
/// Idempotency keys make a rerun after an interruption return the original results.</summary>
public static class PilotCatalog
{
    public const int Version = 1;

    internal sealed record Item(string Category, string Code, string Name, string Description, string PackageLabel, string PackageKind,
        string BaseUnit, int UnitsPerPackage, int Minimum, string Price, (string Key, string Value)[] Attributes);

    internal static readonly (string Code, string Name)[] Categories = [
        ("AIR-MINUMAN", "Air dan minuman"), ("PANTRY", "Pantry kopi dan teh"), ("SNACK-RAPAT", "Snack rapat"),
        ("ATK", "Alat tulis kantor"), ("KEBERSIHAN", "Kebersihan"), ("SEMBAKO", "Sembako"), ("MERCHANDISE", "Merchandise"),
    ];

    internal static readonly Item[] Items = [
        new("AIR-MINUMAN", "AIR-600-K24", "Air mineral 600 ml", "Air mineral dalam botol plastik untuk rapat dan pantry.", "Karton isi 24 botol", "sealed", "botol", 24, 1, "62000.00", [("Volume", "600 ml")]),
        new("AIR-MINUMAN", "AIR-330-K24", "Air mineral 330 ml", "Botol kecil untuk meja rapat dan tamu.", "Karton isi 24 botol", "sealed", "botol", 24, 1, "45000.00", [("Volume", "330 ml")]),
        new("AIR-MINUMAN", "AIR-1500-K12", "Air mineral 1,5 liter", "Botol besar untuk ruang kerja.", "Karton isi 12 botol", "sealed", "botol", 12, 1, "58000.00", [("Volume", "1,5 liter")]),
        new("AIR-MINUMAN", "GALON-19L", "Air galon 19 liter", "Isi ulang galon untuk dispenser kantor. Galon kosong ditukar saat pengantaran.", "Satuan galon", "sealed", "galon", 1, 2, "21000.00", [("Volume", "19 liter")]),
        new("AIR-MINUMAN", "TEH-KOTAK-K24", "Teh kemasan kotak 250 ml", "Teh manis siap minum dalam kotak.", "Karton isi 24 kotak", "sealed", "kotak", 24, 1, "84000.00", [("Volume", "250 ml")]),
        new("AIR-MINUMAN", "KOPI-KALENG-K24", "Kopi susu kaleng 240 ml", "Kopi susu siap minum untuk acara.", "Karton isi 24 kaleng", "sealed", "kaleng", 24, 1, "168000.00", [("Volume", "240 ml")]),
        new("PANTRY", "KOPI-SCT-D24", "Kopi bubuk sachet", "Kopi bubuk robusta dalam sachet sekali seduh.", "Dus isi 24 sachet", "sealed", "sachet", 24, 1, "54000.00", [("Berat", "20 g per sachet")]),
        new("PANTRY", "KOPI-MIX-D30", "Kopi instan tiga dalam satu", "Kopi, gula dan krimer dalam satu sachet.", "Dus isi 30 sachet", "sealed", "sachet", 30, 1, "48000.00", [("Berat", "20 g per sachet")]),
        new("PANTRY", "TEH-CELUP-K25", "Teh celup melati", "Teh celup melati untuk pantry kantor.", "Kotak isi 25 kantong", "sealed", "kantong", 25, 2, "18500.00", [("Isi", "25 kantong")]),
        new("PANTRY", "GULA-1KG", "Gula pasir 1 kg", "Gula pasir putih kemasan plastik.", "Bungkus 1 kg", "sealed", "bungkus", 1, 2, "17500.00", [("Berat", "1 kg")]),
        new("PANTRY", "GULA-STICK-D50", "Gula pasir stik", "Gula dalam stik kertas untuk rapat.", "Dus isi 50 stik", "sealed", "stik", 50, 1, "16000.00", [("Berat", "8 g per stik")]),
        new("PANTRY", "KRIMER-500G", "Krimer bubuk 500 g", "Krimer nabati untuk kopi dan teh.", "Bungkus 500 g", "sealed", "bungkus", 1, 1, "32000.00", [("Berat", "500 g")]),
        new("PANTRY", "GELAS-KRT-P50", "Gelas kertas 8 oz", "Gelas kertas sekali pakai untuk minuman panas.", "Pak isi 50 gelas", "sealed", "gelas", 50, 1, "27500.00", [("Ukuran", "8 oz")]),
        new("PANTRY", "PENGADUK-P100", "Pengaduk kayu", "Pengaduk kayu sekali pakai.", "Pak isi 100 batang", "sealed", "batang", 100, 1, "9500.00", [("Panjang", "14 cm")]),
        new("SNACK-RAPAT", "SNACK-BOX-01", "Snack box rapat", "Kotak kudapan berisi dua kue dan air mineral. Pesan minimal 10 kotak.", "Kotak", "sealed", "porsi", 1, 10, "27500.00", [("Isi", "2 kue dan air mineral")]),
        new("SNACK-RAPAT", "NASI-BOX-01", "Nasi kotak rapat", "Nasi, lauk dan buah untuk rapat siang. Pesan minimal 10 kotak.", "Kotak", "sealed", "porsi", 1, 10, "38000.00", [("Isi", "Nasi, lauk, sayur, buah")]),
        new("SNACK-RAPAT", "BISKUIT-KLG", "Biskuit kaleng 650 g", "Biskuit aneka rasa dalam kaleng untuk tamu.", "Kaleng 650 g", "sealed", "kaleng", 1, 1, "68000.00", [("Berat", "650 g")]),
        new("SNACK-RAPAT", "KACANG-D20", "Kacang kulit 70 g", "Kacang kulit sangrai dalam bungkus kecil.", "Dus isi 20 bungkus", "sealed", "bungkus", 20, 1, "96000.00", [("Berat", "70 g per bungkus")]),
        new("SNACK-RAPAT", "WAFER-D24", "Wafer cokelat", "Wafer cokelat bungkus satuan.", "Dus isi 24 bungkus", "sealed", "bungkus", 24, 1, "52000.00", [("Berat", "21 g per bungkus")]),
        new("SNACK-RAPAT", "PERMEN-TOPLES", "Permen mint toples", "Permen mint untuk meja layanan.", "Toples isi 150 butir", "sealed", "butir", 150, 1, "36000.00", [("Isi", "150 butir")]),
        new("ATK", "HVS-A4-70-R500", "Kertas HVS A4 70 gsm", "Kertas fotokopi A4 70 gsm.", "Rim isi 500 lembar", "sealed", "lembar", 500, 1, "52000.00", [("Ukuran", "A4"), ("Gramatur", "70 gsm")]),
        new("ATK", "HVS-A4-80-R500", "Kertas HVS A4 80 gsm", "Kertas A4 lebih tebal untuk dokumen resmi.", "Rim isi 500 lembar", "sealed", "lembar", 500, 1, "61000.00", [("Ukuran", "A4"), ("Gramatur", "80 gsm")]),
        new("ATK", "HVS-F4-70-R500", "Kertas HVS F4 70 gsm", "Kertas folio untuk formulir dan arsip.", "Rim isi 500 lembar", "sealed", "lembar", 500, 1, "56000.00", [("Ukuran", "F4"), ("Gramatur", "70 gsm")]),
        new("ATK", "PULPEN-HTM-D12", "Pulpen hitam", "Pulpen tinta hitam 0,5 mm.", "Dus isi 12 batang", "sealed", "batang", 12, 1, "30000.00", [("Warna", "Hitam"), ("Mata pena", "0,5 mm")]),
        new("ATK", "PULPEN-BIRU-D12", "Pulpen biru", "Pulpen tinta biru 0,5 mm.", "Dus isi 12 batang", "sealed", "batang", 12, 1, "30000.00", [("Warna", "Biru"), ("Mata pena", "0,5 mm")]),
        new("ATK", "PENSIL-2B-D12", "Pensil 2B", "Pensil kayu 2B untuk formulir.", "Dus isi 12 batang", "sealed", "batang", 12, 1, "24000.00", [("Kekerasan", "2B")]),
        new("ATK", "SPIDOL-WB-D12", "Spidol papan tulis", "Spidol papan tulis yang dapat dihapus.", "Dus isi 12 batang", "sealed", "batang", 12, 1, "84000.00", [("Warna", "Campuran")]),
        new("ATK", "STABILO-D10", "Penanda teks", "Penanda teks warna terang.", "Dus isi 10 batang", "sealed", "batang", 10, 1, "65000.00", [("Warna", "Kuning")]),
        new("ATK", "MAP-PLASTIK-P12", "Map plastik kancing", "Map plastik kancing ukuran folio.", "Pak isi 12 lembar", "sealed", "lembar", 12, 1, "36000.00", [("Ukuran", "Folio")]),
        new("ATK", "AMPLOP-PUTIH-D100", "Amplop putih", "Amplop putih ukuran surat kantor.", "Dus isi 100 lembar", "sealed", "lembar", 100, 1, "28000.00", [("Ukuran", "110 x 230 mm")]),
        new("ATK", "AMPLOP-COKLAT-P50", "Amplop cokelat folio", "Amplop cokelat untuk berkas.", "Pak isi 50 lembar", "sealed", "lembar", 50, 1, "45000.00", [("Ukuran", "Folio")]),
        new("ATK", "BINDER-CLIP-D12", "Klip kertas besar", "Klip penjepit kertas ukuran 155.", "Dus isi 12 buah", "sealed", "buah", 12, 1, "26000.00", [("Ukuran", "No. 155")]),
        new("ATK", "STAPLES-NO10", "Isi staples no. 10", "Isi staples untuk dokumen tipis.", "Kotak isi 1000 butir", "sealed", "butir", 1000, 2, "4500.00", [("Ukuran", "No. 10")]),
        new("ATK", "BUKU-TULIS-P10", "Buku tulis 58 lembar", "Buku tulis bergaris untuk catatan.", "Pak isi 10 buku", "sealed", "buku", 10, 1, "42000.00", [("Isi", "58 lembar")]),
        new("ATK", "ORDNER-BIRU", "Ordner folio", "Ordner punggung lebar untuk arsip.", "Satuan", "loose", "buah", 1, 2, "29500.00", [("Ukuran", "Folio")]),
        new("KEBERSIHAN", "TISU-WAJAH-K48", "Tisu wajah 250 lembar", "Tisu wajah untuk meja layanan dan ruang tamu.", "Karton isi 48 bungkus", "sealed", "bungkus", 48, 1, "336000.00", [("Isi", "250 lembar per bungkus")]),
        new("KEBERSIHAN", "TISU-GULUNG-P10", "Tisu gulung", "Tisu toilet gulung dua lapis.", "Pak isi 10 gulung", "sealed", "gulung", 10, 1, "39000.00", [("Lapis", "2")]),
        new("KEBERSIHAN", "SABUN-TANGAN-2L", "Sabun cuci tangan 2 L", "Sabun cair isi ulang untuk wastafel.", "Jeriken 2 liter", "sealed", "jeriken", 1, 1, "48000.00", [("Volume", "2 liter")]),
        new("KEBERSIHAN", "PEMBERSIH-LANTAI", "Pembersih lantai 800 ml", "Cairan pembersih lantai beraroma.", "Botol 800 ml", "sealed", "botol", 1, 2, "18500.00", [("Volume", "800 ml")]),
        new("KEBERSIHAN", "KANTONG-SAMPAH", "Kantong sampah besar", "Kantong sampah hitam ukuran 60 x 100 cm.", "Pak isi 20 lembar", "sealed", "lembar", 20, 1, "15000.00", [("Ukuran", "60 x 100 cm")]),
        new("KEBERSIHAN", "HAND-SANITIZER", "Penyanitasi tangan 500 ml", "Gel penyanitasi tangan dengan pompa.", "Botol 500 ml", "sealed", "botol", 1, 2, "35000.00", [("Volume", "500 ml")]),
        new("SEMBAKO", "BERAS-5KG", "Beras putih 5 kg", "Beras putih medium untuk kebutuhan dapur.", "Karung 5 kg", "sealed", "karung", 1, 1, "72000.00", [("Berat", "5 kg")]),
        new("SEMBAKO", "MINYAK-2L", "Minyak goreng 2 liter", "Minyak goreng sawit kemasan bantal.", "Bungkus 2 liter", "sealed", "bungkus", 1, 1, "36000.00", [("Volume", "2 liter")]),
        new("SEMBAKO", "TELUR-1KG", "Telur ayam 1 kg", "Telur ayam ras, sekitar 16 butir per kilogram.", "Kemasan 1 kg", "sealed", "kg", 1, 1, "29000.00", [("Berat", "1 kg")]),
        new("SEMBAKO", "MI-INSTAN-D40", "Mi instan goreng", "Mi instan goreng bungkus satuan.", "Dus isi 40 bungkus", "sealed", "bungkus", 40, 1, "128000.00", [("Berat", "85 g per bungkus")]),
        new("MERCHANDISE", "TUMBLER-500", "Tumbler 500 ml", "Tumbler baja tahan karat dengan logo Unit Toko.", "Satuan dalam kotak", "sealed", "buah", 1, 5, "85000.00", [("Volume", "500 ml")]),
        new("MERCHANDISE", "KAOS-M", "Kaos katun ukuran M", "Kaos katun dengan logo Unit Toko.", "Satuan", "sealed", "buah", 1, 5, "75000.00", [("Ukuran", "M")]),
        new("MERCHANDISE", "TAS-BELANJA", "Tas belanja kanvas", "Tas belanja kanvas yang dapat dipakai ulang.", "Satuan", "loose", "buah", 1, 10, "25000.00", [("Ukuran", "40 x 35 cm")]),
    ];

    /// <summary>Adds the expansion once per pilot database (marker field <c>catalogExpansion</c>).</summary>
    public static async Task ExpandAsync(IMongoDatabase database, MongoClient client, PilotSecrets secrets, IMongoCollection<BsonDocument> markers,
        string profile, CancellationToken ct)
    {
        var marker = await markers.Find(new BsonDocument("_id", profile)).SingleAsync(ct);
        if (marker.GetValue("catalogExpansion", 0).ToInt32() >= Version) return;
        var clock = new SystemClock(); var unit = new MongoUnitOfWork(client);
        var actor = new ActorContext(2001, PilotBoundary.Issuer, secrets.Accounts.Single(x => x.Id == 2001).Subject, 1, clock.UtcNow, FactorAssurance.MultiFactor);
        var reviewer = new ActorContext(2002, PilotBoundary.Issuer, secrets.Accounts.Single(x => x.Id == 2002).Subject, 1, clock.UtcNow, FactorAssurance.MultiFactor);
        var service = new CatalogAuthoringService(database, unit, new(database), clock);
        var prices = new CatalogPriceService(database, unit, new(database), clock);
        var owner = new GrantScope(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103);
        static AuthorizationCommandMetadata Meta(string key) => new("local-pilot.catalog-v1." + key, "SYNTHETIC demo catalog; no real product, stock or price", "local-pilot-catalog");
        var categories = new Dictionary<string, LongId>(StringComparer.Ordinal);
        foreach (var (code, name) in Categories)
            categories[code] = (await service.SaveCategoryAsync(actor, new(owner, null, 0, code, name), Meta("category-" + code), ct)).Id;
        var assortments = database.GetCollection<BsonDocument>("catalogAssortments");
        foreach (var item in Items)
        {
            var family = await service.CreateFamilyAsync(actor, new(owner, categories[item.Category], item.Name,
                item.Description + " Data contoh sintetis untuk demo.", []), Meta("family-" + item.Code), ct);
            var attributes = item.Attributes.ToImmutableSortedDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var sku = await service.CreateSkuAsync(actor, new(family.Id, family.Version, item.Code, "synthetic-demo-" + item.Code.ToLowerInvariant(),
                new DefinitionInput(item.PackageLabel, item.PackageKind, item.BaseUnit, item.UnitsPerPackage, attributes, [], item.Minimum, 1)), Meta("sku-" + item.Code), ct);
            var row = await database.GetCollection<BsonDocument>("skus").Find(new BsonDocument("_id", sku.Id.Value)).SingleAsync(ct);
            if (!row["catalogListings"].AsBsonArray.Any(listing => listing["assortmentId"] == 501L))
            {
                var assortment = await assortments.Find(new BsonDocument("_id", 501L)).SingleAsync(ct);
                await service.SetPublicationsAsync(actor, new(sku.Id, sku.Version, [new(501)], [new(new(501), assortment["version"].ToInt64())]),
                    Meta("publish-" + item.Code), ct);
            }
            if (profile == "full")
            {
                var preview = await prices.PreviewAsync(actor, sku.Id.Value, CatalogPriceAccess.Prepare, ct);
                if (preview.Current is null)
                {
                    var proposal = await prices.ProposeAsync(actor, sku.Id.Value, preview.Expected, new("IDR", UnitToko.Modules.Accounting.Money.Parse(item.Price)),
                        Meta("price-" + item.Code), ct);
                    await prices.DecideAsync(reviewer, proposal["proposalId"].AsInt64, 1, CatalogPriceDecision.Approve,
                        "SYNTHETIC independent price review for the demo catalog", Meta("price-approve-" + item.Code), ct);
                }
            }
        }
        await markers.UpdateOneAsync(new BsonDocument("_id", profile), Builders<BsonDocument>.Update.Set("catalogExpansion", Version), cancellationToken: ct);
    }
}
