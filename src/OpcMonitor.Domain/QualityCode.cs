namespace OpcMonitor.Domain;

/// <summary>
/// Coarse quality classification for a reading.
/// </summary>
/// <remarks>
/// OPC UA (Part 4, §7.34) encodes quality in a 32-bit StatusCode whose top two
/// bits carry the severity: 00 = Good, 01 = Uncertain, 10/11 = Bad. Callers of
/// the domain model rarely care about the remaining 30 bits, so the raw code is
/// preserved alongside this three-value summary rather than being thrown away.
/// </remarks>
public enum QualitySeverity
{
    Good = 0,
    Uncertain = 1,
    Bad = 2
}

/// <summary>
/// A protocol status code plus its severity, kept together so the UI can show a
/// badge without re-deriving the classification and diagnostics can still see
/// the exact code the server returned.
/// </summary>
/// <param name="Value">Raw 32-bit status code as returned by the server.</param>
/// <param name="Severity">Severity decoded from the two most significant bits.</param>
/// <param name="Symbol">Human-readable symbolic name, e.g. "Good" or "BadNodeIdUnknown".</param>
public readonly record struct QualityCode(uint Value, QualitySeverity Severity, string Symbol)
{
    /// <summary>The canonical Good status code (0x00000000).</summary>
    public static QualityCode Good { get; } = new(0u, QualitySeverity.Good, "Good");

    public bool IsGood => Severity == QualitySeverity.Good;

    /// <summary>
    /// Classifies a raw status code by its severity bits. This is the only place
    /// in the codebase that knows the bit layout.
    /// </summary>
    public static QualitySeverity SeverityOf(uint statusCode) => (statusCode >> 30) switch
    {
        0 => QualitySeverity.Good,
        1 => QualitySeverity.Uncertain,
        _ => QualitySeverity.Bad
    };

    public static QualityCode From(uint statusCode, string symbol) =>
        new(statusCode, SeverityOf(statusCode), symbol);

    public override string ToString() => $"{Symbol} (0x{Value:X8})";
}
