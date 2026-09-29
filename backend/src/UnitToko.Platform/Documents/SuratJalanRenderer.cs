using System.Globalization;
using System.Net;
using System.Text;
using MongoDB.Bson;

namespace UnitToko.Platform.Documents;

// Version 1 is frozen after publication. Read returns stored bytes, never a current template.
internal static class SuratJalanRenderer
{
    internal static string Render(BsonDocument source, long documentId, DateTimeOffset issuedAt) {
        static string E(BsonValue value) => WebUtility.HtmlEncode(value.IsBsonNull ? "" : value.IsString ? value.AsString : value.ToString() ?? "");
        static string Time(DateTimeOffset value) => value.ToOffset(TimeSpan.FromHours(7)).ToString("dd MMMM yyyy HH:mm 'WIB'", CultureInfo.GetCultureInfo("id-ID"));
        var number = string.Join("/", new[] { "numberEntity", "numberChannel", "numberYear", "number" }.Select(key => E(source[key])));
        var html = new StringBuilder("<!doctype html><html lang=\"id\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><title>Surat Jalan ");
        html.Append(number).Append("</title><style>body{font:15px/1.55 system-ui,sans-serif;color:#162b22;margin:32px auto;padding:0 20px;max-width:900px}h1{font-size:28px;margin:0}h2{font-size:18px;margin-bottom:4px}.muted{color:#465b50}.box{padding:16px;border:1px solid #cbd8cf;border-radius:8px;margin:20px 0}.address{white-space:pre-wrap;overflow-wrap:anywhere}table{border-collapse:collapse;width:100%;font-size:14px}th,td{border-bottom:1px solid #cbd8cf;padding:10px 6px;text-align:left;vertical-align:top;overflow-wrap:anywhere}th{background:#edf4ef}.qty{text-align:right}footer{margin-top:28px;font-size:12px}tr{break-inside:avoid}@media print{body{margin:0;padding:0;font-size:11pt}.box{break-inside:avoid}thead{display:table-header-group}}@media(max-width:540px){body{margin:18px auto;padding:0 12px}th,td{padding:8px 3px;font-size:12px}}</style></head><body><header><p class=\"muted\">Unit Toko · Dokumen pengiriman</p><h1>Surat Jalan</h1><p>Nomor ").Append(number)
            .Append(" · Versi 1</p></header><section class=\"box\"><h2>Penjual</h2><strong>").Append(E(source["seller"]["name"])).Append("</strong><p class=\"address\">").Append(E(source["seller"]["address"]))
            .Append("</p><h2>Pembeli</h2><strong>").Append(E(source["buyer"]["name"])).Append("</strong><p class=\"address\">").Append(E(source["buyer"]["address"])).Append("</p></section>");
        var delivery = source["delivery"];
        html.Append("<section class=\"box\"><h2>Tujuan pengiriman</h2><p>").Append(E(delivery["recipientName"])).Append(" · ").Append(E(delivery["phone"]))
            .Append("</p><p class=\"address\">").Append(E(delivery["address"])).Append("<br>").Append(E(delivery["city"])).Append(' ').Append(E(delivery["postalCode"])).Append("</p>");
        if (!delivery["instructions"].IsBsonNull) html.Append("<p>Petunjuk: ").Append(E(delivery["instructions"])).Append("</p>");
        html.Append("<p>Kurir: ").Append(E(source["courierName"])).Append("</p><p>Diberangkatkan ")
            .Append(Time(DateTimeOffset.FromUnixTimeMilliseconds(source["dispatchedAt"].AsBsonDateTime.MillisecondsSinceEpoch)))
            .Append("</p></section><h2>Barang yang diberangkatkan</h2><table><thead><tr><th scope=\"col\">Barang / SKU</th><th scope=\"col\">Kemasan dan isi</th><th scope=\"col\" class=\"qty\">Jumlah kemasan</th><th scope=\"col\" class=\"qty\">Total satuan dasar</th></tr></thead><tbody>");
        foreach (var line in source["lines"].AsBsonArray) {
            var definition = line["definition"];
            html.Append("<tr><td><strong>").Append(E(line["productName"])).Append("</strong><br>").Append(E(line["skuCode"]))
                .Append("</td><td>").Append(E(definition["packageLabel"])).Append(" · isi ").Append(E(definition["baseUnitsPerPackage"])).Append(' ').Append(E(definition["baseUnitCode"]))
                .Append("</td><td class=\"qty\">").Append(E(line["packages"])).Append("</td><td class=\"qty\">").Append(E(line["baseUnits"])).Append(' ').Append(E(definition["baseUnitCode"])).Append("</td></tr>");
        }
        html.Append("</tbody></table><p>Dokumen ini mencatat barang yang diberangkatkan. Konfirmasi penerimaan dan selisih dicatat secara terpisah oleh penerima yang berwenang.</p><footer><p>Pesanan ")
            .Append(E(source["orderId"])).Append(" · Subpesanan ").Append(E(source["suborderId"])).Append(" · Kiriman ").Append(E(source["shipmentId"]))
            .Append(" · Dokumen ").Append(documentId.ToString(CultureInfo.InvariantCulture)).Append("</p><p>Diterbitkan ").Append(Time(issuedAt))
            .Append(". Salinan tetap; perubahan data berikutnya tidak mengubah dokumen ini. Simpan hanya sesuai kewenangan Anda.</p></footer></body></html>");
        return html.ToString();
    }
}
