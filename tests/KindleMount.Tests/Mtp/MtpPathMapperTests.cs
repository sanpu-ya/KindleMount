using KindleMount.Mtp;
using Xunit;

namespace KindleMount.Tests.Mtp;

public class MtpPathMapperTests
{
    [Theory]
    [InlineData("", "\\")]
    [InlineData(null, "\\")]
    [InlineData("\\", "\\")]
    [InlineData("documents", "\\documents")]
    [InlineData("/documents/book.azw3", "\\documents\\book.azw3")]
    [InlineData("\\documents\\", "\\documents")]
    [InlineData("\\documents\\book.azw3\\", "\\documents\\book.azw3")]
    public void NormalizeDokanPath_ReturnsExpected(string? input, string expected)
    {
        var result = MtpPathMapper.NormalizeDokanPath(input!);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("\\", "\\")]
    [InlineData("\\documents", "\\")]
    [InlineData("\\documents\\book.azw3", "\\documents")]
    [InlineData("\\a\\b\\c", "\\a\\b")]
    public void GetParent_ReturnsExpected(string input, string expected)
    {
        var result = MtpPathMapper.GetParent(input);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("\\", "")]
    [InlineData("\\documents", "documents")]
    [InlineData("\\documents\\book.azw3", "book.azw3")]
    public void GetName_ReturnsExpected(string input, string expected)
    {
        var result = MtpPathMapper.GetName(input);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("\\", "book.azw3", "\\book.azw3")]
    [InlineData("\\documents", "book.azw3", "\\documents\\book.azw3")]
    [InlineData("/documents/", "book.azw3", "\\documents\\book.azw3")]
    public void Combine_ReturnsExpected(string parent, string name, string expected)
    {
        var result = MtpPathMapper.Combine(parent, name);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsVirtualRoot_WhenNoStoragesRefreshed_ReturnsFalseBeforeRefresh()
    {
        var mapper = new MtpPathMapper();

        // Refresh されていない状態では HasVirtualRoot は true (_singleStorageRoot が null) だが、
        // ルート以外は false になることを確認する。
        Assert.False(mapper.IsVirtualRoot("\\documents"));
    }

    [Fact]
    public void IsStorageRoot_RootPath_ReturnsTrue()
    {
        var mapper = new MtpPathMapper();

        Assert.True(mapper.IsStorageRoot("\\"));
    }
}
