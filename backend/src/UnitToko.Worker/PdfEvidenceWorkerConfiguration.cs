using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Worker;

internal static class PdfEvidenceWorkerConfiguration
{
    internal static PdfEvidenceValidationRuntimeOptions? Read(IConfiguration configuration) {
        var section = configuration.GetSection("Evidence:PdfValidation"); if (!section.Exists()) return null;
        Exact(section, ["Root", "StoreId", "TrustedOperatorSids", "ConsumerId", "HostAcceptanceReference", "ClamdConfigurationAcceptanceReference", "Raster", "Scanner"]);
        Exact(section.GetSection("Raster"), ["PythonExecutable", "HelperPath", "PackageRoot", "PillowRoot", "WheelPath", "PillowVersion", "Files",
            "MaximumPages", "MaximumPagePixels", "MaximumTotalPixels", "MaximumOutputBytes", "MaximumMemoryBytes", "Timeout"]);
        Exact(section.GetSection("Scanner"), ["Port", "EngineVersion", "MinimumSignatureVersion", "MaximumSignatureAge", "MaximumBytes", "Timeout"]);
        Array(section.GetSection("TrustedOperatorSids"), null);
        Array(section.GetSection("Raster:Files"), ["Path", "Sha256"]);
        return section.Get<PdfEvidenceValidationRuntimeOptions>() ?? throw Invalid();
    }
    private static void Exact(IConfigurationSection section, string[] keys) {
        var children = section.GetChildren().ToArray();
        if (children.Length != keys.Length || keys.Any(key => !children.Any(child => string.Equals(child.Key, key, StringComparison.OrdinalIgnoreCase)))) throw Invalid();
    }
    private static void Array(IConfigurationSection section, string[]? fields) {
        var children = section.GetChildren().ToArray(); if (children.Length == 0) throw Invalid();
        for (var index = 0; index < children.Length; index++) {
            if (!children.Any(child => child.Key == index.ToString(System.Globalization.CultureInfo.InvariantCulture))) throw Invalid();
            if (fields is not null) Exact(section.GetSection(index.ToString(System.Globalization.CultureInfo.InvariantCulture)), fields);
            else if (section.GetSection(index.ToString(System.Globalization.CultureInfo.InvariantCulture)).GetChildren().Any()) throw Invalid();
        }
    }
    private static InvalidOperationException Invalid() => new("Explicit PDF worker configuration is incomplete or contains unsupported keys");
}
