using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using StorageRoot = UnitToko.Tests.Documents.PrivateEvidenceStorageTests.StorageRoot;

namespace UnitToko.Tests.Documents;

// Loopback scanner and operator approvals are synthetic only. Parser, pins, original store and Mongo are actual.
[SupportedOSPlatform("windows")]
internal sealed class PdfEvidenceAdmissionTestRuntime(LocalScannerTests.Server scanner, PdfEvidenceValidationRuntime runtime,
    IMongoDatabase database, MutableAuthorizationClock clock) : IAsyncDisposable
{
    internal const string Consumer = "synthetic-pdf-admission-worker";
    internal const string Host = "synthetic-pdf-host-not-production-acceptance", Clamd = "synthetic-clamd-config-not-production-acceptance";
    internal LocalScannerTests.Server Scanner { get; } = scanner;
    internal PdfEvidenceValidationRuntime Runtime { get; } = runtime;
    internal static async Task<PdfEvidenceAdmissionTestRuntime> CreateAsync(IMongoDatabase db, MongoUnitOfWork unit, MutableAuthorizationClock clock,
        string root, string store, bool approve = true, bool infected = false, bool badPin = false) {
        clock.UseSystemClock = true;
        var scanner = new LocalScannerTests.Server("ClamAV 1.5.1/30000/" + clock.UtcNow.AddHours(-1).UtcDateTime.ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture),
            infected ? "stream: Synthetic-PDF-Signature FOUND" : "stream: OK");
        try {
            var raster = WindowsPdfRasterTests.Options();
            var pins = raster.Files.Select(pin => new PdfEvidenceRuntimePin(pin.Path, badPin && pin.Path == raster.HelperPath ? new string('0', 64) : pin.Sha256)).ToArray();
            var options = new PdfEvidenceValidationRuntimeOptions(root, store, StorageRoot.TrustedOperators, Consumer, Host, Clamd,
                new(raster.PythonExecutable, raster.HelperPath, raster.PackageRoot, raster.PillowRoot, raster.WheelPath, raster.PillowVersion, pins,
                    raster.Limits.MaximumPages, raster.Limits.MaximumPagePixels, raster.Limits.MaximumTotalPixels, raster.Limits.MaximumOutputBytes, raster.MaximumMemoryBytes, raster.Timeout),
                new(scanner.Port, "1.5.1", 30000, TimeSpan.FromDays(2), 1024 * 1024, TimeSpan.FromSeconds(3)));
            var runtime = PdfEvidenceValidationRuntime.Create(db, unit, new(db), clock, options);
            var data = new PdfEvidenceAdmissionTestRuntime(scanner, runtime, db, clock);
            if (approve) await data.ApproveAsync(store); return data;
        } catch { await scanner.DisposeAsync(); throw; }
    }
    internal async Task ApproveAsync(string store) => await database.GetCollection<BsonDocument>(PdfEvidenceValidationRecords.Policies).InsertOneAsync(new BsonDocument {
        { "_id", (await new IdAllocator(database).NextAsync(PdfEvidenceValidationRecords.Policies)).Value }, { "schemaVersion", 1 }, { "consumerId", Consumer }, { "storeId", store }, { "pipelineId", Runtime.PipelineId },
        { "active", true }, { "revision", 1L }, { "fence", 0L }, { "approvedAt", clock.UtcNow.AddMinutes(-1).UtcDateTime },
        { "expiresAt", clock.UtcNow.AddDays(1).UtcDateTime }, { "hostAcceptanceRef", Host }, { "clamdConfigurationAcceptanceRef", Clamd } });
    internal static byte[] Pdf(int pages = 2, bool oversizePage = false) {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count " + pages + " /Kids [" + string.Join(" ", Enumerable.Range(0, pages).Select(index => (3 + 2 * index) + " 0 R")) + "] >>" };
        for (var index = 0; index < pages; index++) {
            var size = oversizePage && index == pages - 1 ? "10000 10000" : "72 96";
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + size + "] /Rotate " + (index % 2 == 0 ? 0 : 90) + " /Resources << >> /Contents " + (4 + 2 * index) + " 0 R >>");
            const string contents = "q 0 g 8 10 18 22 re f Q\n";
            objects.Add("<< /Length " + contents.Length + " >>\nstream\n" + contents + "\nendstream");
        }
        var text = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Count; index++) { offsets.Add(text.Length); text.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        var xref = text.Length; text.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) text.Append(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        text.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
    internal static byte[] EncryptedPdf() {
        // Actual legacy Standard/R2 security fixture, empty user password. Production refuses all encryption.
        byte[] padding = Convert.FromHexString("28BF4E5E4E758A4164004E56FFFA01082E2E00B6D0683E802F0CA9FE6453697A");
        byte[] identifier = Convert.FromHexString("0102030405060708090A0B0C0D0E0F10");
        byte[] ownerPassword = new byte[32]; var ownerText = Encoding.ASCII.GetBytes("synthetic-owner"); ownerText.CopyTo(ownerPassword, 0); padding.AsSpan(0, 32 - ownerText.Length).CopyTo(ownerPassword.AsSpan(ownerText.Length));
        var owner = Rc4(MD5.HashData(ownerPassword)[..5], padding);
        var keyInput = padding.Concat(owner).Concat(new byte[] { 0xFC, 0xFF, 0xFF, 0xFF }).Concat(identifier).ToArray();
        var key = MD5.HashData(keyInput)[..5]; var user = Rc4(key, padding);
        var objectKey = MD5.HashData(key.Concat(new byte[] { 4, 0, 0, 0, 0 }).ToArray())[..10];
        var contents = Rc4(objectKey, "q 0 g 8 10 18 22 re f Q\n"u8.ToArray());
        using var pdf = new MemoryStream(); var offsets = new List<long> { 0 };
        void Text(string value) => pdf.Write(Encoding.ASCII.GetBytes(value));
        void Object(int index, string value) { offsets.Add(pdf.Position); Text(index + " 0 obj\n" + value + "\nendobj\n"); }
        Text("%PDF-1.3\n"); Object(1, "<< /Type /Catalog /Pages 2 0 R >>"); Object(2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>");
        Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 96] /Resources << >> /Contents 4 0 R >>");
        offsets.Add(pdf.Position); Text("4 0 obj\n<< /Length " + contents.Length + " >>\nstream\n"); pdf.Write(contents); Text("\nendstream\nendobj\n");
        Object(5, "<< /Filter /Standard /V 1 /R 2 /Length 40 /O <" + Convert.ToHexString(owner) + "> /U <" + Convert.ToHexString(user) + "> /P -4 >>");
        var xref = pdf.Position; Text("xref\n0 " + offsets.Count + "\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Text(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        var id = Convert.ToHexString(identifier); Text("trailer\n<< /Size " + offsets.Count + " /Root 1 0 R /Encrypt 5 0 R /ID [<" + id + "><" + id + ">] >>\nstartxref\n" + xref + "\n%%EOF\n");
        return pdf.ToArray();
        static byte[] Rc4(byte[] key, byte[] input) {
            var state = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray(); var j = 0;
            for (var i = 0; i < 256; i++) { j = (j + state[i] + key[i % key.Length]) & 255; (state[i], state[j]) = (state[j], state[i]); }
            var output = new byte[input.Length]; var x = 0; j = 0;
            for (var n = 0; n < input.Length; n++) { x = (x + 1) & 255; j = (j + state[x]) & 255; (state[x], state[j]) = (state[j], state[x]); output[n] = (byte)(input[n] ^ state[(state[x] + state[j]) & 255]); }
            return output;
        }
    }
    public ValueTask DisposeAsync() => Scanner.DisposeAsync();
}
