using DokanNet;
using KindleMount.Devices;
using KindleMount.FileSystem;
using KindleMount.Logging;
using KindleMount.Mtp;

namespace KindleMount.Mounting;

/// <summary>デバイス 1 台のマウント(接続 → Dokan インスタンス生成 → 解放)を受け持つ。</summary>
public sealed class MountSession : IDisposable
{
    private readonly AppSettings _settings;
    private readonly DriveLetterAllocator _allocator;
    private MtpConnection? _connection;
    private MtpDokanOperations? _operations;
    private DokanInstance? _instance;
    private ContentCache? _cache;
    private int _disposed;

    public MountSession(KindleDeviceInfo device, AppSettings settings, DriveLetterAllocator allocator)
    {
        Device = device;
        _settings = settings;
        _allocator = allocator;
    }

    public KindleDeviceInfo Device { get; }

    public char DriveLetter { get; private set; }

    public string MountPoint => DriveLetter == default ? string.Empty : $"{DriveLetter}:\\";

    public bool IsMounted { get; private set; }

    /// <summary>デバイス喪失やドライバ側の停止で使えなくなったときに発火する。</summary>
    public event Action<MountSession>? Faulted;

    /// <summary>書き込んだ内容をデバイスへ反映できなかったときに発火する(パス, 理由)。</summary>
    public event Action<MountSession, string, Exception>? WriteBackFailed;

    /// <summary>
    /// エクスプローラーの「取り出し」など、アプリ以外の要因でアンマウントされたときに発火する。
    /// </summary>
    public event Action<MountSession>? Ejected;

    /// <summary>マウントを実行する。失敗した場合は例外を投げる。</summary>
    public void Mount()
    {
        var letter = _allocator.Allocate(_settings.PreferredDriveLetters)
                     ?? throw new InvalidOperationException("空きドライブレターがありません。");
        DriveLetter = letter;

        try
        {
            _connection = new MtpConnection(Device.DeviceId, _settings.ReadOnly);
            _connection.DeviceLost += OnDeviceLost;
            _connection.Open();

            var mapper = new MtpPathMapper();
            mapper.Refresh(_connection);
            if (mapper.Storages.Count == 0)
            {
                throw new InvalidOperationException(
                    "デバイス上にストレージが見つかりません。Kindle がロック解除されているか確認してください。");
            }

            var store = new MtpFileStore(_connection, mapper, TimeSpan.FromSeconds(_settings.DirectoryCacheSeconds));

            var cacheDirectory = Path.Combine(AppPaths.CacheDirectory, Device.StableKey);
            _cache = new ContentCache(cacheDirectory, (long)_settings.FileCacheLimitMegabytes * 1024 * 1024);
            _cache.CleanupScratch();
            _cache.Trim();

            var volumeLabel = _settings.ResolveVolumeLabel(Device.DisplayName);
            _operations = new MtpDokanOperations(store, _cache, volumeLabel, _settings.ReadOnly);
            _operations.DeviceLost += OnDeviceLost;
            _operations.WriteBackFailed += (path, ex) => WriteBackFailed?.Invoke(this, path, ex);
            _operations.VolumeUnmounted += OnVolumeUnmounted;

            // マウントマネージャーはシステム全体にボリュームを登録するため、
            // 現在のセッションに限定する CurrentSession とは併用しない。
            var options = _settings.UseMountManager
                ? DokanOptions.MountManager
                : DokanOptions.CurrentSession;

            if (_settings.RemovableDrive)
            {
                options |= DokanOptions.RemovableDrive;
            }

            if (_settings.ReadOnly)
            {
                options |= DokanOptions.WriteProtection;
            }

            if (_settings.VerboseLogging)
            {
                options |= DokanOptions.DebugMode;
            }

            var mountPoint = $"{DriveLetter}:\\";
            Log.Info($"{Device.DisplayName} を {mountPoint} にマウントします。");

            var builder = new DokanInstanceBuilder(DokanRuntime.Instance)
                .ConfigureOptions(o =>
                {
                    o.Options = options;
                    o.MountPoint = mountPoint;
                    o.TimeOut = TimeSpan.FromSeconds(_settings.DokanTimeoutSeconds);
                    o.SingleThread = false;
                });

            _instance = builder.Build(_operations);

            WaitForMount(mountPoint);
            IsMounted = true;
        }
        catch
        {
            CleanupAfterFailure();
            throw;
        }
    }

    /// <summary>Dokan の Build は非同期なので、ドライブが見えるまで少し待つ。</summary>
    private void WaitForMount(string mountPoint)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (_instance is null || !_instance.IsFileSystemRunning())
            {
                throw new InvalidOperationException("Dokan ファイルシステムの起動に失敗しました。");
            }

            try
            {
                if (Directory.Exists(mountPoint))
                {
                    return;
                }
            }
            catch (IOException)
            {
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException($"{mountPoint} のマウントがタイムアウトしました。");
    }

    /// <summary>
    /// Dokan 側でボリュームが切り離された。エクスプローラーからの「取り出し」がこれにあたる。
    /// 自分で Dispose() したときも同じ経路を通るので、その場合は何もしない。
    /// </summary>
    private void OnVolumeUnmounted()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        IsMounted = false;
        Log.Info($"{Device.DisplayName} ({MountPoint}) がエクスプローラー側から取り出されました。");
        Ejected?.Invoke(this);
    }

    private void OnDeviceLost()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Log.Info($"{Device.DisplayName} ({MountPoint}) が切断されました。");
        Faulted?.Invoke(this);
    }

    private void CleanupAfterFailure()
    {
        try
        {
            _instance?.Dispose();
        }
        catch (Exception)
        {
        }

        _instance = null;

        _operations?.CloseAllHandles();
        _operations = null;

        _connection?.Dispose();
        _connection = null;

        if (DriveLetter != default)
        {
            _allocator.Release(DriveLetter);
            DriveLetter = default;
        }

        IsMounted = false;
    }

    /// <summary>アンマウントして資源を解放する。Dokan の停止を待つのでバックグラウンドで呼ぶこと。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var mountPoint = MountPoint;
        Log.Info($"{Device.DisplayName} ({mountPoint}) をアンマウントします。");

        try
        {
            _instance?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug($"Dokan インスタンスの解放で例外: {ex.Message}");
        }

        _instance = null;

        try
        {
            _operations?.CloseAllHandles();
        }
        catch (Exception ex)
        {
            Log.Debug($"ハンドルの後始末で例外: {ex.Message}");
        }

        _operations = null;

        _connection?.Dispose();
        _connection = null;

        _cache?.CleanupScratch();
        _cache?.Trim();
        _cache = null;

        if (DriveLetter != default)
        {
            _allocator.Release(DriveLetter);
        }

        IsMounted = false;
    }
}
