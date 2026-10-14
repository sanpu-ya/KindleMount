using KindleMount.Logging;
using Microsoft.Win32;

namespace KindleMount.Startup;

/// <summary>
/// Windows サインイン時の自動起動 (スタートアップ) 登録。
/// 管理者権限が不要で、スタートアップフォルダのショートカットより壊れにくい
/// HKCU の Run キーを使う。登録の有無はレジストリが唯一の情報源で、
/// settings.json には持たせない (手動で消されたときに食い違うため)。
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Run キーの値名。アプリ名と揃えておく。</summary>
    public const string ValueName = AppInfo.Name;

    /// <summary>登録に使う実行ファイルのパス。解決できない場合は null。</summary>
    public static string? ExecutablePath { get; } = ResolveExecutablePath();

    /// <summary>この環境で自動起動の登録ができるか (実行ファイルを特定できたか)。</summary>
    public static bool IsSupported => ExecutablePath is not null;

    /// <summary>Run キーに登録済みかどうか。パスの一致は見ない (<see cref="Synchronize"/> が直す)。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"スタートアップ登録の確認に失敗しました: {ex.Message}");
            return false;
        }
    }

    /// <summary>自動起動を登録する / 解除する。成功したら true。</summary>
    public static bool SetEnabled(bool enabled) => enabled ? Enable() : Disable();

    public static bool Enable()
    {
        if (ExecutablePath is not { } exe)
        {
            Log.Warn("実行ファイルのパスを特定できないため、スタートアップに登録できません。");
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                Log.Warn("Run キーを開けないため、スタートアップに登録できません。");
                return false;
            }

            key.SetValue(ValueName, FormatCommand(exe), RegistryValueKind.String);
            Log.Info($"スタートアップに登録しました: {exe}");
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"スタートアップへの登録に失敗しました: {ex.Message}");
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                // Run キー自体が無ければ、そもそも登録されていない。
                return true;
            }

            if (key.GetValue(ValueName) is null)
            {
                return true;
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Info("スタートアップ登録を解除しました。");
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"スタートアップ登録の解除に失敗しました: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 登録済みのコマンドを現在の実行ファイルのパスへ合わせ直す。
    /// 別フォルダへ移動したり更新で置き換えたりしても自動起動が死なないよう、起動時に呼ぶ。
    /// 未登録のときは何もしない (勝手に登録しない)。
    /// </summary>
    public static void Synchronize()
    {
        if (ExecutablePath is not { } exe)
        {
            return;
        }

        string? current;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            current = key?.GetValue(ValueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"スタートアップ登録の確認に失敗しました: {ex.Message}");
            return;
        }

        if (current is null)
        {
            return;
        }

        var registered = PathFromCommand(current);
        if (string.Equals(registered, exe, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Log.Info($"スタートアップ登録のパスが古いため更新します: {registered} → {exe}");
        Enable();
    }

    /// <summary>Run キーに書き込むコマンド文字列。空白を含むパスのために必ず引用符で囲む。</summary>
    public static string FormatCommand(string executablePath) => $"\"{executablePath}\"";

    /// <summary>Run キーのコマンド文字列から実行ファイルのパスだけを取り出す。</summary>
    public static string PathFromCommand(string? command)
    {
        var value = (command ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (value[0] == '"')
        {
            var close = value.IndexOf('"', 1);
            return close > 0 ? value[1..close] : value[1..];
        }

        var space = value.IndexOf(' ');
        return space > 0 ? value[..space] : value;
    }

    /// <summary>
    /// 自動起動に書き込む exe を決める。
    /// 通常は apphost (KindleMount.exe) 自身。`dotnet KindleMount.dll` のように
    /// 起動された場合は、dotnet.exe を登録しても意味がないので隣の apphost を探す。
    /// </summary>
    private static string? ResolveExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path) &&
            !string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var assembly = typeof(StartupRegistration).Assembly.Location;
        if (string.IsNullOrEmpty(assembly))
        {
            return null;
        }

        var candidate = Path.ChangeExtension(assembly, ".exe");
        return File.Exists(candidate) ? candidate : null;
    }
}
