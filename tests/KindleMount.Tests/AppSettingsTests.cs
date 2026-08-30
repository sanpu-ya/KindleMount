using KindleMount;
using Xunit;

namespace KindleMount.Tests;

public class AppSettingsTests : IDisposable
{
    private readonly string _directory;
    private readonly string _settingsPath;

    public AppSettingsTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "KindleMountTests_" + Guid.NewGuid().ToString("N"));
        _settingsPath = Path.Combine(_directory, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenFileDoesNotExist()
    {
        var settings = AppSettings.Load(_settingsPath);

        Assert.True(settings.AutoMount);
        Assert.True(settings.ShowNotifications);
        Assert.True(settings.RemovableDrive);
        Assert.False(settings.ReadOnly);
        Assert.Equal(15, settings.DirectoryCacheSeconds);
        Assert.Equal(4096, settings.FileCacheLimitMegabytes);
        Assert.Equal(300, settings.DokanTimeoutSeconds);
        Assert.Equal(15, settings.PollIntervalSeconds);
        Assert.Equal("KLMNOPQRSTUVWXYZ", settings.PreferredDriveLetters);
        Assert.Empty(settings.ExtraDeviceNamePatterns);
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenFileIsCorruptJson()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_settingsPath, "{ not valid json !!!");

        var settings = AppSettings.Load(_settingsPath);

        Assert.True(settings.AutoMount);
        Assert.Equal("KLMNOPQRSTUVWXYZ", settings.PreferredDriveLetters);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsValues()
    {
        var settings = AppSettings.Load(_settingsPath);
        settings.AutoMount = false;
        settings.ReadOnly = true;
        settings.DirectoryCacheSeconds = 30;
        settings.PreferredDriveLetters = "XY";
        settings.ExtraDeviceNamePatterns.Add("MyReader");

        settings.Save();

        var reloaded = AppSettings.Load(_settingsPath);

        Assert.False(reloaded.AutoMount);
        Assert.True(reloaded.ReadOnly);
        Assert.Equal(30, reloaded.DirectoryCacheSeconds);
        Assert.Equal("XY", reloaded.PreferredDriveLetters);
        Assert.Contains("MyReader", reloaded.ExtraDeviceNamePatterns);
    }

    [Fact]
    public void Save_DoesNotThrow_WhenPathIsUnset()
    {
        var settings = new AppSettings();

        var exception = Record.Exception(() => settings.Save());

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(600, 600)]
    [InlineData(1000, 600)]
    public void Normalize_ClampsDirectoryCacheSeconds(int input, int expected)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_settingsPath, $$"""{"directoryCacheSeconds": {{input}}}""");

        var settings = AppSettings.Load(_settingsPath);

        Assert.Equal(expected, settings.DirectoryCacheSeconds);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(10, 30)]
    [InlineData(300, 300)]
    [InlineData(10000, 3600)]
    public void Normalize_ClampsDokanTimeoutSeconds(int input, int expected)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_settingsPath, $$"""{"dokanTimeoutSeconds": {{input}}}""");

        var settings = AppSettings.Load(_settingsPath);

        Assert.Equal(expected, settings.DokanTimeoutSeconds);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(1, 3)]
    [InlineData(15, 15)]
    [InlineData(10000, 3600)]
    public void Normalize_ClampsPollIntervalSeconds(int input, int expected)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_settingsPath, $$"""{"pollIntervalSeconds": {{input}}}""");

        var settings = AppSettings.Load(_settingsPath);

        Assert.Equal(expected, settings.PollIntervalSeconds);
    }

    [Fact]
    public void Normalize_FallsBackToDefaultDriveLetters_WhenAllInvalid()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_settingsPath, """{"preferredDriveLetters": "ABC"}""");

        // A, B, C は D..Z の範囲外なのですべて除去され、既定値へフォールバックする。
        var settings = AppSettings.Load(_settingsPath);

        Assert.Equal("KLMNOPQRSTUVWXYZ", settings.PreferredDriveLetters);
    }

    [Fact]
    public void Normalize_FiltersOutOfRangeLettersAndDeduplicates()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_settingsPath, """{"preferredDriveLetters": "abKKZZ"}""");

        var settings = AppSettings.Load(_settingsPath);

        Assert.Equal("KZ", settings.PreferredDriveLetters);
    }

    [Fact]
    public void Normalize_ClampsFileCacheLimitToNonNegative()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_settingsPath, """{"fileCacheLimitMegabytes": -100}""");

        var settings = AppSettings.Load(_settingsPath);

        Assert.Equal(0, settings.FileCacheLimitMegabytes);
    }

    [Fact]
    public void Normalize_InitializesNullExtraDeviceNamePatterns()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_settingsPath, """{"extraDeviceNamePatterns": null}""");

        var settings = AppSettings.Load(_settingsPath);

        Assert.NotNull(settings.ExtraDeviceNamePatterns);
        Assert.Empty(settings.ExtraDeviceNamePatterns);
    }

    [Fact]
    public void ResolveVolumeLabel_UsesDeviceName_WhenNotConfigured()
    {
        var settings = new AppSettings();

        Assert.Equal("Kindle Paperwhite Signature Edition",
            settings.ResolveVolumeLabel("Kindle Paperwhite Signature Edition"));
    }

    [Fact]
    public void ResolveVolumeLabel_UsesFixedLabel_WhenConfigured()
    {
        var settings = new AppSettings { VolumeLabel = "Kindle" };

        Assert.Equal("Kindle", settings.ResolveVolumeLabel("Kindle Paperwhite Signature Edition"));
    }

    [Fact]
    public void ResolveVolumeLabel_TrimsSurroundingWhitespace()
    {
        var settings = new AppSettings { VolumeLabel = "  Kindle  " };

        Assert.Equal("Kindle", settings.ResolveVolumeLabel("device"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveVolumeLabel_FallsBackToDeviceName_WhenBlank(string configured)
    {
        var settings = new AppSettings { VolumeLabel = configured };

        Assert.Equal("device", settings.ResolveVolumeLabel("device"));
    }

    [Fact]
    public void VolumeLabel_RoundTripsThroughFile()
    {
        var settings = AppSettings.Load(_settingsPath);
        settings.VolumeLabel = "Kindle";
        settings.Save();

        var reloaded = AppSettings.Load(_settingsPath);

        Assert.Equal("Kindle", reloaded.VolumeLabel);
    }

    [Fact]
    public void VolumeLabel_DefaultsToEmpty_WhenAbsentFromFile()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_settingsPath, """{ "autoMount": true }""");

        var loaded = AppSettings.Load(_settingsPath);

        Assert.Equal(string.Empty, loaded.VolumeLabel);
    }
}
