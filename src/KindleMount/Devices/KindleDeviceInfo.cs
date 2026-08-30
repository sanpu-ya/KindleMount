using System.Security.Cryptography;
using System.Text;

namespace KindleMount.Devices;

/// <summary>検知された MTP デバイス 1 台分の識別情報。</summary>
public sealed record KindleDeviceInfo(
    string DeviceId,
    string FriendlyName,
    string Description,
    string Manufacturer,
    string? SerialNumber)
{
    /// <summary>UI 表示用の名前。</summary>
    public string DisplayName
    {
        get
        {
            foreach (var candidate in new[] { FriendlyName, Description, Manufacturer })
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate.Trim();
                }
            }

            return "MTP デバイス";
        }
    }

    /// <summary>
    /// 再接続をまたいで同じデバイスを指すキー。
    /// WPD の DeviceId は原則安定しているが、長いのでハッシュ化して短くする。
    /// </summary>
    public string StableKey
    {
        get
        {
            var source = !string.IsNullOrWhiteSpace(SerialNumber) ? SerialNumber! : DeviceId;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source.ToLowerInvariant()));
            return Convert.ToHexString(hash, 0, 8);
        }
    }

    /// <summary>
    /// WM_DEVICECHANGE で通知されたデバイスパスが、このデバイスを指しているか。
    ///
    /// 同じ物理デバイスでも通知の種類 (USB / WPD) で末尾のインターフェース GUID が変わり、
    /// 大文字小文字も揃わないため、GUID を外した本体部分だけで突き合わせる。
    /// 例: \\?\usb#vid_1949&amp;pid_9981#SERIAL#{6ac27878-...}
    /// </summary>
    public bool MatchesDevicePath(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return false;
        }

        return string.Equals(CoreIdentity(devicePath), CoreIdentity(DeviceId), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>デバイスパスから接頭辞と末尾のインターフェース GUID を取り除く。</summary>
    private static string CoreIdentity(string value)
    {
        var trimmed = value.Trim().TrimStart('\\', '?', '.');

        var guidStart = trimmed.LastIndexOf('#');
        if (guidStart >= 0 && trimmed.AsSpan(guidStart + 1).TrimStart().StartsWith("{"))
        {
            trimmed = trimmed[..guidStart];
        }

        return trimmed.TrimEnd('\\');
    }

    public bool Equals(KindleDeviceInfo? other) =>
        other is not null && string.Equals(DeviceId, other.DeviceId, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(DeviceId);
}
