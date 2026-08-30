using DokanNet.Logging;

namespace KindleMount.Logging;

/// <summary>DokanNet のログを本アプリのロガーへ橋渡しする。</summary>
public sealed class DokanLoggerAdapter : ILogger
{
    public bool DebugEnabled => Log.MinimumLevel <= LogLevel.Debug;

    public void Debug(string message, params object[] args) => Log.Debug("dokan: " + Format(message, args));

    public void Info(string message, params object[] args) => Log.Debug("dokan: " + Format(message, args));

    public void Warn(string message, params object[] args) => Log.Warn("dokan: " + Format(message, args));

    public void Error(string message, params object[] args) => Log.Error("dokan: " + Format(message, args));

    public void Fatal(string message, params object[] args) => Log.Error("dokan: " + Format(message, args));

    private static string Format(string message, object[] args)
    {
        if (args is null || args.Length == 0)
        {
            return message;
        }

        try
        {
            return string.Format(message, args);
        }
        catch (FormatException)
        {
            return message;
        }
    }
}
