using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;

namespace UnitToko.Platform.Documents;

// Only the Billing owner supplies a verified immutable invoice. No remote images, scripts,
// current master data or browser-calculated amounts are included in the stored document.
internal static class InvoiceRenderer
{
    internal static string Hash(string html) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(html)));
    internal static string Render(BsonDocument row) {
        static string E(BsonValue value) => WebUtility.HtmlEncode(value.IsString ? value.AsString : value.ToString()) ?? "";
        static string M(BsonValue value) => Decimal128.ToDecimal(value.AsDecimal128).ToString("N2", CultureInfo.GetCultureInfo("id-ID"));
        var source = row["sources"][0]["snapshot"]; var buyer = source["commercial"]["terms"]["buyer"]; var seller = source["shipment"]["seller"];
        var text = new StringBuilder("<!doctype html><html lang=\"id\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Invoice Unit Toko</title><style>body{font:15px/1.5 system-ui,sans-serif;margin:32px;color:#172c29}h1{margin-bottom:4px}table{width:100%;border-collapse:collapse;margin:24px 0}th,td{padding:10px;border-bottom:1px solid #cad4d1;text-align:left;vertical-align:top}.money{text-align:right}small{display:block;color:#435a53}@media print{body{margin:0}tr{break-inside:avoid}}</style></head><body><h1>Invoice</h1>");
        text.Append("<p>Nomor ").Append(E(row["numberEntity"])).Append('/').Append(E(row["numberChannel"])).Append('/').Append(E(row["numberYear"])).Append('/').Append(E(row["number"])).Append("<br>Terbit ").Append(E(row["issueDate"])).Append(" · Jatuh tempo ").Append(E(row["dueDate"])).Append("</p>");
        text.Append("<p><strong>").Append(E(seller["name"])).Append("</strong><br>").Append(E(seller["address"])).Append("</p><p>Ditagihkan kepada<br><strong>").Append(E(buyer["name"])).Append("</strong><br>").Append(E(buyer["address"])).Append("</p>");
        text.Append("<table><thead><tr><th>Barang / kemasan pesanan</th><th>Diterima</th><th class=\"money\">Nilai bersih</th><th class=\"money\">Pajak</th><th class=\"money\">Biaya</th></tr></thead><tbody>");
        foreach (var line in row["lines"].AsBsonArray) {
            var definition = line["definition"];
            text.Append("<tr><td>").Append(E(line["productName"])).Append("<small>").Append(E(line["skuCode"])).Append(" · ").Append(E(definition["packageLabel"])).Append(" · ").Append(E(definition["baseUnitsPerPackage"])).Append(' ').Append(E(definition["baseUnitCode"])).Append(" per kemasan<br>Pesanan ").Append(E(line["orderId"])).Append(" · Bukti penerimaan ").Append(E(line["receiptId"])).Append("</small></td><td>").Append(E(line["acceptedBaseUnits"])).Append(' ').Append(E(definition["baseUnitCode"])).Append("</td><td class=\"money\">").Append(M(line["net"])).Append("</td><td class=\"money\">").Append(M(line["tax"])).Append("</td><td class=\"money\">").Append(M(line["charges"])).Append("</td></tr>");
        }
        text.Append("</tbody></table><p class=\"money\"><strong>Total IDR ").Append(M(row["totals"]["total"])).Append("</strong></p><p>").Append(E(row["terms"]["taxDocumentProcess"])).Append("</p><small>Invoice berdasarkan barang yang telah diterima. Status pembayaran diperiksa melalui akun yang berwenang. Dokumen pajak mengikuti proses yang tercantum di atas.</small></body></html>");
        return text.ToString();
    }
}
