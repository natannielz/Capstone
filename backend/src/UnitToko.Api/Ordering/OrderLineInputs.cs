using System.Globalization;
using UnitToko.Modules.Contracts;

namespace UnitToko.Api.Ordering;

internal static class OrderLineInputs
{
    internal static long Revision(string value) {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision.ToString(CultureInfo.InvariantCulture) != value || revision != 0 && (revision is < 2 or > 103))
            throw new DomainValidationException("invalid_order_line_identity", "Choose the exact accepted order line");
        return revision;
    }
}
