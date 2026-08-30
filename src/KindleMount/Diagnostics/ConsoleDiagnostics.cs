using KindleMount.Devices;
using KindleMount.Mounting;
using KindleMount.Mtp;
using MediaDevices;
using System.Runtime.InteropServices;

namespace KindleMount.Diagnostics;

/// <summary>
/// `KindleMount.exe --scan` 用の診断出力。GUI アプリなので親コンソールへ手動で接続する。
/// デバイスが認識されないときの切り分けに使う。
/// </summary>
public static class ConsoleDiagnostics
{
    private const int AttachParentProcess = -1;

    public static int Run(AppSettings settings)
    {
        AttachToParentConsole();

        Console.WriteLine();
        var heading = $"{AppInfo.NameWithVersion} 診断";
        Console.WriteLine(heading);
        Console.WriteLine(new string('=', heading.Length));
        Console.WriteLine($"設定ファイル : {AppPaths.SettingsFile}");
        Console.WriteLine($"ログ         : {AppPaths.LogDirectory}");
        Console.WriteLine($"キャッシュ   : {AppPaths.CacheDirectory}");
        Console.WriteLine();

        Console.WriteLine(DokanRuntime.TryProbe(out var dokanMessage)
            ? $"[OK]   {dokanMessage}"
            : $"[NG]   {dokanMessage}");
        Console.WriteLine();

        Console.WriteLine("接続中の MTP / WPD デバイス:");
        var scanner = new KindleScanner(settings);
        var kindles = scanner.Scan();

        var all = SafeEnumerate();
        if (all.Count == 0)
        {
            Console.WriteLine("  (見つかりません)");
        }

        foreach (var device in all)
        {
            var isTarget = kindles.Any(k => string.Equals(k.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"  [{(isTarget ? "対象" : "対象外")}] {device.FriendlyName}");
            Console.WriteLine($"           説明     : {device.Description}");
            Console.WriteLine($"           製造元   : {device.Manufacturer}");
            Console.WriteLine($"           DeviceId : {device.DeviceId}");

            if (isTarget)
            {
                DumpStorages(device.DeviceId);
            }

            Console.WriteLine();
        }

        Console.WriteLine(kindles.Count > 0
            ? $"対象デバイス {kindles.Count} 台を検出しました。"
            : "対象デバイスは見つかりませんでした。Kindle を USB 接続し、画面のロックを解除してください。");
        Console.WriteLine();

        DokanRuntime.Shutdown();
        return kindles.Count > 0 ? 0 : 2;
    }

    private static void DumpStorages(string deviceId)
    {
        try
        {
            using var connection = new MtpConnection(deviceId, readOnly: true);
            connection.Open();

            var mapper = new MtpPathMapper();
            mapper.Refresh(connection);

            foreach (var storage in mapper.Storages)
            {
                Console.WriteLine($"           ストレージ: {storage.Name}  ({storage.MtpPath})");
            }

            var store = new MtpFileStore(connection, mapper, TimeSpan.Zero);
            var (total, free) = store.GetSpace();
            if (total > 0)
            {
                Console.WriteLine($"           容量     : {free / 1024.0 / 1024:N0} MB 空き / {total / 1024.0 / 1024:N0} MB");
            }

            var root = store.ListDirectory("\\");
            if (root is not null)
            {
                var names = root.Take(8).Select(e => e.IsDirectory ? e.Name + "\\" : e.Name);
                Console.WriteLine($"           ルート   : {string.Join(", ", names)}{(root.Count > 8 ? " ..." : string.Empty)}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"           !! 接続できません: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static List<KindleDeviceInfo> SafeEnumerate()
    {
        var list = new List<KindleDeviceInfo>();
        try
        {
            foreach (var device in MediaDeviceManager.Instance.GetDevices() ?? Enumerable.Empty<MediaDevice>())
            {
                try
                {
                    list.Add(new KindleDeviceInfo(
                        device.DeviceId,
                        Safe(() => device.FriendlyName),
                        Safe(() => device.Description),
                        Safe(() => device.Manufacturer),
                        null));
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  !! 列挙に失敗しました: {ex.Message}");
        }

        return list;
    }

    private static string Safe(Func<string?> getter)
    {
        try
        {
            return getter() ?? string.Empty;
        }
        catch (Exception)
        {
            return "(取得できません)";
        }
    }

    private static void AttachToParentConsole()
    {
        if (!AttachConsole(AttachParentProcess))
        {
            return;
        }

        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
