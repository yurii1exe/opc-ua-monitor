using OpcMonitor.Infrastructure;

namespace OpcMonitor.Tests;

public class NodeAddressTests
{
    [Theory]
    [InlineData("i=2258")]
    [InlineData("ns=2;s=Some.Tag")]
    [InlineData("ns=3;i=1001")]
    [InlineData("s=Plain.String.Id")]
    [InlineData("g=09087e75-8e5e-499b-954f-f2a8624db28a")]
    [InlineData("b=M/RbKBsRVkePCePcx24oRA==")]
    [InlineData("nsu=http://example.test/UA/;s=Tag")]
    public void RecognisesProtocolNodeIds(string address)
    {
        Assert.True(NodeAddress.LooksLikeNodeId(address));
    }

    [Theory]
    [InlineData("Server/ServerStatus/CurrentTime")]
    [InlineData("Boiler/Drum/LevelIndicator")]
    [InlineData("Simulation")]
    public void TreatsEverythingElseAsABrowsePath(string address)
    {
        // NodeId.Parse would accept every one of these as a namespace-zero
        // string identifier, which is why classification is by prefix and not by
        // trying to parse and seeing whether it throws.
        Assert.False(NodeAddress.LooksLikeNodeId(address));
    }

    [Theory]
    [InlineData("Server/ServerStatus/CurrentTime", new[] { "Server", "ServerStatus", "CurrentTime" })]
    [InlineData("/Server/ServerStatus/", new[] { "Server", "ServerStatus" })]
    [InlineData("Server // ServerStatus", new[] { "Server", "ServerStatus" })]
    public void SplitsBrowsePathsForgivingly(string address, string[] expected)
    {
        Assert.Equal(expected, NodeAddress.SplitBrowsePath(address));
    }

    [Fact]
    public void DefaultsTheDisplayNameToTheLeafSegment()
    {
        Assert.Equal("CurrentTime", NodeAddress.DefaultDisplayName("Server/ServerStatus/CurrentTime"));
    }

    [Fact]
    public void LeavesANodeIdAsItsOwnDisplayName()
    {
        Assert.Equal("i=2258", NodeAddress.DefaultDisplayName("i=2258"));
    }
}
