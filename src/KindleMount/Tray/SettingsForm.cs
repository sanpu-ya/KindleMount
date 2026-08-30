namespace KindleMount.Tray;

/// <summary>設定ダイアログ。デザイナを使わずコードで組む(単一ファイルで完結させるため)。</summary>
public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;

    private readonly CheckBox _autoMount = new() { Text = "USB 接続時に自動でマウントする", AutoSize = true };
    private readonly CheckBox _openExplorer = new() { Text = "マウント後にエクスプローラーで開く", AutoSize = true };
    private readonly CheckBox _notifications = new() { Text = "マウント/取り外しを通知する", AutoSize = true };
    private readonly CheckBox _readOnly = new() { Text = "読み取り専用でマウントする", AutoSize = true };
    private readonly CheckBox _removable = new() { Text = "リムーバブルドライブとして扱う", AutoSize = true };
    private readonly CheckBox _mountManager = new()
    {
        Text = "マウントマネージャー経由でマウントする (OS との統合が良くなる)",
        AutoSize = true,
    };
    private readonly CheckBox _anyMtp = new() { Text = "Kindle 以外の MTP デバイスも対象にする", AutoSize = true };
    private readonly CheckBox _verbose = new() { Text = "詳細ログを記録する", AutoSize = true };

    private readonly TextBox _driveLetters = new() { Width = 200 };
    private readonly TextBox _volumeLabel = new() { Width = 200, MaxLength = 32, PlaceholderText = "(空ならデバイス名)" };
    private readonly NumericUpDown _cacheLimit = new() { Minimum = 0, Maximum = 1_000_000, Increment = 256, Width = 100 };
    private readonly NumericUpDown _dirCache = new() { Minimum = 0, Maximum = 600, Width = 100 };
    private readonly NumericUpDown _pollInterval = new() { Minimum = 0, Maximum = 3600, Width = 100 };

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;

        Text = $"{AppInfo.NameWithVersion} の設定";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(460, 470);
        Icon = TrayIcons.Active;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(16),
            AutoSize = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddFullWidth(Control control)
        {
            layout.Controls.Add(control);
            layout.SetColumnSpan(control, 2);
        }

        void AddLabeled(string label, Control control)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) });
            layout.Controls.Add(control);
        }

        AddFullWidth(SectionLabel("動作"));
        AddFullWidth(_autoMount);
        AddFullWidth(_openExplorer);
        AddFullWidth(_notifications);
        AddFullWidth(_readOnly);
        AddFullWidth(_removable);
        AddFullWidth(_mountManager);
        AddFullWidth(_anyMtp);

        AddFullWidth(SectionLabel("ドライブ"));
        AddLabeled("希望するドライブレター", _driveLetters);
        AddLabeled("ボリュームラベル", _volumeLabel);

        AddFullWidth(SectionLabel("性能"));
        AddLabeled("ファイルキャッシュ上限 (MB)", _cacheLimit);
        AddLabeled("フォルダ一覧のキャッシュ (秒)", _dirCache);
        AddLabeled("定期スキャン間隔 (秒 / 0 で無効)", _pollInterval);

        AddFullWidth(SectionLabel("その他"));
        AddFullWidth(_verbose);

        var note = new Label
        {
            Text = "変更は次回マウント時から反映されます。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 12, 3, 3),
        };
        AddFullWidth(note);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Width = 90 };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = 46,
            Padding = new Padding(16, 8, 16, 8),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        ok.Click += (_, _) => Apply();

        LoadValues();
    }

    private static Label SectionLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        Margin = new Padding(3, 12, 3, 4),
    };

    private void LoadValues()
    {
        _autoMount.Checked = _settings.AutoMount;
        _openExplorer.Checked = _settings.OpenExplorerOnMount;
        _notifications.Checked = _settings.ShowNotifications;
        _readOnly.Checked = _settings.ReadOnly;
        _removable.Checked = _settings.RemovableDrive;
        _mountManager.Checked = _settings.UseMountManager;
        _anyMtp.Checked = _settings.AllowAnyMtpDevice;
        _verbose.Checked = _settings.VerboseLogging;
        _driveLetters.Text = _settings.PreferredDriveLetters;
        _volumeLabel.Text = _settings.VolumeLabel;
        _cacheLimit.Value = Math.Clamp(_settings.FileCacheLimitMegabytes, 0, 1_000_000);
        _dirCache.Value = Math.Clamp(_settings.DirectoryCacheSeconds, 0, 600);
        _pollInterval.Value = Math.Clamp(_settings.PollIntervalSeconds, 0, 3600);
    }

    private void Apply()
    {
        _settings.AutoMount = _autoMount.Checked;
        _settings.OpenExplorerOnMount = _openExplorer.Checked;
        _settings.ShowNotifications = _notifications.Checked;
        _settings.ReadOnly = _readOnly.Checked;
        _settings.RemovableDrive = _removable.Checked;
        _settings.UseMountManager = _mountManager.Checked;
        _settings.AllowAnyMtpDevice = _anyMtp.Checked;
        _settings.VerboseLogging = _verbose.Checked;
        _settings.PreferredDriveLetters = _driveLetters.Text;
        _settings.VolumeLabel = _volumeLabel.Text.Trim();
        _settings.FileCacheLimitMegabytes = (int)_cacheLimit.Value;
        _settings.DirectoryCacheSeconds = (int)_dirCache.Value;
        _settings.PollIntervalSeconds = (int)_pollInterval.Value;
    }
}
