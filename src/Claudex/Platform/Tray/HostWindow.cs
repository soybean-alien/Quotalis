using System.Runtime.InteropServices;
using Claudex.Core.Diagnostics;
using static Claudex.Platform.Native.ShellNative;

namespace Claudex.Platform.Tray;

/// <summary>
/// An invisible top-level Win32 window that receives tray callbacks and system broadcasts
/// (display changes, work-area changes, Explorer restarts). Top-level rather than message-only,
/// because broadcasts such as WM_DISPLAYCHANGE are not delivered to message-only windows.
/// Lives on the UI thread.
/// </summary>
internal sealed class HostWindow : IDisposable
{
    private const string ClassName = "Claudex.Host";
    private readonly WndProc _wndProc;
    private readonly uint _taskbarCreated;
    private readonly List<IntPtr> _powerNotifications = [];

    public HostWindow()
    {
        _wndProc = WindowProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref wc);
        Handle = CreateWindowEx(0, ClassName, "Claudex", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (Handle == IntPtr.Zero)
        {
            Log.Error("window", "Host window could not be created", data: new { error = Marshal.GetLastWin32Error() });
        }
        else if (!WTSRegisterSessionNotification(Handle, NOTIFY_FOR_THIS_SESSION))
        {
            Log.Debug("window", "Lock notifications unavailable", new { error = Marshal.GetLastWin32Error() });
        }
    }

    public IntPtr Handle { get; }

    /// <summary>Tray icon callback: message id, lParam, wParam.</summary>
    public event Action<uint, IntPtr, IntPtr>? Message;

    /// <summary>Monitor layout, DPI or work area changed.</summary>
    public event Action? DisplaySettingsChanged;

    /// <summary>Explorer restarted; tray icons must be re-added.</summary>
    public event Action? TaskbarCreated;

    /// <summary>WM_HOTKEY with the registered id.</summary>
    public event Action<int>? HotKey;

    /// <summary>The PC was locked or went to sleep (false), or was unlocked or woke up (true).</summary>
    public event Action<bool>? SessionActiveChanged;

    /// <summary>A registered power setting changed: its GUID and its DWORD value.</summary>
    public event Action<Guid, uint>? PowerSettingChanged;

    /// <summary>
    /// Subscribes to power setting notifications (battery level, power source, lid). Windows sends the
    /// current value right after registering. Replaces an earlier subscription.
    /// </summary>
    public void RegisterPowerSettings(params Guid[] settings)
    {
        UnregisterPowerSettings();
        foreach (var setting in settings)
        {
            var guid = setting;
            var handle = Power.PowerNative.RegisterPowerSettingNotification(Handle, ref guid, Power.PowerNative.DEVICE_NOTIFY_WINDOW_HANDLE);
            if (handle != IntPtr.Zero)
            {
                _powerNotifications.Add(handle);
            }
        }
    }

    public void UnregisterPowerSettings()
    {
        foreach (var handle in _powerNotifications)
        {
            Power.PowerNative.UnregisterPowerSettingNotification(handle);
        }

        _powerNotifications.Clear();
    }

    public void Dispose()
    {
        UnregisterPowerSettings();
        if (Handle != IntPtr.Zero)
        {
            WTSUnRegisterSessionNotification(Handle);
            DestroyWindow(Handle);
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == _taskbarCreated)
            {
                TaskbarCreated?.Invoke();
                return IntPtr.Zero;
            }

            switch (msg)
            {
                case 0x0312: // WM_HOTKEY
                    HotKey?.Invoke((int)wParam);
                    return IntPtr.Zero;
                case WM_DISPLAYCHANGE:
                case WM_DPICHANGED:
                    DisplaySettingsChanged?.Invoke();
                    break;
                case WM_SETTINGCHANGE:
                    // SPI_SETWORKAREA = 0x2F (taskbar moved or resized).
                    if (wParam == 0x2F)
                    {
                        DisplaySettingsChanged?.Invoke();
                    }

                    break;
                case Power.PowerNative.WM_POWERBROADCAST when (int)wParam == Power.PowerNative.PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero:
                    // POWERBROADCAST_SETTING: the setting's GUID, the data length, then the data.
                    var setting = Marshal.PtrToStructure<Guid>(lParam);
                    var length = Marshal.ReadInt32(lParam, 16);
                    if (length >= 4)
                    {
                        PowerSettingChanged?.Invoke(setting, (uint)Marshal.ReadInt32(lParam, 20));
                    }

                    return new IntPtr(1);
                case WM_WTSSESSION_CHANGE when (int)wParam is WTS_SESSION_LOCK or WTS_SESSION_UNLOCK:
                    SessionActiveChanged?.Invoke((int)wParam == WTS_SESSION_UNLOCK);
                    break;
                case Power.PowerNative.WM_POWERBROADCAST when (int)wParam is PBT_APMSUSPEND or PBT_APMRESUMEAUTOMATIC:
                    SessionActiveChanged?.Invoke((int)wParam == PBT_APMRESUMEAUTOMATIC);
                    break;
                case >= WM_APP and < 0xC000:
                    Message?.Invoke(msg, lParam, wParam);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Log.Error("window", "Host window message failed", ex);
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private const uint WM_WTSSESSION_CHANGE = 0x02B1;
    private const int WTS_SESSION_LOCK = 0x7;
    private const int WTS_SESSION_UNLOCK = 0x8;
    private const int PBT_APMSUSPEND = 0x4;
    private const int PBT_APMRESUMEAUTOMATIC = 0x12;
    private const int NOTIFY_FOR_THIS_SESSION = 0;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);
}
