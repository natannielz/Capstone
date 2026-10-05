using System.Diagnostics;

namespace UnitToko.Platform.Procurement;

// Optional timing only. Callers use fixed stage names and record no identities,
// arguments, private fields or proof data. Without a listener no activity exists.
internal static class SupplierDocumentMatchDiagnostics
{
    internal const string SourceName = "UnitToko.SupplierDocumentMatching";
    private static readonly ActivitySource Source = new(SourceName);
    internal static Activity? Begin(string stage) => Source.HasListeners() ? Source.StartActivity(stage, ActivityKind.Internal) : null;
}
