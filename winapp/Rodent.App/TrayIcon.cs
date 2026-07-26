using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Rodent.Core.Diagnostics;

namespace Rodent.App;

/// <summary>
/// Notification-area icon on plain Win32 (Shell_NotifyIcon + a message-only
/// window). WinForms' NotifyIcon did the same job, but referencing WinForms
/// dragged its whole assembly set into the self-contained build — ~29 MB of the
/// download for one 16x16 icon and a two-item menu.
///
/// The menu is a native popup: it dismisses correctly when the app isn't the
/// foreground window, which is the part hand-rolled menus usually get wrong.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8001;      // WM_APP + 1
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_NULL = 0x0000;

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04;

    private const int MF_STRING = 0x0000;
    private const int TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;

    private readonly HwndSource _source;
    private readonly List<(string text, Action action)> _items = new();
    private readonly uint _taskbarCreated;
    private IntPtr _icon;
    private string _tip;
    private bool _added;

    public event Action? Activated;               // left click / double click

    public TrayIcon(string tip, Uri iconResource)
    {
        _tip = tip;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        // Message-only window: never visible, just a target for the callbacks.
        var p = new HwndSourceParameters("RodentTray")
        {
            ParentWindow = new IntPtr(-3),        // HWND_MESSAGE
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);

        _icon = LoadIcon(iconResource);
        Add();
    }

    public void AddMenuItem(string text, Action action) => _items.Add((text, action));

    public string Tip
    {
        get => _tip;
        set { _tip = value; if (_added) Notify(NIM_MODIFY); }
    }

    private void Add()
    {
        if (Notify(NIM_ADD))
        {
            _added = true;
            Log.Debug($"tray: icon added (hwnd 0x{_source.Handle.ToInt64():X}, icon 0x{_icon.ToInt64():X})");
        }
        else Log.Warn("tray: Shell_NotifyIcon(NIM_ADD) failed");
    }

    private bool Notify(int message)
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _source.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = _icon,
            szTip = _tip,
        };
        return Shell_NotifyIcon(message, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APP_TRAY)
        {
            Log.Trace($"tray: callback 0x{(int)lParam:X}");
            switch ((int)lParam)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    Activated?.Invoke();
                    handled = true;
                    break;
                case WM_RBUTTONUP:
                    ShowMenu();
                    handled = true;
                    break;
            }
        }
        else if (_taskbarCreated != 0 && msg == (int)_taskbarCreated)
        {
            // Explorer restarted and dropped every tray icon; put ours back.
            Log.Debug("tray: taskbar restarted, re-adding the icon");
            _added = false;
            Add();
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            for (int i = 0; i < _items.Count; i++)
                AppendMenu(menu, MF_STRING, i + 1, _items[i].text);

            GetCursorPos(out POINT pt);
            // Required by the shell: the owner must be foreground, or the menu
            // stays up after a click elsewhere.
            SetForegroundWindow(_source.Handle);
            int cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, _source.Handle, IntPtr.Zero);
            PostMessage(_source.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (cmd >= 1 && cmd <= _items.Count)
            {
                try { _items[cmd - 1].action(); }
                catch (Exception ex) { Log.Exception(ex, "tray menu item"); }
            }
        }
        finally { DestroyMenu(menu); }
    }

    /// <summary>
    /// Turn a packed .ico resource into an HICON at the shell's small-icon size.
    /// The file is an ICONDIR header plus one ICONDIRENTRY per size; we pick the
    /// closest and hand that image to CreateIconFromResourceEx.
    /// </summary>
    private static IntPtr LoadIcon(Uri resource)
    {
        try
        {
            var sri = Application.GetResourceStream(resource);
            if (sri == null) return IntPtr.Zero;
            using var ms = new MemoryStream();
            sri.Stream.CopyTo(ms);
            byte[] ico = ms.ToArray();
            if (ico.Length < 6) return IntPtr.Zero;

            int count = BitConverter.ToUInt16(ico, 4);
            int want = GetSystemMetrics(SM_CXSMICON);
            int best = -1, bestScore = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                int entry = 6 + i * 16;
                if (entry + 16 > ico.Length) break;
                int w = ico[entry] == 0 ? 256 : ico[entry];
                int score = Math.Abs(w - want);
                if (score < bestScore) { bestScore = score; best = entry; }
            }
            if (best < 0) return IntPtr.Zero;

            int bytes = BitConverter.ToInt32(ico, best + 8);
            int offset = BitConverter.ToInt32(ico, best + 12);
            if (offset < 0 || bytes <= 0 || offset + bytes > ico.Length) return IntPtr.Zero;

            IntPtr buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                Marshal.Copy(ico, offset, buffer, bytes);
                return CreateIconFromResourceEx(buffer, (uint)bytes, true, 0x00030000, want, want, 0);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception ex)
        {
            Log.Exception(ex, "loading the tray icon");
            return IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_added) { Notify(NIM_DELETE); _added = false; }
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
        _source.Dispose();
    }

    // ---- Win32 ----
    private const int SM_CXSMICON = 49;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, int flags, int id, string item);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconFromResourceEx(IntPtr data, uint size, bool isIcon,
        uint version, int cx, int cy, uint flags);
}
