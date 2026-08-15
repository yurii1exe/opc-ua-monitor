using Opc.Ua;
using OpcMonitor.Domain;
using OpcMonitor.Infrastructure;

namespace OpcMonitor.Tests;

public class DataValueMapperTests
{
    private static readonly DateTimeOffset Received = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CarriesValueQualityAndBothTimestamps()
    {
        var source = new DateTime(2026, 1, 1, 11, 59, 58, DateTimeKind.Utc);
        var server = new DateTime(2026, 1, 1, 11, 59, 59, DateTimeKind.Utc);

        var reading = DataValueMapper.ToReading("temp", new DataValue
        {
            Value = 21.5,
            StatusCode = StatusCodes.Good,
            SourceTimestamp = source,
            ServerTimestamp = server
        }, Received);

        Assert.Equal(21.5, reading.Value);
        Assert.True(reading.Quality.IsGood);
        Assert.Equal(source, reading.SourceTimestamp!.Value.UtcDateTime);
        Assert.Equal(server, reading.ServerTimestamp!.Value.UtcDateTime);
        Assert.Equal(Received, reading.ReceivedAt);
    }

    [Fact]
    public void ReportsAnUnsetTimestampAsNullRatherThanTheYearOne()
    {
        var reading = DataValueMapper.ToReading("temp", new DataValue
        {
            Value = 1,
            StatusCode = StatusCodes.Good,
            SourceTimestamp = DateTime.MinValue,
            ServerTimestamp = DateTime.MinValue
        }, Received);

        Assert.Null(reading.SourceTimestamp);
        Assert.Null(reading.ServerTimestamp);
        // Charts still need one axis, so the receive time stands in.
        Assert.Equal(Received, reading.EffectiveTimestamp);
    }

    [Fact]
    public void PrefersTheDeviceClockOverTheServerClock()
    {
        var source = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var server = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc);

        var reading = DataValueMapper.ToReading("temp", new DataValue
        {
            Value = 1,
            StatusCode = StatusCodes.Good,
            SourceTimestamp = source,
            ServerTimestamp = server
        }, Received);

        Assert.Equal(source, reading.EffectiveTimestamp.UtcDateTime);
    }

    [Fact]
    public void DropsTheValueOfABadQualityReadingButKeepsTheStatus()
    {
        var reading = DataValueMapper.ToReading("temp", new DataValue
        {
            Value = -1,
            StatusCode = StatusCodes.BadNodeIdUnknown
        }, Received);

        // A stale number with a bad status is how bad data reaches a dashboard
        // looking like good data.
        Assert.Null(reading.Value);
        Assert.Equal(QualitySeverity.Bad, reading.Quality.Severity);
        Assert.Equal("BadNodeIdUnknown", reading.Quality.Symbol);
    }

    [Fact]
    public void KeepsAnUncertainValueBecauseItIsStillInformation()
    {
        var reading = DataValueMapper.ToReading("temp", new DataValue
        {
            Value = 19.0,
            StatusCode = StatusCodes.UncertainLastUsableValue
        }, Received);

        Assert.Equal(19.0, reading.Value);
        Assert.Equal(QualitySeverity.Uncertain, reading.Quality.Severity);
    }

    [Fact]
    public void FlattensLocalizedTextAndNodeIdsToSomethingSerialisable()
    {
        var text = DataValueMapper.ToClrValue(new LocalizedText("en", "Running"));
        var nodeId = DataValueMapper.ToClrValue(new NodeId(2258));

        Assert.Equal("Running", text);
        Assert.Equal("i=2258", nodeId);
    }

    [Fact]
    public void RendersArraysReadablyInsteadOfAsATypeName()
    {
        var reading = DataValueMapper.ToReading("array", new DataValue
        {
            Value = new[] { 1, 2, 3 },
            StatusCode = StatusCodes.Good
        }, Received);

        Assert.Equal("[1, 2, 3]", reading.DisplayValue);
    }

    [Fact]
    public void ElidesLongArraysRatherThanFloodingTheUi()
    {
        var reading = DataValueMapper.ToReading("array", new DataValue
        {
            Value = Enumerable.Range(0, 50).ToArray(),
            StatusCode = StatusCodes.Good
        }, Received);

        Assert.Contains("(50 items)", reading.DisplayValue);
    }

    [Theory]
    [InlineData(0x00000000u, QualitySeverity.Good)]
    [InlineData(0x40000000u, QualitySeverity.Uncertain)]
    [InlineData(0x80000000u, QualitySeverity.Bad)]
    public void DecodesSeverityFromTheTopTwoStatusBits(uint statusCode, QualitySeverity expected)
    {
        Assert.Equal(expected, QualityCode.SeverityOf(statusCode));
    }
}
