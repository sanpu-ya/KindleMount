using KindleMount.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KindleMount;

/// <summary>
/// %APPDATA%\KindleMount\settings.json に永続化される設定。
/// 壊れた JSON でも起動できるよう、読み込み失敗時は既定値へフォールバックする。
/// </summary>
public sealed class AppSettings
{
    /// <summary>希望するドライブレター。使用中なら空きレターへ自動フォールバックする。</summary>
    public string PreferredDriveLetters { get; set; } = "KLMNOPQRSTUVWXYZ";

    /// <summary>USB 接続を検知したら自動でマウントする。</summary>
    public bool AutoMount { get; set; } = true;

    /// <summary>マウント完了時にエクスプローラーを開く。</summary>
    public bool OpenExplorerOnMount { get; set; }

    /// <summary>マウント/取り外しをバルーン通知する。</summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>読み取り専用でマウントする(誤書き込み防止)。</summary>
    public bool ReadOnly { get; set; }

    /// <summary>ドライブを「リムーバブルドライブ」として見せる。</summary>
    public bool RemovableDrive { get; set; } = true;

    /// <summary>
    /// Windows のマウントマネージャー経由でマウントする。
    /// エクスプローラーの「取り出し」やボリューム一覧など OS 側との統合が良くなるが、
    /// 環境によっては管理者権限が要る。使用中のレターは OS が別のものへ振り直す。
    /// </summary>
    public bool UseMountManager { get; set; }

    /// <summary>
    /// ドライブに表示するボリュームラベル。空ならデバイス名 (例: "Kindle Paperwhite Signature Edition")
    /// をそのまま使う。"Kindle" のように短い固定名を付けたい場合に指定する。
    /// </summary>
    public string VolumeLabel { get; set; } = string.Empty;

    /// <summary>ディレクトリ一覧のキャッシュ有効期間(秒)。MTP は列挙が遅いので既定 15 秒。</summary>
    public int DirectoryCacheSeconds { get; set; } = 15;

    /// <summary>ファイル内容ローカルキャッシュの上限(MB)。0 で無制限。</summary>
    public int FileCacheLimitMegabytes { get; set; } = 4096;

    /// <summary>Dokan の I/O タイムアウト(秒)。大きなファイル転送に備えて長めに取る。</summary>
    public int DokanTimeoutSeconds { get; set; } = 300;

    /// <summary>USB 変化イベントに加えて行う定期スキャンの間隔(秒)。0 で無効。</summary>
    public int PollIntervalSeconds { get; set; } = 15;

    /// <summary>Kindle 判定に追加で使う名前パターン(部分一致・大文字小文字無視)。</summary>
    public List<string> ExtraDeviceNamePatterns { get; set; } = new();

    /// <summary>true なら Kindle 以外の MTP デバイスも対象にする。</summary>
    public bool AllowAnyMtpDevice { get; set; }

    public bool VerboseLogging { get; set; }

    [JsonIgnore]
    public string Path { get; private set; } = string.Empty;

    /// <summary>
    /// 実際に使うボリュームラベルを決める。
    /// <see cref="VolumeLabel"/> が空ならデバイス名へフォールバックする。
    /// </summary>
    public string ResolveVolumeLabel(string deviceName) =>
        string.IsNullOrWhiteSpace(VolumeLabel) ? deviceName : VolumeLabel.Trim();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppSettings Load(string path)
    {
        AppSettings settings;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            else
            {
                settings = new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"設定の読み込みに失敗したため既定値を使用します: {ex.Message}");
            settings = new AppSettings();
        }

        settings.Path = path;
        settings.Normalize();
        return settings;
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(Path))
        {
            Log.Warn("設定の保存先パスが未設定です。");
            return;
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"設定の保存に失敗しました: {ex.Message}");
        }
    }

    private void Normalize()
    {
        var letters = new string((PreferredDriveLetters ?? string.Empty)
            .ToUpperInvariant()
            .Where(c => c is >= 'D' and <= 'Z')
            .Distinct()
            .ToArray());

        PreferredDriveLetters = letters.Length > 0 ? letters : "KLMNOPQRSTUVWXYZ";
        DirectoryCacheSeconds = Math.Clamp(DirectoryCacheSeconds, 0, 600);
        DokanTimeoutSeconds = Math.Clamp(DokanTimeoutSeconds, 30, 3600);
        PollIntervalSeconds = PollIntervalSeconds <= 0 ? 0 : Math.Clamp(PollIntervalSeconds, 3, 3600);
        FileCacheLimitMegabytes = Math.Max(0, FileCacheLimitMegabytes);
        ExtraDeviceNamePatterns ??= new List<string>();
    }
}
