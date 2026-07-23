using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Rodent.Core.Automation;

/// <summary>
/// Reports the process name of the foreground window and raises an event when it
/// changes. This is the "which app is active" signal that drives per-app behavior
/// (the software side of what G HUB does). A WinEvent hook gives the instant
/// signal, backed by a slow poll: EVENT_SYSTEM_FOREGROUND is unreliable for
/// restore-from-taskbar and show-desktop transitions (fires with the taskbar's
/// hwnd, or not at all), which left the lighting stuck on the previous app.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    public event Action<string>? AppChanged; // exe name without extension, lowercased

    private readonly WinEventProc _proc;      // kept alive so the delegate isn't GC'd
    private IntPtr _hook;
    private Timer? _poll;
    private readonly object _sync = new();
    private string _current = "";
    private string _pollSample = "";

    public string CurrentApp => _current;

    public ForegroundWatcher()
    {
        _proc = OnWinEvent;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _poll = new Timer(_ => { try { PollTick(); } catch { } }, null, 600, 600);
        Update(GetForegroundWindow());
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        _poll?.Dispose();
        _poll = null;
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        => Update(hwnd);

    /// <summary>
    /// Safety poll for transitions the WinEvent misses. Acts only when two
    /// consecutive samples agree: a single sample mid taskbar-click often reads
    /// the transient foreground (the taskbar itself) and caused churn while
    /// Windows was still handing focus over.
    /// </summary>
    private void PollTick()
    {
        string app = ProcessNameOf(GetForegroundWindow());
        bool stable = app.Length > 0 && app == _pollSample;
        _pollSample = app;
        if (stable) Update(GetForegroundWindow());
    }

    private void Update(IntPtr hwnd)
    {
        string app = ProcessNameOf(hwnd);
        // Compare-and-set under a lock (hook runs on the UI thread, the poll on a
        // pool thread); the event fires OUTSIDE it — a handler hopping threads
        // while the other path waits on the lock would deadlock.
        lock (_sync)
        {
            if (app.Length == 0 || app == _current) return;
            _current = app;
        }
        AppChanged?.Invoke(app);
    }

    private static string ProcessNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.ToLowerInvariant();
        }
        catch { return ""; }
    }

    public void Dispose() => Stop();

    // ---- Win32 ----
    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
