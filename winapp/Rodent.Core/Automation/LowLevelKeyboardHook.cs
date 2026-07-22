using System.Runtime.InteropServices;

namespace Rodent.Core.Automation;

/// <summary>
/// Global low-level keyboard hook. Two uses: macro recording (OnKey, observe-only)
/// and the per-app signal-key engine (OnKeyDecide, may swallow F13-F17 that the
/// mouse's remapped buttons emit). Runs its own thread with a message loop.
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    /// <summary>(virtualKey, scanCode, isExtended, isDown) — observe only. The scan
    /// code is the physical key, which is what layout-correct recording needs.</summary>
    public Action<int, uint, bool, bool>? OnKey;

    /// <summary>(virtualKey, isDown) → true to swallow the event. Keep this fast
    /// (rule lookup only); heavy work must be queued elsewhere.</summary>
    public Func<int, bool, bool>? OnKeyDecide;

    private readonly HookProc _proc;
    private IntPtr _hook;
    private Thread? _thread;
    private uint _threadId;

    public LowLevelKeyboardHook() => _proc = Callback;

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "RodentKeyHook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            if (down || up)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                OnKey?.Invoke((int)data.vkCode, data.scanCode, (data.flags & LLKHF_EXTENDED) != 0, down);
                if (OnKeyDecide != null && OnKeyDecide((int)data.vkCode, down))
                    return (IntPtr)1; // swallow
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Stop()
    {
        if (_thread == null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _thread = null;
    }

    public void Dispose() => Stop();

    // ---- Win32 ----
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_EXTENDED = 0x01;
    private const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
}
