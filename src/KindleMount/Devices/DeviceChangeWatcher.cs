using KindleMount.Logging;
using System.Runtime.InteropServices;

namespace KindleMount.Devices;

/// <summary>
/// WM_DEVICECHANGE を受け取る隠しウィンドウ。WPD デバイスインターフェースを購読し、
/// 到着/取り外しをデバウンスしてから通知する(1 回の接続で複数イベントが飛ぶため)。
/// </summary>
public sealed class DeviceChangeWatcher : IDisposable
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceQueryRemove = 0x8001;
    private const int DbtDeviceRemovePending = 0x8003;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDevNodesChanged = 0x0007;
    private const int DbtDevTypDeviceInterface = 0x00000005;
    private const int DeviceNotifyWindowHandle = 0x00000000;

    /// <summary>GUID_DEVINTERFACE_WPD</summary>
    private static readonly Guid WpdInterfaceGuid = new("6AC27878-A6FA-4155-BA85-F98F491D4F33");

    /// <summary>GUID_DEVINTERFACE_USB_DEVICE (MTP として現れる前の生 USB 到着も拾う)</summary>
    private static readonly Guid UsbInterfaceGuid = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    private readonly MessageWindow _window;
    private readonly System.Windows.Forms.Timer _debounce;
    private readonly List<IntPtr> _registrations = new();

    /// <summary>デバイス構成が変化した可能性があるときに UI スレッドで発火する。</summary>
    public event Action? DevicesChanged;

    /// <summary>
    /// デバイスの取り外しが要求されたときに、対象のデバイスパスを添えて発火する。
    /// 取り外しを妨げないよう、デバウンスせず直ちに通知する。
    ///
    /// 注意: 実機 (Kindle Paperwhite / WPD) で「ハードウェアの安全な取り外し」を試したところ、
    /// 届いたのは DBT_DEVICEREMOVECOMPLETE だけで、この予告は配送されなかった。
    /// デバイスインターフェース登録では予告は届かず、受け取るには対象デバイスのファイル
    /// ハンドルを DBT_DEVTYP_HANDLE で登録する必要がある。掴んだまま取り外しても
    /// 拒否 (veto) されなかったため、現状はそこまでしていない。
    /// 予告が届く環境では、この経路の方が早く資源を手放せる。
    /// </summary>
    public event Action<string>? DeviceRemovalRequested;

    public DeviceChangeWatcher(int debounceMilliseconds = 1500)
    {
        _debounce = new System.Windows.Forms.Timer { Interval = Math.Max(200, debounceMilliseconds) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            DevicesChanged?.Invoke();
        };

        _window = new MessageWindow(OnDeviceChange);
        RegisterFor(WpdInterfaceGuid);
        RegisterFor(UsbInterfaceGuid);
    }

    private void OnDeviceChange(int eventType, IntPtr data)
    {
        // 取り外しの予告。ここでデバイスを掴んだままだと OS 側の取り外しが失敗するので、
        // まとめずにその場で知らせて、掴んでいる資源を解放させる。
        if (eventType is DbtDeviceQueryRemove or DbtDeviceRemovePending)
        {
            var devicePath = ReadDeviceInterfaceName(data);
            Log.Debug($"デバイスの取り外し要求を受信 (event=0x{eventType:X4}, device={devicePath ?? "?"})");

            if (devicePath is not null)
            {
                DeviceRemovalRequested?.Invoke(devicePath);
            }

            return;
        }

        if (eventType is not (DbtDeviceArrival or DbtDeviceRemoveComplete or DbtDevNodesChanged))
        {
            return;
        }

        Log.Debug($"WM_DEVICECHANGE を受信 (event=0x{eventType:X4})");

        // 連続イベントをまとめる。
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>
    /// DEV_BROADCAST_DEVICEINTERFACE からデバイスパスを取り出す。
    /// 対象が別種の通知(ボリュームやハンドル)なら null。
    /// </summary>
    private static string? ReadDeviceInterfaceName(IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var header = Marshal.PtrToStructure<DevBroadcastHeader>(data);
            if (header.DeviceType != DbtDevTypDeviceInterface)
            {
                return null;
            }

            // dbcc_name はヘッダー(size/type/reserved) と ClassGuid の直後から始まる可変長文字列。
            var nameOffset = (sizeof(int) * 3) + Marshal.SizeOf<Guid>();
            var name = Marshal.PtrToStringUni(IntPtr.Add(data, nameOffset));
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex)
        {
            Log.Debug($"デバイス通知の解析に失敗しました: {ex.Message}");
            return null;
        }
    }

    private void RegisterFor(Guid interfaceGuid)
    {
        var filter = new DevBroadcastDeviceInterface
        {
            Size = Marshal.SizeOf<DevBroadcastDeviceInterface>(),
            DeviceType = DbtDevTypDeviceInterface,
            Reserved = 0,
            ClassGuid = interfaceGuid,
            Name = 0,
        };

        var buffer = Marshal.AllocHGlobal(filter.Size);
        try
        {
            Marshal.StructureToPtr(filter, buffer, false);
            var handle = RegisterDeviceNotification(_window.Handle, buffer, DeviceNotifyWindowHandle);
            if (handle == IntPtr.Zero)
            {
                Log.Warn($"RegisterDeviceNotification に失敗しました (error={Marshal.GetLastWin32Error()})。ポーリングで代替します。");
            }
            else
            {
                _registrations.Add(handle);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        _debounce.Stop();

        foreach (var handle in _registrations)
        {
            UnregisterDeviceNotification(handle);
        }

        _registrations.Clear();
        _debounce.Dispose();

        // ReleaseHandle() は HWND を破棄せずラッパーから切り離すだけなので、
        // 先に呼ぶと DestroyHandle() が何も破棄できなくなる (ウィンドウリーク)。
        _window.DestroyHandle();
    }

    private sealed class MessageWindow : NativeWindow
    {
        private readonly Action<int, IntPtr> _onDeviceChange;

        public MessageWindow(Action<int, IntPtr> onDeviceChange)
        {
            _onDeviceChange = onDeviceChange;
            CreateHandle(new CreateParams
            {
                Caption = "KindleMount.DeviceWatcher",
                // HWND_MESSAGE 相当のメッセージ専用ウィンドウ。
                Parent = new IntPtr(-3),
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmDeviceChange)
            {
                _onDeviceChange(m.WParam.ToInt32(), m.LParam);
            }

            base.WndProc(ref m);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevBroadcastHeader
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevBroadcastDeviceInterface
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
        public Guid ClassGuid;
        public short Name;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, IntPtr notificationFilter, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);
}
