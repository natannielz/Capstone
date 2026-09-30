using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using UnitToko.ImageDecoder;

// Private stdin only. This executable has no Mongo, storage, credential, file-output or network API.
// Its caller must create it inside the enforced job before it receives any untrusted input.
try {
    if (!OperatingSystem.IsWindows() || args.Length != 4) throw new InvalidDataException();
    ProcessConfinement.RequireBoundedJob();
    var maximumBytes = int.Parse(args[0], CultureInfo.InvariantCulture);
    var maximumWidth = int.Parse(args[1], CultureInfo.InvariantCulture);
    var maximumHeight = int.Parse(args[2], CultureInfo.InvariantCulture);
    var maximumPixels = long.Parse(args[3], CultureInfo.InvariantCulture);
    if (maximumBytes is <= 0 or > 10 * 1024 * 1024 || maximumWidth is <= 0 or > 32768 || maximumHeight is <= 0 or > 32768 || maximumPixels is <= 0 or > 25_000_000) throw new InvalidDataException();
    using var input = Console.OpenStandardInput(); using var content = new MemoryStream();
    var buffer = new byte[65536];
    while (true) {
        var read = input.Read(buffer, 0, Math.Min(buffer.Length, maximumBytes - (int)content.Length + 1));
        if (read == 0) break;
        if (content.Length + read > maximumBytes) throw new InvalidDataException();
        content.Write(buffer, 0, read);
    }
    var bytes = content.ToArray(); var mime = ImageEnvelope.Media(bytes);
    var size = StrictImageDecoder.Decode(bytes, mime, maximumWidth, maximumHeight, maximumPixels);
    Console.Write(JsonSerializer.Serialize(new { status = "decoded", mime, width = size.Width, height = size.Height, frames = 1, pages = 1,
        length = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() }));
    return 0;
} catch (InvalidDataException) {
    // Do not return parser exceptions, input bytes, paths or account data.
    Console.Write("{\"status\":\"invalid_media\"}"); return 2;
} catch {
    Console.Write("{\"status\":\"decoder_unavailable\"}"); return 3;
}
