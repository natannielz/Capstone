using System.Collections.Immutable;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Ordering;

/// <summary>A branch asks a single fulfillment owner for physical packages. This is no sale, price or stock promise.</summary>
public sealed record BranchOrderRequestLine(LongId SkuId, LongId DefinitionId, long DefinitionVersion, int Packages);
public sealed record BranchOrderRequestInput(OrderBuyingContext Context, ImmutableArray<BranchOrderRequestLine> Lines, string? Note);
public enum BranchOrderRequestDecision { Accept, Reject }

public static class BranchOrderRequestRules
{
    public static BranchOrderRequestInput Copy(BranchOrderRequestInput value)
    {
        if (value is null || value.Context is null || value.Lines.IsDefaultOrEmpty || value.Lines.Length > 100 ||
            value.Lines.Any(line => line is null || line.SkuId.Value <= 0 || line.DefinitionId.Value <= 0 ||
                line.DefinitionVersion <= 0 || line.Packages is < 1 or > DomainValues.MaximumLineQuantity) ||
            value.Lines.Select(line => line.SkuId).Distinct().Count() != value.Lines.Length)
            throw Invalid();
        value.Context.Validate();
        if (value.Note is { } note && (string.IsNullOrWhiteSpace(note) || note.Length > 1000 || note.Trim() != note || note.Any(char.IsControl))) throw Invalid();
        return value with { Lines = value.Lines.ToArray().ToImmutableArray() };
    }
    public static void Reason(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() != value || value.Length > 1000 || value.Any(char.IsControl)) throw Invalid();
    }
    private static DomainValidationException Invalid() => new("invalid_branch_order_request", "Choose a branch, current packages and bounded review text");
}
