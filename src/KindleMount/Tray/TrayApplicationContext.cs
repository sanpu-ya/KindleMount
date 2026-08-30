using KindleMount.Devices;
using KindleMount.Logging;
using KindleMount.Mounting;
using System.Diagnostics;

namespace KindleMount.Tray;

/// <summary>タスクトレイ常駐の本体。メニューの構築と MountService の橋渡しを行う。</summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly MountService _service;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private bool _shuttingDown;

    public TrayApplicationContext(AppSettings settings)
    {
        _settings = settings;

        _menu = new ContextMenuStrip { ShowImageMargin = false };
        _menu.Opening += (_, _) => BuildMenu();

        _notifyIcon = new NotifyIcon
        {
            Icon = TrayIcons.Idle,
            Text = "KindleMount",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenFirstMount();

        _service = new MountService(settings);
        _service.StateChanged += OnStateChanged;
        _service.Notification += OnNotification;
        _service.Start();

        BuildMenu();
        UpdateIcon();
    }

    private void OnStateChanged()
    {
        UpdateIcon();
        BuildMenu();
    }

    private void OnNotification(string title, string message, bool isError)
    {
        if (!_settings.ShowNotifications || _shuttingDown)
        {
            return;
        }

        _notifyIcon.ShowBalloonTip(
            isError ? 8000 : 4000,
            title,
            message,
            isError ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private void UpdateIcon()
    {
        var sessions = _service.Sessions;

        _notifyIcon.Icon = _service.IsBusy
            ? TrayIcons.Busy
            : sessions.Count > 0
                ? TrayIcons.Active
                : TrayIcons.Idle;

        var text = sessions.Count switch
        {
            0 => "KindleMount - 未接続",
            1 => $"KindleMount - {sessions[0].Device.DisplayName} ({sessions[0].MountPoint})",
            _ => $"KindleMount - {sessions.Count} 台をマウント中",
        };

        // NotifyIcon.Text は 63 文字までしか受け付けない。
        _notifyIcon.Text = text.Length <= 63 ? text : text[..60] + "...";
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();

        var sessions = _service.Sessions;
        var mountedIds = sessions.Select(s => s.Device.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (sessions.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem("マウント中のデバイスはありません") { Enabled = false });
        }
        else
        {
            foreach (var session in sessions)
            {
                var item = new ToolStripMenuItem($"{session.MountPoint}  {session.Device.DisplayName}");
                item.DropDownItems.Add("開く", null, (_, _) => OpenPath(session.MountPoint));
                item.DropDownItems.Add("安全に取り外す", null, (_, _) => _ = _service.UnmountAsync(session));
                _menu.Items.Add(item);
            }
        }

        var pending = _service.DetectedDevices.Where(d => !mountedIds.Contains(d.DeviceId)).ToList();
        if (pending.Count > 0)
        {
            _menu.Items.Add(new ToolStripSeparator());
            foreach (var device in pending)
            {
                _menu.Items.Add(
                    new ToolStripMenuItem($"マウント: {device.DisplayName}", null, (_, _) => MountDevice(device)));
            }
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("デバイスを再検索", null, (_, _) => _service.RequestScan("手動"));

        var autoMount = new ToolStripMenuItem("自動マウント") { Checked = _settings.AutoMount, CheckOnClick = true };
        autoMount.Click += (_, _) =>
        {
            _settings.AutoMount = autoMount.Checked;
            _settings.Save();
            if (_settings.AutoMount)
            {
                _service.RequestScan("自動マウント有効化");
            }
        };
        _menu.Items.Add(autoMount);

        _menu.Items.Add("設定...", null, (_, _) => ShowSettings());
        _menu.Items.Add("ログを開く", null, (_, _) => OpenPath(Log.FilePath ?? AppPaths.LogDirectory));

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(AppInfo.NameWithVersion) { Enabled = false });
        _menu.Items.Add("終了", null, (_, _) => _ = ShutdownAsync());
    }

    private void MountDevice(KindleDeviceInfo device)
    {
        _ = _service.MountAsync(device);
    }

    private void OpenFirstMount()
    {
        var session = _service.Sessions.FirstOrDefault();
        if (session is not null)
        {
            OpenPath(session.MountPoint);
        }
        else
        {
            _service.RequestScan("トレイのダブルクリック");
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"{path} を開けませんでした: {ex.Message}");
        }
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog() == DialogResult.OK)
        {
            _settings.Save();
            Log.MinimumLevel = _settings.VerboseLogging ? LogLevel.Debug : LogLevel.Info;
            _service.RequestScan("設定変更");
        }
    }

    private async Task ShutdownAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        _notifyIcon.Icon = TrayIcons.Busy;
        _notifyIcon.Text = "KindleMount - 終了処理中";

        try
        {
            await _service.UnmountAllAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error("終了時のアンマウントで例外", ex);
        }

        _notifyIcon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _service.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            DokanRuntime.Shutdown();
        }

        base.Dispose(disposing);
    }
}
