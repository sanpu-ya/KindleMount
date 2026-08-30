using DokanNet;
using KindleMount.Logging;

namespace KindleMount.Mounting;

/// <summary>Dokan ドライバの導入状況を確認する。未導入なら案内を出すための情報を返す。</summary>
public static class DokanRuntime
{
    public const string DownloadUrl = "https://github.com/dokan-dev/dokany/releases";

    private static readonly object Gate = new();
    private static Dokan? _instance;

    /// <summary>プロセス全体で共有する Dokan ハンドル。</summary>
    public static Dokan Instance
    {
        get
        {
            lock (Gate)
            {
                return _instance ??= new Dokan(new DokanLoggerAdapter());
            }
        }
    }

    /// <summary>ドライバが使えるかどうか。使えない場合は理由を返す。</summary>
    public static bool TryProbe(out string message)
    {
        try
        {
            var dokan = Instance;
            var version = dokan.Version;
            var driver = dokan.DriverVersion;
            message = $"Dokan ライブラリ {version} / ドライバ {driver}";
            Log.Info(message);
            return true;
        }
        catch (DllNotFoundException)
        {
            message = "Dokan ライブラリ (dokan2.dll) が見つかりません。Dokany のインストールが必要です。";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            message = "Dokan ライブラリのバージョンが古い可能性があります。Dokany 2.x をインストールしてください。";
            return false;
        }
        catch (Exception ex)
        {
            message = $"Dokan ドライバを初期化できません: {ex.Message}";
            return false;
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            try
            {
                _instance?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug($"Dokan の終了処理で例外: {ex.Message}");
            }

            _instance = null;
        }
    }
}
