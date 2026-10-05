using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Documents;

public sealed class PdfDocumentOcrWorkerConfigurationTests
{
    [Fact]
    public void Detailed_PDF_failures_map_only_to_established_bounded_queue_codes_and_are_not_guessed() {
        var allowed = new[] { "ocr_unavailable", "ocr_timeout", "ocr_invalid_output", "ocr_integrity_mismatch", "ocr_invalid_media", "ocr_original_unavailable" };
        foreach (var code in PdfDocumentExtractionSchemas.SafeFailure()["enum"].AsBsonArray.Select(value => value.AsString))
            Assert.Contains(PdfDocumentOcrFailures.QueueFailure(code), allowed);
        Assert.Equal("ocr_timeout", PdfDocumentOcrFailures.QueueFailure("pdf_document_deadline")); Assert.True(PdfDocumentOcrFailures.Retryable("pdf_document_deadline"));
        Assert.Equal("ocr_invalid_media", PdfDocumentOcrFailures.QueueFailure("pdf_raster_encrypted_pdf")); Assert.False(PdfDocumentOcrFailures.Retryable("pdf_document_page_limit"));
        Assert.Throws<PersistenceConflictException>(() => PdfDocumentOcrFailures.QueueFailure("adapter-private-path-error"));
    }
    [Fact]
    public void Worker_configuration_requires_explicit_supplier_section_and_rejects_source_guessing_or_unknown_fields() {
        var empty = new ConfigurationBuilder().Build(); Assert.Null(Read(empty, "RejectUnsupportedPayment")); Assert.Null(Read(empty, "ReadSupplierInvoice"));
        var fields = Fields("Evidence:SupplierDocumentPdfOcr"); var config = new ConfigurationBuilder().AddInMemoryCollection(fields).Build();
        var actual = Assert.IsType<PdfDocumentOcrRuntimeOptions>(Read(config, "ReadSupplierInvoice")); Assert.Equal("synthetic-supplier-pdf", actual.ConsumerId);
        Assert.Equal("synthetic-raster-approval", actual.RasterAcceptanceReference); Assert.Null(Read(config, "RejectUnsupportedPayment"));
        fields["Evidence:SupplierDocumentPdfOcr:SourceKind"] = "Payment";
        AssertInvalid(new ConfigurationBuilder().AddInMemoryCollection(fields).Build(), "ReadSupplierInvoice");
    }

    [Fact]
    public void Payment_pdf_configuration_and_runtime_are_refused_before_any_private_io_or_database_access() {
        var config = new ConfigurationBuilder().AddInMemoryCollection(Fields("Evidence:PaymentPdfOcr")).Build();
        var error = Assert.Throws<TargetInvocationException>(() => Read(config, "RejectUnsupportedPayment"));
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal("Payment PDF processing requires a separate supplementary evidence owner; primary payment evidence must remain an eligible image", error.InnerException!.Message);
        Assert.Throws<NotSupportedException>(() => PdfDocumentOcrRuntime.CreatePayment(null!, null!, null!, null!, null!));
    }

    [Fact]
    public void Supplement_configuration_is_explicit_and_cannot_activate_primary_payment_or_adopt_supplier_configuration() {
        var empty = new ConfigurationBuilder().Build(); Assert.Null(Read(empty, "ReadPaymentSupplement"));
        var fields = Fields("Evidence:PaymentSupplementPdfOcr");
        fields["Evidence:PaymentSupplementPdfOcr:ConsumerId"] = "synthetic-payment-supplement-pdf";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(fields).Build();
        var actual = Assert.IsType<PdfDocumentOcrRuntimeOptions>(Read(configuration, "ReadPaymentSupplement"));
        Assert.Equal("synthetic-payment-supplement-pdf", actual.ConsumerId);
        Assert.Null(Read(configuration, "ReadSupplierInvoice")); Assert.Null(Read(configuration, "RejectUnsupportedPayment"));
        fields["Evidence:PaymentSupplementPdfOcr:SourceKind"] = "Payment";
        AssertInvalid(new ConfigurationBuilder().AddInMemoryCollection(fields).Build(), "ReadPaymentSupplement");
        Assert.Throws<NotSupportedException>(() => PdfDocumentOcrRuntime.CreatePayment(null!, null!, null!, null!, null!));
    }

    [Fact]
    public void Nested_missing_approval_limits_unknown_engine_and_noncontiguous_pin_lists_fail_closed() {
        foreach (var (remove, add) in new[] {
            ("Evidence:SupplierDocumentPdfOcr:ModelSelectionReference", (string?)null),
            ("Evidence:SupplierDocumentPdfOcr:Engine:Limits:MaximumPixels", (string?)null),
            ("", "Evidence:SupplierDocumentPdfOcr:Engine:FallbackProvider"),
            ("Evidence:SupplierDocumentPdfOcr:Raster:Files:0:Path", "Evidence:SupplierDocumentPdfOcr:Raster:Files:7:Path") }) {
            var fields = Fields("Evidence:SupplierDocumentPdfOcr");
            if (remove.Length > 0) fields.Remove(remove);
            if (add is not null) fields[add] = "unsupported";
            AssertInvalid(new ConfigurationBuilder().AddInMemoryCollection(fields).Build(), "ReadSupplierInvoice");
        }
    }

    private static Dictionary<string, string?> Fields(string section) {
        // The reader test is configuration-only; these paths and approvals authorize no native runtime or production use.
        var hash = new string('a', 64);
        var options = new PdfDocumentOcrRuntimeOptions("synthetic-root", "synthetic-store", ["S-1-5-18"], "synthetic-supplier-pdf",
            "synthetic-host-approval", "synthetic-model-approval", "synthetic-raster-approval",
            new("synthetic-python", "synthetic-raster-helper", "synthetic-package", "synthetic-pillow", "synthetic-wheel", "synthetic-version",
                [new("synthetic-raster-file", hash)], 10, 4_000_000, 25_000_000, 12 * 1024 * 1024, 128L * 1024 * 1024, TimeSpan.FromSeconds(30)),
            new("synthetic-python", "synthetic-ocr-helper", "synthetic-package", "synthetic-pillow", "synthetic-model", "synthetic-version",
                [new("synthetic-ocr-file", hash)], new EvidenceLimits(10 * 1024 * 1024, 32768, 32768, 25_000_000), 128L * 1024 * 1024, TimeSpan.FromSeconds(30)));
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { [section.Split(':')[0]] = new Dictionary<string, object> { [section.Split(':')[1]] = options } }));
        return new ConfigurationBuilder().AddJsonStream(stream).Build().AsEnumerable().Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value);
    }
    private static object? Read(IConfiguration configuration, string method) {
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "MigrationWorker", "UnitToko.Worker.dll"));
        var type = assembly.GetType("UnitToko.Worker.PdfDocumentOcrWorkerConfiguration", throwOnError: true)!;
        return type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [configuration]);
    }
    private static void AssertInvalid(IConfiguration configuration, string method) {
        var error = Assert.Throws<TargetInvocationException>(() => Read(configuration, method));
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal("Explicit whole-PDF OCR worker configuration is incomplete or contains unsupported keys", error.InnerException!.Message);
    }
}
