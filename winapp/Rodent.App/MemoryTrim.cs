using System.Runtime.InteropServices;
using Rodent.Core.Diagnostics;

namespace Rodent.App;

/// <summary>
/// Rodent spends most of its life sitting in the tray with no window on screen.
/// WPF holds on to render-side buffers and the GC has no reason to give pages
/// back, so a hidden app kept a working set the size of a visible one.
///
/// Collecting and then emptying the working set when the window goes away hands
/// those pages back to Windows; they fault back in on demand when the window is
/// shown again, which for a tray app is exactly the right trade.
/// </summary>
internal static class MemoryTrim
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    // -1/-1 tells Windows to trim the working set to the minimum it can.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    public static void Trim(string reason)
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
            Log.Debug($"trimmed working set ({reason})");
        }
        catch (Exception ex)
        {
            Log.Exception(ex, "trimming the working set");
        }
    }
}
