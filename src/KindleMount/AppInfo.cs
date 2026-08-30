using System.Reflection;

namespace KindleMount;

/// <summary>アプリ名とバージョンの表示用文字列。</summary>
public static class AppInfo
{
    public const string Name = "KindleMount";

    /// <summary>"v0.1" 形式の表示用バージョン。</summary>
    public static string DisplayVersion { get; } = FormatVersion(ReadAssemblyVersion());

    /// <summary>"KindleMount v0.1"。ウィンドウのタイトルやログの見出しに使う。</summary>
    public static string NameWithVersion { get; } = $"{Name} {DisplayVersion}";

    /// <summary>
    /// アセンブリのバージョン文字列を表示用に整える。
    /// ビルドメタデータ (+ 以降) と、末尾に並ぶ意味のない .0 を落として "v" を付ける。
    /// </summary>
    public static string FormatVersion(string? raw)
    {
        var version = (raw ?? string.Empty).Trim();
        if (version.Length == 0)
        {
            return "v0.0";
        }

        var metadata = version.IndexOf('+');
        if (metadata >= 0)
        {
            version = version[..metadata];
        }

        // 0.1.0.0 → 0.1。ただし 1.2.3 のように意味のある桁は残す。
        var parts = version.Split('.');
        var keep = parts.Length;
        while (keep > 2 && parts[keep - 1] == "0")
        {
            keep--;
        }

        version = string.Join('.', parts.Take(keep));

        return version.StartsWith('v') ? version : "v" + version;
    }

    private static string? ReadAssemblyVersion()
    {
        var assembly = typeof(AppInfo).Assembly;

        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString();
    }
}
