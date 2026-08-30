using DokanNet;
using Timer = System.Threading.Timer;

namespace KindleMount.FileSystem;

/// <summary>
/// MTP の転送は数十秒〜数分かかることがある。その間 Dokan のカーネル側タイムアウトで
/// 操作が打ち切られないよう、定期的に TryResetTimeout を呼んでおく。
/// </summary>
public sealed class DokanKeepAlive : IDisposable
{
    private readonly Timer _timer;
    private readonly ManualResetEvent _disposed = new(false);

    public DokanKeepAlive(IDokanFileInfo info, int intervalMilliseconds = 8_000, int extendMilliseconds = 120_000)
    {
        _timer = new Timer(
            _ =>
            {
                try
                {
                    info.TryResetTimeout(extendMilliseconds);
                }
                catch (Exception)
                {
                    // ハンドルが既に閉じられている場合は何もしない。
                }
            },
            null,
            intervalMilliseconds,
            intervalMilliseconds);
    }

    public void Dispose()
    {
        // コールバック実行中に戻らないよう、完全停止を待つ。
        _timer.Dispose(_disposed);
        _disposed.WaitOne(TimeSpan.FromSeconds(5));
        _disposed.Dispose();
    }
}
