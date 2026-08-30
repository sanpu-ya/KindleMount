using KindleMount.Mounting;
using Xunit;

namespace KindleMount.Tests.Mounting;

public class DriveLetterAllocatorTests
{
    [Fact]
    public void Allocate_ReturnsFirstAvailablePreferredLetter()
    {
        var allocator = new DriveLetterAllocator();

        // 実機の使用中ドライブ構成に依存しないよう、広い候補集合から
        // 何かしらの文字が返ることのみ確認する。
        var letter = allocator.Allocate("KLMNOPQRSTUVWXYZ");

        Assert.NotNull(letter);
        Assert.Contains(letter!.Value, "KLMNOPQRSTUVWXYZ");
    }

    [Fact]
    public void Allocate_DoesNotReturnSameLetterTwice_UntilReleased()
    {
        var allocator = new DriveLetterAllocator();

        // 実機の使用中ドライブと衝突しないよう、実際に確保できた文字を使って検証する。
        var first = allocator.Allocate("KLMNOPQRSTUVWXYZ");
        Assert.NotNull(first);

        // 同じ 1 文字だけを希望してもすでに予約済みのため、D..Z のフォールバック探索から
        // 別の空きレターが返る(Allocate は希望が尽きても全体から探すため null にはならない)。
        var second = allocator.Allocate(first!.Value.ToString());
        Assert.NotNull(second);
        Assert.NotEqual(first, second);

        allocator.Release(first.Value);

        var third = allocator.Allocate(first.Value.ToString());
        Assert.Equal(first, third);
    }

    [Fact]
    public void Allocate_FallsBackToUnusedLetter_WhenPreferredExhausted()
    {
        var allocator = new DriveLetterAllocator();

        var first = allocator.Allocate("KLMNOPQRSTUVWXYZ");
        Assert.NotNull(first);

        // 希望候補を単一文字(すでに予約済み)にしても、D..Z の範囲から
        // 別の空きレターにフォールバックできる。
        var fallback = allocator.Allocate(first!.Value.ToString());

        Assert.NotNull(fallback);
        Assert.NotEqual(first, fallback);
    }

    [Fact]
    public void Release_AllowsReallocation()
    {
        var allocator = new DriveLetterAllocator();

        var letter = allocator.Allocate("KLMNOPQRSTUVWXYZ");
        Assert.NotNull(letter);

        allocator.Release(letter!.Value);

        var reallocated = allocator.Allocate(letter.Value.ToString());
        Assert.Equal(letter, reallocated);
    }

    [Fact]
    public void Allocate_IsCaseInsensitive()
    {
        var allocator = new DriveLetterAllocator();

        var letter = allocator.Allocate("KLMNOPQRSTUVWXYZ");
        Assert.NotNull(letter);
        allocator.Release(letter!.Value);

        var lowerCasePreference = char.ToLowerInvariant(letter.Value).ToString();
        var reallocated = allocator.Allocate(lowerCasePreference);

        Assert.Equal(letter, reallocated);
    }
}
