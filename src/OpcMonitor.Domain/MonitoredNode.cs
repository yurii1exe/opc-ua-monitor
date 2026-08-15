namespace OpcMonitor.Domain;

/// <summary>
/// A node the service has been asked to watch.
/// </summary>
/// <param name="Id">
/// Stable identifier used by the API and the dashboard. This is the address the
/// operator configured — either a protocol node id or a browse path — not
/// necessarily the node id the server ended up resolving it to.
/// </param>
/// <param name="DisplayName">Label shown on the dashboard.</param>
/// <param name="Unit">Engineering unit, e.g. "degC". Null when unitless.</param>
/// <param name="Minimum">Lower bound of the normal band, for alarm colouring.</param>
/// <param name="Maximum">Upper bound of the normal band, for alarm colouring.</param>
public sealed record MonitoredNode(
    string Id,
    string DisplayName,
    string? Unit = null,
    double? Minimum = null,
    double? Maximum = null)
{
    /// <summary>
    /// Classifies a numeric value against the configured band. Returns
    /// <c>null</c> when no band is configured or the value is not numeric, so
    /// the caller can distinguish "in range" from "no opinion".
    /// </summary>
    public bool? IsWithinBand(object? value)
    {
        if (Minimum is null && Maximum is null) return null;
        if (!TryAsDouble(value, out var d)) return null;

        if (Minimum is { } min && d < min) return false;
        if (Maximum is { } max && d > max) return false;
        return true;
    }

    private static bool TryAsDouble(object? value, out double result)
    {
        switch (value)
        {
            case null:
                result = 0;
                return false;
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case IConvertible convertible and (sbyte or byte or short or ushort or int or uint or long or ulong or decimal):
                result = convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
