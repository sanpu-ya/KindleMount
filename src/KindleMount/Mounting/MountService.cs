using KindleMount.Devices;
using KindleMount.Logging;

namespace KindleMount.Mounting;

/// <summary>
/// デバイス検知とマウント状態を突き合わせる中核。
///
/// WPD/Dokan の呼び出しはすべてスレッドプール(MTA)上で行い、UI へは
/// <see cref="SynchronizationContext"/> 経由で戻す。
/// </summary>
public sealed class MountService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly KindleScanner _scanner;
    private readonly DriveLetterAllocator _allocator = new();
    private readonly DeviceChangeWatcher _watcher;
    private readonly System.Windows.Forms.Timer? _pollTimer;
    private readonly SynchronizationContext _ui;

    private readonly Dictionary<string, MountSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoMountAttempted = new(StringComparer.OrdinalIgnoreCase);
    private List<KindleDeviceInfo> _detected = new();

    private int _scanScheduled;
    private bool _disposed;

    public MountService(AppSettings settings)
    {
        _settings = settings;
        _scanner = new KindleScanner(settings);
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        _watcher = new DeviceChangeWatcher();
        _watcher.DevicesChanged += () => RequestScan("USB イベント");
        _watcher.DeviceRemovalRequested += OnDeviceRemovalRequested;

        if (settings.PollIntervalSeconds > 0)
        {
            _pollTimer = new System.Windows.Forms.Timer { Interval = settings.PollIntervalSeconds * 1000 };
            _pollTimer.Tick += (_, _) => RequestScan("定期スキャン");
        }
    }

    /// <summary>マウント一覧や検出状況が変わったときに UI スレッドで発火する。</summary>
    public event Action? StateChanged;

    /// <summary>ユーザーへ知らせたい出来事(バルーン通知用)。</summary>
    public event Action<string, string, bool>? Notification;

    public IReadOnlyList<MountSession> Sessions
    {
        get
        {
            lock (_sessions)
            {
                return _sessions.Values.ToList();
            }
        }
    }

    public IReadOnlyList<KindleDeviceInfo> DetectedDevices => _detected;

    public bool IsBusy
    {
        get
        {
            lock (_sessions)
            {
                return _busy.Count > 0;
            }
        }
    }

    public void Start()
    {
        _pollTimer?.Start();
        RequestScan("起動時スキャン");
    }

    /// <summary>スキャンを 1 回だけ予約する(多重実行を防ぐ)。</summary>
    public void RequestScan(string reason)
    {
        if (Interlocked.Exchange(ref _scanScheduled, 1) == 1)
        {
            return;
        }

        Log.Debug($"デバイススキャンを開始します ({reason})");

        Task.Run(() =>
        {
            try
            {
                var devices = _scanner.Scan();
                Post(() => Reconcile(devices));
            }
            catch (Exception ex)
            {
                Log.Error("デバイススキャンに失敗しました", ex);
            }
            finally
            {
                Volatile.Write(ref _scanScheduled, 0);
            }
        });
    }

    /// <summary>検出結果と現在のマウント状態を突き合わせる。UI スレッドで呼ばれる。</summary>
    private void Reconcile(IReadOnlyList<KindleDeviceInfo> devices)
    {
        if (_disposed)
        {
            return;
        }

        _detected = devices.ToList();

        var present = new HashSet<string>(devices.Select(d => d.DeviceId), StringComparer.OrdinalIgnoreCase);

        // 消えたデバイスのマウントを解除する。
        List<MountSession> stale;
        lock (_sessions)
        {
            stale = _sessions.Values.Where(s => !present.Contains(s.Device.DeviceId)).ToList();
        }

        foreach (var session in stale)
        {
            Log.Info($"{session.Device.DisplayName} が取り外されました。");
            _ = UnmountAsync(session, notify: true);
        }

        // 自動マウントの対象を探す。
        if (_settings.AutoMount)
        {
            foreach (var device in devices)
            {
                lock (_sessions)
                {
                    if (_sessions.ContainsKey(device.DeviceId) ||
                        _busy.Contains(device.DeviceId) ||
                        !_autoMountAttempted.Add(device.DeviceId))
                    {
                        continue;
                    }
                }

                _ = MountAsync(device);
            }
        }

        // 取り外されたデバイスは再挿入時にまた自動マウントできるようにする。
        lock (_sessions)
        {
            _autoMountAttempted.RemoveWhere(id => !present.Contains(id));
        }

        StateChanged?.Invoke();
    }

    public Task MountAsync(KindleDeviceInfo device)
    {
        lock (_sessions)
        {
            if (_sessions.ContainsKey(device.DeviceId) || !_busy.Add(device.DeviceId))
            {
                return Task.CompletedTask;
            }
        }

        StateChanged?.Invoke();

        return Task.Run(() =>
        {
            var session = new MountSession(device, _settings, _allocator);
            try
            {
                session.Mount();
                session.Faulted += OnSessionFaulted;
                session.WriteBackFailed += OnWriteBackFailed;
                session.Ejected += OnSessionEjected;

                lock (_sessions)
                {
                    _sessions[device.DeviceId] = session;
                    _busy.Remove(device.DeviceId);
                }

                Log.Info($"{device.DisplayName} を {session.MountPoint} にマウントしました。");
                Post(() =>
                {
                    StateChanged?.Invoke();
                    Notification?.Invoke(
                        "Kindle をマウントしました",
                        $"{device.DisplayName} → {session.MountPoint}",
                        false);

                    if (_settings.OpenExplorerOnMount)
                    {
                        OpenInExplorer(session.MountPoint);
                    }
                });
            }
            catch (Exception ex)
            {
                lock (_sessions)
                {
                    _busy.Remove(device.DeviceId);
                }

                session.Dispose();
                Log.Error($"{device.DisplayName} のマウントに失敗しました", ex);

                Post(() =>
                {
                    StateChanged?.Invoke();
                    Notification?.Invoke("マウントに失敗しました", $"{device.DisplayName}: {ex.Message}", true);
                });
            }
        });
    }

    public Task UnmountAsync(MountSession session, bool notify = true, string? notificationTitle = null)
    {
        lock (_sessions)
        {
            if (!_sessions.Remove(session.Device.DeviceId))
            {
                return Task.CompletedTask;
            }

            _busy.Add(session.Device.DeviceId);
        }

        StateChanged?.Invoke();

        var mountPoint = session.MountPoint;
        var name = session.Device.DisplayName;

        return Task.Run(() =>
        {
            try
            {
                session.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error($"{name} のアンマウントで例外", ex);
            }
            finally
            {
                lock (_sessions)
                {
                    _busy.Remove(session.Device.DeviceId);
                }

                Post(() =>
                {
                    StateChanged?.Invoke();
                    if (notify)
                    {
                        Notification?.Invoke(
                            notificationTitle ?? "Kindle を取り外しました",
                            $"{name} ({mountPoint})",
                            false);
                    }
                });
            }
        });
    }

    public async Task UnmountAllAsync()
    {
        List<MountSession> sessions;
        lock (_sessions)
        {
            sessions = _sessions.Values.ToList();
        }

        await Task.WhenAll(sessions.Select(s => UnmountAsync(s, notify: false))).ConfigureAwait(false);
    }

    private void OnSessionFaulted(MountSession session)
    {
        Post(() => _ = UnmountAsync(session, notify: true));
    }

    /// <summary>
    /// エクスプローラーでドライブが取り出された。Dokan 側は既に切り離されているので、
    /// こちら側に残った MTP 接続とドライブレターを解放する。
    /// (デバイス自体は接続されたままなので、再マウントはトレイから手動で行う)
    /// </summary>
    private void OnSessionEjected(MountSession session)
    {
        Post(() => _ = UnmountAsync(session, notify: true, notificationTitle: "Kindle を取り出しました"));
    }

    /// <summary>
    /// OS からデバイス取り外しの予告が届いた。「ハードウェアの安全な取り外し」がこれにあたる。
    /// MTP セッションを掴んだままだと取り外しが拒否されるので、直ちにマウントを解除する。
    /// </summary>
    private void OnDeviceRemovalRequested(string devicePath)
    {
        MountSession? target;
        lock (_sessions)
        {
            target = _sessions.Values.FirstOrDefault(s => s.Device.MatchesDevicePath(devicePath));
        }

        if (target is null)
        {
            return;
        }

        Log.Info($"{target.Device.DisplayName} の取り外し要求を受けたので、マウントを解除します。");
        _ = UnmountAsync(target, notify: true, notificationTitle: "Kindle を取り外しました");
    }


    /// <summary>書き戻しの失敗は黙って捨てるとデータ損失になるので必ず知らせる。</summary>
    private void OnWriteBackFailed(MountSession session, string path, Exception ex)
    {
        Post(() => Notification?.Invoke(
            "デバイスへの書き込みに失敗しました",
            $"{session.MountPoint.TrimEnd('\\')}{path} — {ex.Message}",
            true));
    }

    private static void OpenInExplorer(string mountPoint)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = mountPoint,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"エクスプローラーを開けませんでした: {ex.Message}");
        }
    }

    private void Post(Action action) => _ui.Post(_ => action(), null);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pollTimer?.Stop();
        _pollTimer?.Dispose();
        _watcher.Dispose();

        List<MountSession> sessions;
        lock (_sessions)
        {
            sessions = _sessions.Values.ToList();
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            try
            {
                session.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug($"終了時のアンマウントで例外: {ex.Message}");
            }
        }
    }
}
