using KindleMount.Startup;
using Xunit;

namespace KindleMount.Tests.Startup;

public class StartupRegistrationTests
{
    [Fact]
    public void FormatCommand_QuotesPath()
    {
        Assert.Equal(
            @"""C:\Program Files\KindleMount\KindleMount.exe""",
            StartupRegistration.FormatCommand(@"C:\Program Files\KindleMount\KindleMount.exe"));
    }

    [Fact]
    public void PathFromCommand_RoundTripsFormatCommand()
    {
        const string exe = @"C:\Program Files\KindleMount\KindleMount.exe";

        Assert.Equal(exe, StartupRegistration.PathFromCommand(StartupRegistration.FormatCommand(exe)));
    }

    [Theory]
    [InlineData(@"""C:\Apps\KindleMount.exe""", @"C:\Apps\KindleMount.exe")]
    [InlineData(@"""C:\Apps\KindleMount.exe"" --scan", @"C:\Apps\KindleMount.exe")]
    [InlineData(@"C:\Apps\KindleMount.exe", @"C:\Apps\KindleMount.exe")]
    [InlineData(@"C:\Apps\KindleMount.exe --scan", @"C:\Apps\KindleMount.exe")]
    [InlineData(@"   ""C:\Apps\KindleMount.exe""   ", @"C:\Apps\KindleMount.exe")]
    public void PathFromCommand_ExtractsExecutable(string command, string expected)
    {
        Assert.Equal(expected, StartupRegistration.PathFromCommand(command));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PathFromCommand_ReturnsEmptyWhenMissing(string? command)
    {
        Assert.Equal(string.Empty, StartupRegistration.PathFromCommand(command));
    }

    [Fact]
    public void PathFromCommand_HandlesUnterminatedQuote()
    {
        Assert.Equal(
            @"C:\Apps\KindleMount.exe",
            StartupRegistration.PathFromCommand(@"""C:\Apps\KindleMount.exe"));
    }

    [Fact]
    public void ValueName_MatchesAppName()
    {
        Assert.Equal(AppInfo.Name, StartupRegistration.ValueName);
    }
}
