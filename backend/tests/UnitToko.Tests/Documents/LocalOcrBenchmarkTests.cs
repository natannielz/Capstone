using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Tests.Documents;

[SupportedOSPlatform("windows")]
public sealed class LocalOcrBenchmarkTests
{
    [LocalOcrFact]
    public async Task Record_real_candidate_metrics_on_identified_synthetic_fixtures_without_claiming_model_selection() {
        var directory = Environment.GetEnvironmentVariable("UNITTOKO_OCR_FIXTURES")!;
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json")));
        Assert.True(manifest.RootElement.GetProperty("synthetic").GetBoolean()); Assert.False(manifest.RootElement.GetProperty("heldOut").GetBoolean());
        var options = LocalOcrTests.Options(); var engine = new WindowsLocalOcrEngine(options); var observations = new List<object>();
        var samples = new List<double>(); var peaks = new List<long>(); int exact = 0, expectedKnown = 0, unknown = 0, wrong = 0, falseKnown = 0, failed = 0, sequence = 0;
        foreach (var sample in manifest.RootElement.GetProperty("cases").EnumerateArray()) {
            var file = sample.GetProperty("image").GetString()!; Assert.Equal(Path.GetFileName(file), file);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, file));
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); Assert.Equal(sample.GetProperty("sha256").GetString(), hash);
            for (var repetition = 0; repetition < 2; repetition++) {
                using var input = new MemoryStream(bytes); var result = await engine.ExtractAsync(sample.GetProperty("id").GetString()!, new(1, 1, hash), ExtractionPurpose.Payment, input, default);
                var measurement = engine.LastMeasurement; Assert.NotNull(measurement); Assert.True(measurement.PeakJobMemoryBytes > 0);
                samples.Add(measurement.Elapsed.TotalMilliseconds); peaks.Add(measurement.PeakJobMemoryBytes.Value);
                var matches = new List<object>();
                if (result.Status != "succeeded") failed++;
                foreach (var field in sample.GetProperty("expected").EnumerateObject()) {
                    var actual = result.Fields.SingleOrDefault(f => f.Field == field.Name)?.Value; var expected = field.Value.GetString();
                    if (expected is not null) { expectedKnown++; if (actual == expected) exact++; else if (actual is null) unknown++; else wrong++; }
                    else if (actual is not null) falseKnown++;
                    matches.Add(new { field = field.Name, expectedKnown = expected is not null, actualKnown = actual is not null, exact = actual == expected });
                }
                observations.Add(new { fixture = sample.GetProperty("id").GetString(), sha256 = hash, repetition,
                    processSample = sequence++ == 0 ? "first_process" : "subsequent_process_fresh_model", result.Status, result.FailureCode,
                    elapsedMs = measurement.Elapsed.TotalMilliseconds, peakJobCommittedBytes = measurement.PeakJobMemoryBytes,
                    preprocessing = engine.LastPreprocessing, matches });
            }
        }
        Assert.Equal(manifest.RootElement.GetProperty("cases").GetArrayLength() * 2, observations.Count);
        var report = new { schemaVersion = 1, synthetic = true, heldOut = false, productionAccepted = false, modelSelected = false,
            engineStandard = WindowsLocalOcrEngine.Engine, corpus = "generated-identifiable-smoke-v2",
            provenance = engine.Provenance, environment = new { os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), logicalProcessors = Environment.ProcessorCount },
            semantics = new { timeIncludes = "pin verification + fresh Python process + model initialization + recognition + bounded output",
                warm = "subsequent process with potential OS cache; not persistent model inference", memory = "Windows job peak committed memory; not RSS" },
            metrics = new { expectedKnown, exact, unknown, wrong, falseKnown, failed, p50Ms = samples.Order().ElementAt(samples.Count / 2),
                p95Ms = samples.Order().ElementAt((int)Math.Ceiling(samples.Count * .95) - 1), peakJobCommittedBytes = peaks.Max() },
            reviewerEffort = (object?)null, approvedThresholds = (object?)null, observations };
        await File.WriteAllTextAsync(Path.Combine(directory, "tesseract-candidate-results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(Path.Combine(directory, "tesseract-candidate-pins.json"), JsonSerializer.Serialize(new {
            syntheticTestPins = true, productionApproved = false, engine.Provenance, options.Files
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(0, failed);
    }
}
