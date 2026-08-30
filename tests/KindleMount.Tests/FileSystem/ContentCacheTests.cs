using KindleMount.FileSystem;
using Xunit;

namespace KindleMount.Tests.FileSystem;

public class ContentCacheTests : IDisposable
{
    private readonly string _directory;

    public ContentCacheTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "KindleMountTests_" + Guid.NewGuid().ToString("N"));
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
    public void Constructor_CreatesDirectory()
    {
        _ = new ContentCache(_directory, 0);

        Assert.True(Directory.Exists(_directory));
    }

    [Fact]
    public void GetCachePath_IsDeterministic_ForSameInputs()
    {
        var cache = new ContentCache(_directory, 0);
        var writeTime = new DateTime(2024, 1, 1);

        var path1 = cache.GetCachePath("\\documents\\book.azw3", 1000, writeTime);
        var path2 = cache.GetCachePath("\\documents\\book.azw3", 1000, writeTime);

        Assert.Equal(path1, path2);
    }

    [Fact]
    public void GetCachePath_Differs_WhenLengthDiffers()
    {
        var cache = new ContentCache(_directory, 0);
        var writeTime = new DateTime(2024, 1, 1);

        var path1 = cache.GetCachePath("\\documents\\book.azw3", 1000, writeTime);
        var path2 = cache.GetCachePath("\\documents\\book.azw3", 2000, writeTime);

        Assert.NotEqual(path1, path2);
    }

    [Fact]
    public void GetCachePath_Differs_WhenPathDiffers()
    {
        var cache = new ContentCache(_directory, 0);
        var writeTime = new DateTime(2024, 1, 1);

        var path1 = cache.GetCachePath("\\documents\\book1.azw3", 1000, writeTime);
        var path2 = cache.GetCachePath("\\documents\\book2.azw3", 1000, writeTime);

        Assert.NotEqual(path1, path2);
    }

    [Fact]
    public void GetCachePath_IsCaseInsensitive_ForPath()
    {
        var cache = new ContentCache(_directory, 0);
        var writeTime = new DateTime(2024, 1, 1);

        var path1 = cache.GetCachePath("\\Documents\\Book.azw3", 1000, writeTime);
        var path2 = cache.GetCachePath("\\documents\\book.azw3", 1000, writeTime);

        Assert.Equal(path1, path2);
    }

    [Fact]
    public void CreateScratchPath_ReturnsUniquePaths()
    {
        var cache = new ContentCache(_directory, 0);

        var path1 = cache.CreateScratchPath();
        var path2 = cache.CreateScratchPath();

        Assert.NotEqual(path1, path2);
        Assert.True(ContentCache.IsScratch(path1));
        Assert.True(ContentCache.IsScratch(path2));
    }

    [Theory]
    [InlineData("scratch_abc123.tmp", true)]
    [InlineData("normal_abc123.bin", false)]
    public void IsScratch_DetectsScratchFiles(string fileName, bool expected)
    {
        Assert.Equal(expected, ContentCache.IsScratch(fileName));
    }

    [Fact]
    public void IsUsable_ReturnsFalse_WhenFileDoesNotExist()
    {
        var cache = new ContentCache(_directory, 0);
        var missingPath = Path.Combine(_directory, "missing.bin");

        Assert.False(cache.IsUsable(missingPath, 100));
    }

    [Fact]
    public void IsUsable_ReturnsTrue_WhenLengthMatches()
    {
        var cache = new ContentCache(_directory, 0);
        var filePath = Path.Combine(_directory, "existing.bin");
        File.WriteAllBytes(filePath, new byte[100]);

        Assert.True(cache.IsUsable(filePath, 100));
    }

    [Fact]
    public void IsUsable_ReturnsFalse_WhenLengthMismatches()
    {
        var cache = new ContentCache(_directory, 0);
        var filePath = Path.Combine(_directory, "existing.bin");
        File.WriteAllBytes(filePath, new byte[100]);

        Assert.False(cache.IsUsable(filePath, 200));
    }

    [Fact]
    public void Remove_DeletesExistingFile()
    {
        var cache = new ContentCache(_directory, 0);
        var filePath = Path.Combine(_directory, "toDelete.bin");
        File.WriteAllBytes(filePath, new byte[10]);

        cache.Remove(filePath);

        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void Remove_DoesNotThrow_WhenFileMissing()
    {
        var cache = new ContentCache(_directory, 0);
        var filePath = Path.Combine(_directory, "missing.bin");

        var exception = Record.Exception(() => cache.Remove(filePath));

        Assert.Null(exception);
    }

    [Fact]
    public void CleanupScratch_RemovesOnlyScratchFiles()
    {
        var cache = new ContentCache(_directory, 0);
        var scratchPath = cache.CreateScratchPath();
        File.WriteAllBytes(scratchPath, new byte[10]);

        var normalPath = Path.Combine(_directory, "keep.bin");
        File.WriteAllBytes(normalPath, new byte[10]);

        cache.CleanupScratch();

        Assert.False(File.Exists(scratchPath));
        Assert.True(File.Exists(normalPath));
    }

    [Fact]
    public void Trim_RemovesOldestFiles_WhenOverLimit()
    {
        var cache = new ContentCache(_directory, limitBytes: 10);

        var oldFile = Path.Combine(_directory, "old.bin");
        var newFile = Path.Combine(_directory, "new.bin");
        File.WriteAllBytes(oldFile, new byte[10]);
        File.SetLastAccessTimeUtc(oldFile, DateTime.UtcNow.AddMinutes(-10));

        File.WriteAllBytes(newFile, new byte[10]);
        File.SetLastAccessTimeUtc(newFile, DateTime.UtcNow);

        cache.Trim();

        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
    }

    [Fact]
    public void Trim_DoesNothing_WhenLimitIsZeroOrNegative()
    {
        var cache = new ContentCache(_directory, limitBytes: 0);
        var filePath = Path.Combine(_directory, "keep.bin");
        File.WriteAllBytes(filePath, new byte[100]);

        cache.Trim();

        Assert.True(File.Exists(filePath));
    }
}
