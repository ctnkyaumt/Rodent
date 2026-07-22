using System.Runtime.InteropServices;

namespace Rodent.Core.Automation;

/// <summary>
/// Global low-level mouse hook, observe-only (never swallows) — used for macro
/// recording of mouse clicks. Runs its own thread with a message loop, like
/// LowLevelKeyboardHook.
/// </summary>
public sealed class LowLevelMouseHook : IDisposable
{
    /// <summary>(button mask 1/2/4/8/16 = L/R/M/Back/Forward, isDown, screenX, screenY).</summary>
    public Action<int, bool, int, int>? OnButton;

    private readonly HookProc _proc;
    private IntPtr _hook;
    private Thread? _thread;
    private uint _threadId;

    public LowLevelMouseHook() => _proc = Callback;

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "RodentMouseHook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && OnButton != null)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            (int mask, bool down) = (int)wParam switch
            {
                WM_LBUTTONDOWN => (0x01, true), WM_LBUTTONUP => (0x01, false),
                WM_RBUTTONDOWN => (0x02, true), WM_RBUTTONUP => (0x02, false),
                WM_MBUTTONDOWN => (0x04, true), WM_MBUTTONUP => (0x04, false),
                WM_XBUTTONDOWN => ((data.mouseData >> 16) == 1 ? 0x08 : 0x10, true),
                WM_XBUTTONUP => ((data.mouseData >> 16) == 1 ? 0x08 : 0x10, false),
                _ => (0, false),
            };
            if (mask != 0)
            {
                try { OnButton(mask, down, data.pt.X, data.pt.Y); }
                catch { /* never throw inside a hook */ }
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
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    private const int WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;
    private const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }
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
