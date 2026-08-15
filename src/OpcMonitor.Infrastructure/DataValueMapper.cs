using System.Xml;
using Opc.Ua;
using OpcMonitor.Domain;

namespace OpcMonitor.Infrastructure;

/// <summary>
/// Converts the SDK's <see cref="DataValue"/> into the domain's
/// <see cref="NodeReading"/>. This is the boundary: nothing in
/// <c>OpcMonitor.Domain</c> or <c>OpcMonitor.Api</c> handles an SDK type.
/// </summary>
public static class DataValueMapper
{
    public static NodeReading ToReading(string nodeId, DataValue dataValue, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(dataValue);

        var status = dataValue.StatusCode;
        var quality = QualityCode.From(status.Code, StatusCodes.GetBrowseName(status.Code));

        return new NodeReading(
            NodeId: nodeId,
            Value: quality.Severity == QualitySeverity.Bad ? null : ToClrValue(dataValue.Value),
            Quality: quality,
            SourceTimestamp: ToOffset(dataValue.SourceTimestamp),
            ServerTimestamp: ToOffset(dataValue.ServerTimestamp),
            ReceivedAt: receivedAt);
    }

    /// <summary>
    /// An unset timestamp arrives as <see cref="DateTime.MinValue"/>. Mapping it
    /// to null keeps "the server did not say" distinguishable from "the year 1".
    /// </summary>
    private static DateTimeOffset? ToOffset(DateTime value)
    {
        if (value == DateTime.MinValue) return null;

        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return new DateTimeOffset(utc);
    }

    /// <summary>
    /// Reduces an OPC UA variant to something a JSON serialiser will render
    /// usefully. Structured and opaque types become their string form rather
    /// than being dropped: a dashboard showing the text of an unfamiliar type is
    /// more useful than one showing a blank cell.
    /// </summary>
    public static object? ToClrValue(object? value) => value switch
    {
        null => null,
        Variant variant => ToClrValue(variant.Value),
        LocalizedText text => text.Text,
        QualifiedName name => name.Name,
        NodeId nodeId => nodeId.ToString(),
        ExpandedNodeId expanded => expanded.ToString(),
        StatusCode code => StatusCodes.GetBrowseName(code.Code),
        XmlElement element => element.OuterXml,
        ExtensionObject extension => extension.ToString(),
        byte[] bytes => Convert.ToBase64String(bytes),
        Matrix matrix => ToClrValue(matrix.ToArray()),
        Array array => FlattenArray(array),
        _ => value
    };

    private static object?[] FlattenArray(Array array)
    {
        var items = new object?[array.Length];
        var index = 0;
        foreach (var element in array) items[index++] = ToClrValue(element);
        return items;
    }
}
