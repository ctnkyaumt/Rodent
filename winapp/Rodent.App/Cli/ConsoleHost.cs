using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Rodent.App.Cli;

/// <summary>
/// Rodent is a WinExe (no console of its own, so launching the GUI never flashes
/// a black window). For CLI output we attach to the console that started us; if
/// there is none — double-clicked from Explorer — text falls back to a message box.
/// </summary>
internal static class ConsoleHost
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);

    private static bool _tried;
    public static bool Attached { get; private set; }

    public static bool Attach()
    {
        if (_tried) return Attached;
        _tried = true;

        // A valid stdout handle means output is already going somewhere useful
        // (inherited console, or `Rodent.exe --help > out.txt`).
        IntPtr outHandle = GetStdHandle(StdOutputHandle);
        if (outHandle != IntPtr.Zero && outHandle != new IntPtr(-1)) Attached = true;
        else if (GetConsoleWindow() != IntPtr.Zero) Attached = true;
        else if (AttachConsole(AttachParentProcess)) Attached = true;

        if (Attached)
        {
            try
            {
                // Standard handles are not wired up for a WinExe; rebuild them.
                var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetOut(stdout);
                var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetError(stderr);
            }
            catch { Attached = false; }
            // Fails when output is a file rather than a console; harmless either way.
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        }
        return Attached;
    }

    /// <summary>Print to the terminal, or show a dialog when there isn't one.</summary>
    public static void Out(string text)
    {
        if (Attach()) Console.WriteLine(text);
        else System.Windows.MessageBox.Show(text, "Rodent",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    public static void Error(string text)
    {
        if (Attach()) Console.Error.WriteLine(text);
        else System.Windows.MessageBox.Show(text, "Rodent",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }

    /// <summary>
    /// cmd.exe has already redrawn its prompt by the time we write, so leave a
    /// blank line at the end to keep the next prompt on its own line.
    /// </summary>
    public static void Finish()
    {
        if (Attached) Console.WriteLine();
    }

    /// <summary>Console sink for <see cref="Rodent.Core.Diagnostics.Log"/>.</summary>
    public static void EchoLine(string line)
    {
        if (Attached) Console.WriteLine(line);
    }
}
