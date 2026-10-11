using MRemoteMac.Controls;
using MRemoteMac.Models;
using Xunit;

namespace MRemoteMac.Tests;

/// <summary>mRemoteMac opens RDP only; other protocols from mRemoteNG files are preserved but marked unsupported.</summary>
public class ProtocolTests
{
    private static PropertyRow ProtocolRow(ConnectionInfo c) =>
        new(c, typeof(ConnectionInfo).GetProperty(nameof(ConnectionInfo.Protocol))!, new CategoryRow("Protocol"), inheritanceRow: false);

    [Fact]
    public void RdpConnectionIsSupported()
    {
        var c = new ConnectionInfo { Protocol = ProtocolType.RDP };
        Assert.True(c.IsSupported);
        Assert.Null(c.UnsupportedHint);
        Assert.Equal([ProtocolType.RDP], ProtocolRow(c).EnumValues.Cast<ProtocolType>());
    }

    [Fact]
    public void NonRdpConnectionIsMarkedUnsupportedAndCanOnlySwitchToRdp()
    {
        var c = new ConnectionInfo { Protocol = ProtocolType.SSH2 };
        Assert.False(c.IsSupported);
        Assert.Contains("SSH2", c.UnsupportedHint);
        Assert.Equal([ProtocolType.RDP, ProtocolType.SSH2], ProtocolRow(c).EnumValues.Cast<ProtocolType>());
    }

    [Fact]
    public void ChangingProtocolRaisesIsSupported()
    {
        var c = new ConnectionInfo { Protocol = ProtocolType.SSH2 };
        var raised = new List<string?>();
        c.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        c.Protocol = ProtocolType.RDP;
        Assert.Contains(nameof(ConnectionInfo.IsSupported), raised);
        Assert.True(c.IsSupported);
    }

    [Fact]
    public void FoldersAreAlwaysSupported() => Assert.True(new ContainerInfo().IsSupported);
}
