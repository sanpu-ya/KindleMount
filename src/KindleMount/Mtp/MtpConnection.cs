using KindleMount.Logging;
using MediaDevices;
using System.Runtime.InteropServices;

namespace KindleMount.Mtp;

/// <summary>
/// 1 台の MTP デバイスへのアクセスを直列化するラッパー。
///
/// WPD は 1 デバイスにつき 1 トランザクションしか処理できないため、Dokan の複数
/// ワーカースレッドからの呼び出しをここでロックしてシリアライズする。
/// セッションが切れた場合は 1 度だけ透過的に再接続する。
/// </summary>
public sealed class MtpConnection : IDisposable
{
    private readonly object _gate = new();
    private readonly string _deviceId;
    private readonly bool _readOnly;
    private MediaDevice? _device;
    private bool _disposed;
    private bool _deviceGone;

    public MtpConnection(string deviceId, bool readOnly)
    {
        _deviceId = deviceId;
        _readOnly = readOnly;
    }

    /// <summary>デバイスが失われたことを最初に検知したときに 1 度だけ発火する。</summary>
    public event Action? DeviceLost;

    public string DeviceId => _deviceId;

    public bool IsDeviceGone => Volatile.Read(ref _deviceGone);

    public void Open()
    {
        lock (_gate)
        {
            EnsureOpen();
        }
    }

    public T Exec<T>(string operation, Func<MediaDevice, T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            try
            {
                EnsureOpen();
                return action(_device!);
            }
            catch (NotConnectedException ex)
            {
                Log.Debug($"MTP セッションが切断されたため再接続します ({operation}): {ex.Message}");
                return Retry(operation, action);
            }
            catch (COMException ex) when (IsSessionError(ex))
            {
                Log.Debug($"MTP COM エラーのため再接続します ({operation}): 0x{ex.HResult:X8}");
                return Retry(operation, action);
            }
        }
    }

    public void Exec(string operation, Action<MediaDevice> action) =>
        Exec(operation, device =>
        {
            action(device);
            return true;
        });

    private T Retry<T>(string operation, Func<MediaDevice, T> action)
    {
        CloseCore();
        try
        {
            EnsureOpen();
            return action(_device!);
        }
        catch (Exception ex) when (ex is NotConnectedException or COMException)
        {
            MarkGone($"{operation} の再試行に失敗しました");
            throw new MtpDeviceGoneException($"MTP 操作 '{operation}' に失敗しました。", ex);
        }
    }

    private void EnsureOpen()
    {
        if (_device is { IsConnected: true })
        {
            return;
        }

        CloseCore();

        MediaDevice? found = null;
        try
        {
            var candidates = MediaDeviceManager.Instance.GetDevices() ?? Enumerable.Empty<MediaDevice>();
            foreach (var device in candidates)
            {
                if (found is null && string.Equals(device.DeviceId, _deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    found = device;
                }
                else
                {
                    device.Dispose();
                }
            }
        }
        catch (COMException ex)
        {
            MarkGone("WPD デバイスの列挙に失敗しました");
            throw new MtpDeviceGoneException("WPD デバイスの列挙に失敗しました。", ex);
        }

        if (found is null)
        {
            MarkGone("デバイスが WPD から見えなくなりました");
            throw new MtpDeviceGoneException($"デバイスが見つかりません: {_deviceId}");
        }

        try
        {
            var access = _readOnly ? MediaDeviceAccess.GenericRead : MediaDeviceAccess.Default;
            found.Connect(access, MediaDeviceShare.Default, enableCache: false);
        }
        catch (Exception ex)
        {
            found.Dispose();
            MarkGone("MTP セッションの確立に失敗しました");
            throw new MtpDeviceGoneException("MTP セッションの確立に失敗しました。", ex);
        }

        _device = found;
        Log.Debug($"MTP セッションを確立しました: {_deviceId}");
    }

    private void CloseCore()
    {
        if (_device is null)
        {
            return;
        }

        try
        {
            if (_device.IsConnected)
            {
                _device.Disconnect();
            }
        }
        catch (Exception)
        {
            // 切断済みなら何もすることはない。
        }

        try
        {
            _device.Dispose();
        }
        catch (Exception)
        {
        }

        _device = null;
    }

    private void MarkGone(string reason)
    {
        var first = false;
        lock (_gate)
        {
            if (!_deviceGone)
            {
                _deviceGone = true;
                first = true;
            }
        }

        if (first)
        {
            Log.Info($"デバイスを喪失しました ({reason}): {_deviceId}");
            DeviceLost?.Invoke();
        }
    }

    /// <summary>セッション再確立で回復し得る COM エラーかどうか。</summary>
    private static bool IsSessionError(COMException ex) => unchecked((uint)ex.HResult) switch
    {
        0x800706BA => true, // RPC サーバーを利用できません
        0x800706BE => true, // リモート プロシージャ コールが失敗しました
        0x8007001F => true, // デバイスが動作していません
        0x80070015 => true, // デバイスの準備ができていません
        0x8007048F => true, // デバイスが接続されていません
        0x80042005 => true, // WPD_E_DEVICE_NOT_OPEN
        _ => false,
    };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CloseCore();
        }
    }
}
