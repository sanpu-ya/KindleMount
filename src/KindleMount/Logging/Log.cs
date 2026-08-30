namespace KindleMount.Logging;

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
}

/// <summary>
/// 極小のファイル + メモリロガー。トレイ UI からも直近ログを読めるようにしている。
/// Dokan のコールバックから高頻度で呼ばれるため、ロックは短く保つ。
/// </summary>
public static class Log
{
    private const int MaxFileBytes = 2 * 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _filePath;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static string? FilePath => _filePath;

    public static void Initialize(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        var path = Path.Combine(logDirectory, "kindlemount.log");
        lock (Gate)
        {
            RollIfNeeded(path);
            _filePath = path;
        }
        Info($"--- {AppInfo.NameWithVersion} 起動 ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ---");
    }

    public static void Trace(string message) => Write(LogLevel.Trace, message);
    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string message, Exception ex) =>
        Write(LogLevel.Error, $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(LogLevel level, string message)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()[0]}] {message}";
        lock (Gate)
        {
            if (_filePath is null)
            {
                return;
            }

            try
            {
                File.AppendAllText(_filePath, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // ログ書き込みの失敗でアプリを止めない。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void RollIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxFileBytes)
            {
                var old = path + ".old";
                File.Delete(old);
                File.Move(path, old);
            }
        }
        catch (IOException)
        {
        }
    }
}
