using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Worker;

internal static class PdfDocumentOcrWorkerConfiguration
{
    internal static void RejectUnsupportedPayment(IConfiguration configuration) {
        if (configuration.GetSection("Evidence:PaymentPdfOcr").Exists())
            throw new InvalidOperationException("Payment PDF processing requires a separate supplementary evidence owner; primary payment evidence must remain an eligible image");
    }
    internal static PdfDocumentOcrRuntimeOptions? ReadSupplierInvoice(IConfiguration configuration) => Read(configuration, "Evidence:SupplierDocumentPdfOcr");
    internal static PdfDocumentOcrRuntimeOptions? ReadPaymentSupplement(IConfiguration configuration) => Read(configuration, "Evidence:PaymentSupplementPdfOcr");
    private static PdfDocumentOcrRuntimeOptions? Read(IConfiguration configuration, string path) {
        var section = configuration.GetSection(path); if (!section.Exists()) return null;
        Exact(section, ["Root", "StoreId", "TrustedOperatorSids", "ConsumerId", "HostAcceptanceReference", "ModelSelectionReference", "RasterAcceptanceReference", "Raster", "Engine"]);
        Exact(section.GetSection("Raster"), ["PythonExecutable", "HelperPath", "PackageRoot", "PillowRoot", "WheelPath", "PillowVersion", "Files",
            "MaximumPages", "MaximumPagePixels", "MaximumTotalPixels", "MaximumOutputBytes", "MaximumMemoryBytes", "Timeout"]);
        Exact(section.GetSection("Engine"), ["PythonExecutable", "HelperPath", "PackageRoot", "PillowRoot", "ModelRoot", "ModelVersion", "Files", "Limits", "MaximumMemoryBytes", "Timeout"]);
        Exact(section.GetSection("Engine:Limits"), ["MaximumBytes", "MaximumWidth", "MaximumHeight", "MaximumPixels"]);
        Array(section.GetSection("TrustedOperatorSids"), null);
        Array(section.GetSection("Raster:Files"), ["Path", "Sha256"]);
        Array(section.GetSection("Engine:Files"), ["Path", "Sha256"]);
        return section.Get<PdfDocumentOcrRuntimeOptions>() ?? throw Invalid();
    }
    private static void Exact(IConfigurationSection section, string[] keys) {
        var children = section.GetChildren().ToArray();
        if (children.Length != keys.Length || keys.Any(key => !children.Any(child => string.Equals(child.Key, key, StringComparison.OrdinalIgnoreCase)))) throw Invalid();
    }
    private static void Array(IConfigurationSection section, string[]? fields) {
        var children = section.GetChildren().ToArray(); if (children.Length == 0) throw Invalid();
        for (var index = 0; index < children.Length; index++) {
            var key = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!children.Any(child => child.Key == key)) throw Invalid();
            var item = section.GetSection(key);
            if (fields is not null) Exact(item, fields);
            else if (item.GetChildren().Any()) throw Invalid();
        }
    }
    private static InvalidOperationException Invalid() => new("Explicit whole-PDF OCR worker configuration is incomplete or contains unsupported keys");
}
