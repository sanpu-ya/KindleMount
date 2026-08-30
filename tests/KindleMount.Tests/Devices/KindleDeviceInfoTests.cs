using KindleMount.Devices;
using Xunit;

namespace KindleMount.Tests.Devices;

public class KindleDeviceInfoTests
{
    [Fact]
    public void DisplayName_PrefersFriendlyName()
    {
        var info = new KindleDeviceInfo("id", "MyKindle", "desc", "manufacturer", null);

        Assert.Equal("MyKindle", info.DisplayName);
    }

    [Fact]
    public void DisplayName_FallsBackToDescription_WhenFriendlyNameEmpty()
    {
        var info = new KindleDeviceInfo("id", "", "desc", "manufacturer", null);

        Assert.Equal("desc", info.DisplayName);
    }

    [Fact]
    public void DisplayName_FallsBackToManufacturer_WhenFriendlyNameAndDescriptionEmpty()
    {
        var info = new KindleDeviceInfo("id", "", "", "manufacturer", null);

        Assert.Equal("manufacturer", info.DisplayName);
    }

    [Fact]
    public void DisplayName_FallsBackToDefault_WhenAllEmpty()
    {
        var info = new KindleDeviceInfo("id", "", "", "", null);

        Assert.Equal("MTP デバイス", info.DisplayName);
    }

    [Fact]
    public void DisplayName_TrimsWhitespace()
    {
        var info = new KindleDeviceInfo("id", "  MyKindle  ", "desc", "manufacturer", null);

        Assert.Equal("MyKindle", info.DisplayName);
    }

    [Fact]
    public void StableKey_IsDeterministic_ForSameDeviceId()
    {
        var info1 = new KindleDeviceInfo("DEVICE-123", "a", "b", "c", null);
        var info2 = new KindleDeviceInfo("device-123", "x", "y", "z", null);

        Assert.Equal(info1.StableKey, info2.StableKey);
    }

    [Fact]
    public void StableKey_PrefersSerialNumber_OverDeviceId()
    {
        var withSerial = new KindleDeviceInfo("id-1", "a", "b", "c", "SERIAL-1");
        var withDifferentIdSameSerial = new KindleDeviceInfo("id-2", "a", "b", "c", "SERIAL-1");

        Assert.Equal(withSerial.StableKey, withDifferentIdSameSerial.StableKey);
    }

    [Fact]
    public void StableKey_DiffersForDifferentDeviceIds()
    {
        var info1 = new KindleDeviceInfo("id-1", "a", "b", "c", null);
        var info2 = new KindleDeviceInfo("id-2", "a", "b", "c", null);

        Assert.NotEqual(info1.StableKey, info2.StableKey);
    }

    [Fact]
    public void Equals_IsCaseInsensitive_OnDeviceId()
    {
        var info1 = new KindleDeviceInfo("DEVICE-123", "a", "b", "c", null);
        var info2 = new KindleDeviceInfo("device-123", "x", "y", "z", null);

        Assert.True(info1.Equals(info2));
        Assert.Equal(info1.GetHashCode(), info2.GetHashCode());
    }

    [Fact]
    public void Equals_ReturnsFalse_ForDifferentDeviceId()
    {
        var info1 = new KindleDeviceInfo("id-1", "a", "b", "c", null);
        var info2 = new KindleDeviceInfo("id-2", "a", "b", "c", null);

        Assert.False(info1.Equals(info2));
    }

    // 以下は実機 (Kindle Paperwhite Signature Edition) で観測した値をもとにしている。
    private const string WpdDeviceId =
        @"\?\usb#vid_1949&pid_9981#gn433w074287041k#{6ac27878-a6fa-4155-ba85-f98f491d4f33}";

    private static KindleDeviceInfo Kindle() =>
        new(WpdDeviceId, "Kindle Paperwhite Signature Edition", "MTP USB デバイス", "", "GN433W074287041K");

    [Fact]
    public void MatchesDevicePath_MatchesItself()
    {
        Assert.True(Kindle().MatchesDevicePath(WpdDeviceId));
    }

    [Fact]
    public void MatchesDevicePath_IgnoresCase()
    {
        Assert.True(Kindle().MatchesDevicePath(WpdDeviceId.ToUpperInvariant()));
    }

    [Fact]
    public void MatchesDevicePath_MatchesUsbInterfaceNotification()
    {
        // 同じ機器でも USB インターフェース経由の通知は末尾の GUID が異なる。
        const string usbNotification =
            @"\?\USB#VID_1949&PID_9981#GN433W074287041K#{a5dcbf10-6530-11d2-901f-00c04fb951ed}";

        Assert.True(Kindle().MatchesDevicePath(usbNotification));
    }

    [Fact]
    public void MatchesDevicePath_RejectsDifferentSerialNumber()
    {
        const string otherUnit =
            @"\?\usb#vid_1949&pid_9981#zz999z000000000z#{6ac27878-a6fa-4155-ba85-f98f491d4f33}";

        Assert.False(Kindle().MatchesDevicePath(otherUnit));
    }

    [Fact]
    public void MatchesDevicePath_RejectsDifferentProduct()
    {
        const string otherModel =
            @"\?\usb#vid_1949&pid_0004#gn433w074287041k#{6ac27878-a6fa-4155-ba85-f98f491d4f33}";

        Assert.False(Kindle().MatchesDevicePath(otherModel));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MatchesDevicePath_RejectsEmpty(string? devicePath)
    {
        Assert.False(Kindle().MatchesDevicePath(devicePath));
    }
}
