namespace OpcMonitor.Domain;

/// <summary>
/// One value change for one node.
/// </summary>
/// <param name="NodeId">Configured id of the node this reading belongs to.</param>
/// <param name="Value">
/// The value as delivered by the server, already converted to a CLR primitive.
/// Null is a legitimate value, distinct from a bad-quality reading.
/// </param>
/// <param name="Quality">Quality of this particular reading.</param>
/// <param name="SourceTimestamp">
/// When the underlying device produced the value. Servers may leave this unset,
/// in which case it is null rather than silently substituted.
/// </param>
/// <param name="ServerTimestamp">When the OPC UA server recorded the value.</param>
/// <param name="ReceivedAt">When this process received it. Always populated.</param>
public sealed record NodeReading(
    string NodeId,
    object? Value,
    QualityCode Quality,
    DateTimeOffset? SourceTimestamp,
    DateTimeOffset? ServerTimestamp,
    DateTimeOffset ReceivedAt)
{
    /// <summary>
    /// Best available timestamp: the device clock if the server supplied one,
    /// otherwise the server clock, otherwise our own receive time. Charts need a
    /// single axis and should not have to make this choice themselves.
    /// </summary>
    public DateTimeOffset EffectiveTimestamp =>
        SourceTimestamp ?? ServerTimestamp ?? ReceivedAt;

    /// <summary>Maximum array elements rendered before the display value elides.</summary>
    private const int MaxRenderedElements = 8;

    /// <summary>Formats the value for display without imposing a locale.</summary>
    public string DisplayValue => Render(Value);

    private static string Render(object? value) => value switch
    {
        null => "—",
        string text => text,
        // Arrays are common in OPC UA and the default ToString gives
        // "System.Object[]", which tells an operator nothing at all.
        System.Collections.IEnumerable sequence and not string => RenderSequence(sequence),
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "—"
    };

    private static string RenderSequence(System.Collections.IEnumerable sequence)
    {
        var rendered = new List<string>(MaxRenderedElements);
        var total = 0;

        foreach (var element in sequence)
        {
            total++;
            if (rendered.Count < MaxRenderedElements) rendered.Add(Render(element));
        }

        var suffix = total > rendered.Count ? $", … ({total} items)" : string.Empty;
        return $"[{string.Join(", ", rendered)}{suffix}]";
    }
}
