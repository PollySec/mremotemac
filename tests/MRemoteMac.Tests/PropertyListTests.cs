using MRemoteMac.Models;
using Xunit;

namespace MRemoteMac.Tests;

/// <summary>Grid shows only functional properties; the file keeps every mRemoteNG property.</summary>
public class PropertyListTests
{
    [Theory]
    [InlineData(nameof(ConnectionInfo.Panel))]
    [InlineData(nameof(ConnectionInfo.RdpVersion))]
    [InlineData(nameof(ConnectionInfo.ExtApp))]
    [InlineData(nameof(ConnectionInfo.RedirectKeys))]
    public void HiddenPropertiesStayInTheFileListButNotInTheGrid(string name)
    {
        Assert.Contains(ConnectionInfo.FileProperties, p => p.Name == name);
        Assert.DoesNotContain(ConnectionInfo.GridProperties, p => p.Name == name);
    }

    [Fact]
    public void EveryGridPropertyHasHelpText()
    {
        var missing = ConnectionInfo.GridProperties
            .Where(p => string.IsNullOrWhiteSpace(p.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .OfType<System.ComponentModel.DescriptionAttribute>().FirstOrDefault()?.Description))
            .Select(p => p.Name).ToList();
        Assert.Empty(missing);
    }
}
