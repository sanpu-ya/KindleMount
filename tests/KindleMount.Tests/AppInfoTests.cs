using KindleMount;
using Xunit;

namespace KindleMount.Tests;

public class AppInfoTests
{
    [Theory]
    [InlineData("0.1", "v0.1")]
    [InlineData("0.1.0", "v0.1")]
    [InlineData("0.1.0.0", "v0.1")]
    [InlineData("1.2.3", "v1.2.3")]
    [InlineData("1.2.3.4", "v1.2.3.4")]
    [InlineData("2.0", "v2.0")]
    public void FormatVersion_TrimsTrailingZeroSegments(string raw, string expected)
    {
        Assert.Equal(expected, AppInfo.FormatVersion(raw));
    }

    [Theory]
    [InlineData("0.1.0+9a1b2c3", "v0.1")]
    [InlineData("1.2.3+build.55", "v1.2.3")]
    public void FormatVersion_DropsBuildMetadata(string raw, string expected)
    {
        Assert.Equal(expected, AppInfo.FormatVersion(raw));
    }

    [Fact]
    public void FormatVersion_DoesNotDoublePrefix()
    {
        Assert.Equal("v0.1", AppInfo.FormatVersion("v0.1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FormatVersion_FallsBackWhenMissing(string? raw)
    {
        Assert.Equal("v0.0", AppInfo.FormatVersion(raw));
    }

    [Fact]
    public void FormatVersion_KeepsAtLeastTwoSegments()
    {
        Assert.Equal("v1.0", AppInfo.FormatVersion("1.0.0.0"));
    }

    [Fact]
    public void DisplayVersion_MatchesAssemblyVersion()
    {
        Assert.Equal("v0.1", AppInfo.DisplayVersion);
    }

    [Fact]
    public void NameWithVersion_CombinesNameAndVersion()
    {
        Assert.Equal("KindleMount v0.1", AppInfo.NameWithVersion);
    }
}
