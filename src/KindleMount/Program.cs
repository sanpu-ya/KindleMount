using KindleMount.Logging;
using KindleMount.Mounting;
using KindleMount.Tray;
using System.Diagnostics;

namespace KindleMount;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\KindleMount.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        AppPaths.EnsureCreated();

        if (args.Any(a => a is "--scan" or "-s" or "/scan"))
        {
            // 診断モード: 常駐せずデバイス検出結果だけを出力する。
            var diagnosticSettings = AppSettings.Load(AppPaths.SettingsFile);
            Log.MinimumLevel = LogLevel.Warn;
            return Diagnostics.ConsoleDiagnostics.Run(diagnosticSettings);
        }

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "KindleMount は既に起動しています。タスクトレイのアイコンを確認してください。",
                "KindleMount",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        Log.Initialize(AppPaths.LogDirectory);

        var settings = AppSettings.Load(AppPaths.SettingsFile);
        Log.MinimumLevel = settings.VerboseLogging ? LogLevel.Debug : LogLevel.Info;

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("UI スレッドで未処理例外", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log.Error("未処理例外", ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("未観測のタスク例外", e.Exception);
            e.SetObserved();
        };

        if (!DokanRuntime.TryProbe(out var probeMessage))
        {
            Log.Error(probeMessage);
            ShowDokanMissingDialog(probeMessage);
            return 1;
        }

        try
        {
            using var context = new TrayApplicationContext(settings);
            Application.Run(context);
        }
        catch (Exception ex)
        {
            Log.Error("致命的なエラーで終了します", ex);
            MessageBox.Show(
                $"KindleMount を継続できません。\n\n{ex.Message}\n\nログ: {Log.FilePath}",
                "KindleMount",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            DokanRuntime.Shutdown();
            Log.Info("--- 終了 ---");
        }

        return 0;
    }

    private static void ShowDokanMissingDialog(string reason)
    {
        var result = MessageBox.Show(
            $"""
             MTP デバイスをドライブとしてマウントするには Dokany ドライバが必要です。

             {reason}

             Dokany 2.x のインストーラー配布ページを開きますか?
             """,
            "KindleMount - Dokany が必要です",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result == DialogResult.Yes)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = DokanRuntime.DownloadUrl,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"ブラウザーを開けませんでした: {ex.Message}");
            }
        }
    }
}
