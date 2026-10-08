using System.Collections.Immutable;
using System.Globalization;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Operations;

public sealed record AnomalyReviewPolicy(string Version, string ApprovalReference, long? MaximumPackages,
    decimal? MaximumUnitPrice, string? Currency, decimal? MaximumRefund);
public sealed record AnomalyOrderLine(string Reference, long Packages, decimal UnitPrice, decimal Subtotal, string Currency);
public sealed record AnomalyRefund(decimal Amount, decimal ReservedAmount, string Currency);
public sealed record AnomalySignal(string Category, string Code, string State, string Reference,
    string? Observed, string? Expected, string? PolicyVersion, string Explanation);

/// <summary>Advisory evidence only. Rules have no order, stock, financial or permission command dependency.</summary>
public static class OperationalAnomalyRules
{
    public const string Version = "operational-anomaly-v1";
    public static void Validate(AnomalyReviewPolicy policy)
    {
        if (!Text(policy.Version, 120) || !Text(policy.ApprovalReference, 256) ||
            policy.MaximumPackages is <= 0 or > DomainValues.MaximumLineQuantity ||
            policy.MaximumUnitPrice is <= 0 or > DomainValues.MaximumMoney ||
            policy.MaximumRefund is <= 0 or > DomainValues.MaximumMoney ||
            policy.MaximumUnitPrice is { } price && decimal.Round(price, 2) != price ||
            policy.MaximumRefund is { } refund && decimal.Round(refund, 2) != refund ||
            (policy.MaximumUnitPrice.HasValue || policy.MaximumRefund.HasValue) &&
                (policy.Currency is null || policy.Currency.Length != 3 || policy.Currency.Any(c => c is < 'A' or > 'Z')) ||
            !policy.MaximumUnitPrice.HasValue && !policy.MaximumRefund.HasValue && policy.Currency is not null)
            throw new DomainValidationException("anomaly_policy_invalid", "Review policy requires explicit version, approval and valid units");
    }

    public static ImmutableArray<AnomalySignal> Order(IReadOnlyList<AnomalyOrderLine> lines, AnomalyReviewPolicy? policy)
    {
        if (lines.Count is < 1 or > 100) throw new DomainValidationException("anomaly_source_unavailable", "A bounded captured order is required");
        if (policy is not null) Validate(policy);
        var result = ImmutableArray.CreateBuilder<AnomalySignal>();
        foreach (var line in lines) {
            if (!Text(line.Reference, 160) || line.Packages is < 1 or > DomainValues.MaximumLineQuantity ||
                line.UnitPrice < 0 || line.Subtotal < 0 || line.UnitPrice > DomainValues.MaximumMoney || line.Subtotal > DomainValues.MaximumMoney ||
                line.Currency.Length != 3 || line.Currency.Any(c => c is < 'A' or > 'Z'))
                throw new DomainValidationException("anomaly_source_unavailable", "Captured order facts are invalid");
            var expected = line.UnitPrice * line.Packages;
            result.Add(new("price", "price_extension", line.Subtotal == expected ? "consistent" : "flagged", line.Reference,
                Number(line.Subtotal), Number(expected), null,
                $"Subtotal tercatat {Number(line.Subtotal)} {line.Currency}; harga paket × jumlah = {Number(expected)} {line.Currency}."));
            result.Add(Threshold("quantity", "quantity_limit", line.Reference, line.Packages, policy?.MaximumPackages,
                "paket", policy?.Version));
            result.Add(Threshold("price", "price_limit", line.Reference, line.UnitPrice,
                policy?.Currency == line.Currency ? policy.MaximumUnitPrice : null, line.Currency, policy?.Version));
        }
        return result.ToImmutable();
    }

    public static ImmutableArray<AnomalySignal> Refund(AnomalyRefund refund, AnomalyReviewPolicy? policy)
    {
        if (policy is not null) Validate(policy);
        if (refund.Amount < 0 || refund.ReservedAmount < 0 || refund.Amount > DomainValues.MaximumMoney || refund.ReservedAmount > DomainValues.MaximumMoney ||
            refund.Currency.Length != 3 || refund.Currency.Any(c => c is < 'A' or > 'Z'))
            throw new DomainValidationException("anomaly_source_unavailable", "Captured refund facts are invalid");
        return [new("refund", "refund_reserved_amount", refund.Amount == refund.ReservedAmount ? "consistent" : "flagged", "refund",
            Number(refund.Amount), Number(refund.ReservedAmount), null,
            $"Nilai pengembalian {Number(refund.Amount)} {refund.Currency}; nilai yang disetujui {Number(refund.ReservedAmount)} {refund.Currency}."),
            Threshold("refund", "refund_limit", "refund", refund.Amount,
                policy?.Currency == refund.Currency ? policy.MaximumRefund : null, refund.Currency, policy?.Version)];
    }

    public static AnomalySignal AdjustmentUnavailable() => new("adjustment", "adjustment_source_unavailable", "unavailable", "adjustment", null, null, null,
        "Sumber penyesuaian dan pembanding stok yang disetujui belum tersedia. Hasil hitung fisik tidak dianggap sebagai penyesuaian stok.");
    private static AnomalySignal Threshold(string category, string code, string reference, decimal observed, decimal? maximum, string unit, string? version) =>
        maximum is { } limit ? new(category, code, observed > limit ? "flagged" : "within_policy", reference, Number(observed), Number(limit), version,
            $"Nilai {Number(observed)} {unit}; batas tinjauan {Number(limit)} {unit} pada kebijakan {version}. Tanda hanya muncul bila nilai melebihi batas.") :
        new(category, code, "policy_needed", reference, Number(observed), null, version,
            $"Nilai tercatat {Number(observed)} {unit}. Batas tinjauan yang disetujui untuk satuan ini belum tersedia; kewajaran belum dapat disimpulkan.");
    public static bool Text(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum && value == value.Trim() && !value.Any(char.IsControl);
    private static string Number(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
}
