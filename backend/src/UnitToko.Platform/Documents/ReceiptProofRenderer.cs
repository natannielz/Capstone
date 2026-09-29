using System.Globalization;
using System.Net;
using System.Text;
using MongoDB.Bson;

namespace UnitToko.Platform.Documents;

// Frozen v1. All displayed facts are accepted source snapshots; downloads return stored bytes.
internal static class ReceiptProofRenderer
{
    internal static string Render(BsonDocument source, long documentId, DateTimeOffset issuedAt) {
        static string E(BsonValue value) => WebUtility.HtmlEncode(value.IsBsonNull ? "" : value.IsString ? value.AsString : value.ToString() ?? "");
        static string Time(DateTimeOffset value) => value.ToOffset(TimeSpan.FromHours(7)).ToString("dd MMMM yyyy HH:mm 'WIB'", CultureInfo.GetCultureInfo("id-ID"));
        var receipt = source["receipt"]; var number = string.Join("/", new[] { "numberEntity", "numberChannel", "numberYear", "number" }.Select(key => E(source[key])));
        var html = new StringBuilder("<!doctype html><html lang=\"id\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><title>Bukti Penerimaan ");
        html.Append(number).Append("</title><style>body{font:15px/1.6 system-ui,sans-serif;color:#162b22;max-width:850px;margin:32px auto;padding:0 20px;overflow-wrap:anywhere}h1{font-size:28px}h2{font-size:19px}h3{font-size:16px}.box{border:1px solid #cbd8cf;border-radius:8px;padding:16px;margin:18px 0;break-inside:avoid}.address{white-space:pre-wrap}dl{display:grid;grid-template-columns:minmax(90px,1fr) minmax(0,2fr);gap:8px}dd{margin:0}footer{font-size:12px;border-top:1px solid #cbd8cf;margin-top:24px;padding-top:12px}@media(max-width:540px){body{margin:16px auto;padding:0 12px}.box{padding:12px}}@media print{body{margin:0;padding:0;font-size:11pt}}</style></head><body><header><p>Unit Toko · Dokumen penerimaan</p><h1>Bukti Penerimaan</h1><p>Surat Jalan ").Append(number).Append(" · Versi bukti 1</p></header>");
        html.Append("<section class=\"box\"><h2>Kiriman</h2><p>Penjual: <strong>").Append(E(source["seller"]["name"])).Append("</strong></p><p class=\"address\">").Append(E(source["seller"]["address"]))
            .Append("</p><p>Pembeli: <strong>").Append(E(source["buyer"]["name"])).Append("</strong></p><p class=\"address\">").Append(E(source["buyer"]["address"]))
            .Append("</p><p class=\"address\">Tujuan: ").Append(E(source["delivery"]["address"])).Append("<br>").Append(E(source["delivery"]["city"])).Append(' ').Append(E(source["delivery"]["postalCode"]))
            .Append("</p><p>Kurir: ").Append(E(source["courierName"])).Append("</p></section><section class=\"box\"><h2>Pernyataan penerima</h2><p>Penerima berwenang: <strong>").Append(E(receipt["recipientName"]))
            .Append("</strong></p><p>Dicatat pada ").Append(Time(DateTimeOffset.FromUnixTimeMilliseconds(receipt["recordedAt"].AsBsonDateTime.MillisecondsSinceEpoch)))
            .Append("</p><blockquote>").Append(E(receipt["consentText"])).Append("</blockquote><p>Konfirmasi menggunakan akun penerima dan QR kiriman yang terikat pada versi Surat Jalan. Bukti ini mencatat persetujuan elektronik; tidak menyatakan tanda tangan elektronik tersertifikasi.</p></section><h2>Jumlah yang dikonfirmasi</h2>");
        foreach (var line in source["lines"].AsBsonArray) {
            var accepted = receipt["lines"].AsBsonArray.Single(item => item["skuId"] == line["skuId"]); var definition = line["definition"];
            html.Append("<section class=\"box\"><h3>").Append(E(line["productName"])).Append(" · ").Append(E(line["skuCode"])).Append("</h3><p>").Append(E(definition["packageLabel"])).Append(" · isi ").Append(E(definition["baseUnitsPerPackage"])).Append(' ').Append(E(definition["baseUnitCode"]))
                .Append("</p><p>Diberangkatkan: ").Append(E(line["packages"])).Append(" kemasan (").Append(E(line["baseUnits"])).Append(' ').Append(E(definition["baseUnitCode"])).Append(")</p><dl>");
            foreach (var (field, label) in new[] { ("accepted", "Diterima"), ("rejected", "Ditolak"), ("missing", "Belum diterima") }) {
                html.Append("<dt>").Append(label).Append("</dt><dd>").Append(E(accepted[field]["packages"])).Append(" kemasan + ").Append(E(accepted[field]["looseUnits"])).Append(" satuan lepas = ")
                    .Append(E(accepted[field + "BaseUnits"])).Append(' ').Append(E(definition["baseUnitCode"])).Append("</dd>");
            }
            html.Append("</dl>"); if (!accepted["exceptionReason"].IsBsonNull) html.Append("<p>Catatan selisih: ").Append(E(accepted["exceptionReason"])).Append("</p>"); html.Append("</section>");
        }
        html.Append("<p>").Append(receipt["hasException"].AsBoolean ? "Ada penolakan atau kekurangan. Penyelesaian selisih dicatat melalui tindak lanjut terpisah." : "Seluruh jumlah kiriman diterima.")
            .Append("</p><footer><p>Kiriman ").Append(E(source["shipmentId"])).Append(" · Penerimaan ").Append(E(receipt["id"])).Append(" · Bukti ").Append(documentId.ToString(CultureInfo.InvariantCulture))
            .Append("</p><p>Surat Jalan dokumen ").Append(E(receipt["documentId"])).Append(" versi ").Append(E(receipt["documentVersion"])).Append(" · SHA-256 ").Append(E(receipt["documentHash"]))
            .Append("</p><p>Hash rekaman penerimaan: ").Append(E(receipt["recordHash"])).Append("</p><p>Diterbitkan ").Append(Time(issuedAt))
            .Append(". Salinan tetap dari penerimaan tercatat. Simpan hanya sesuai kewenangan Anda.</p></footer></body></html>");
        return html.ToString();
    }
}
