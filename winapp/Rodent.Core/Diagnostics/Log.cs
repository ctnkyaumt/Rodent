using System.Text;

namespace Rodent.Core.Diagnostics;

public enum LogLevel { Off = 0, Error = 1, Warn = 2, Info = 3, Debug = 4, Trace = 5 }

/// <summary>
/// Small leveled file logger. Deliberately dependency-free and fail-silent: a
/// broken log path must never take the app down, so every write is guarded.
///
/// Default sink is %LOCALAPPDATA%\Rodent\logs\rodent.log, rolled at 2 MB with
/// five generations kept. <see cref="Echo"/> mirrors lines to a console when the
/// process was launched from one (see the CLI layer).
/// </summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private const int Generations = 5;

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static string? _path;

    public static LogLevel Level { get; private set; } = LogLevel.Off;
    /// <summary>Optional second sink (console mirror). Set by <c>Init</c>.</summary>
    public static Action<string>? Echo { get; set; }
    /// <summary>Log file in use, or null when file logging is off/unavailable.</summary>
    public static string? FilePath => _path;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rodent", "logs");

    public static string DefaultFile => Path.Combine(DefaultDirectory, "rodent.log");

    public static void Init(LogLevel level, string? file = null, Action<string>? echo = null)
    {
        lock (Gate)
        {
            CloseWriter();
            Level = level;
            Echo = echo;
            if (level == LogLevel.Off) return;
            try
            {
                string target = string.IsNullOrWhiteSpace(file) ? DefaultFile : Path.GetFullPath(file);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Roll(target);
                // FileShare.ReadWrite so the user can tail the log while Rodent runs.
                var fs = new FileStream(target, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
                _path = target;
            }
            catch
            {
                CloseWriter();
            }
        }
    }

    public static bool IsEnabled(LogLevel level) => Level != LogLevel.Off && level <= Level;

    public static void Error(string message) => Write(LogLevel.Error, "ERR ", message);
    public static void Warn(string message) => Write(LogLevel.Warn, "WARN", message);
    public static void Info(string message) => Write(LogLevel.Info, "INFO", message);
    public static void Debug(string message) => Write(LogLevel.Debug, "DBG ", message);
    public static void Trace(string message) => Write(LogLevel.Trace, "TRC ", message);

    /// <summary>Log an exception with its full chain; used by the crash handlers.</summary>
    public static void Exception(Exception ex, string context)
    {
        if (!IsEnabled(LogLevel.Error)) return;
        var sb = new StringBuilder();
        sb.Append(context).Append(": ").Append(ex.GetType().FullName).Append(": ").AppendLine(ex.Message);
        sb.Append(ex.StackTrace);
        for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
            sb.AppendLine().Append("  --> ").Append(inner.GetType().FullName).Append(": ").Append(inner.Message);
        Write(LogLevel.Error, "ERR ", sb.ToString());
    }

    /// <summary>Hex dump helper for HID traffic (Trace level only).</summary>
    public static void Frame(string prefix, ReadOnlySpan<byte> data)
    {
        if (!IsEnabled(LogLevel.Trace)) return;
        var sb = new StringBuilder(prefix).Append(' ');
        foreach (byte b in data) sb.Append(b.ToString("X2")).Append(' ');
        Write(LogLevel.Trace, "TRC ", sb.ToString());
    }

    public static void Close()
    {
        lock (Gate) CloseWriter();
    }

    private static void Write(LogLevel level, string tag, string message)
    {
        if (!IsEnabled(level)) return;
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {tag} [{Environment.CurrentManagedThreadId:D2}] {message}";
        lock (Gate)
        {
            try { _writer?.WriteLine(line); } catch { /* disk full / handle lost */ }
        }
        try { Echo?.Invoke(line); } catch { }
    }

    private static void CloseWriter()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        _path = null;
    }

    /// <summary>rodent.log -> rodent.1.log -> ... -> rodent.5.log (oldest dropped).</summary>
    private static void Roll(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;

            string dir = Path.GetDirectoryName(path)!;
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            string Gen(int n) => Path.Combine(dir, $"{stem}.{n}{ext}");

            File.Delete(Gen(Generations));
            for (int n = Generations - 1; n >= 1; n--)
                if (File.Exists(Gen(n))) File.Move(Gen(n), Gen(n + 1), overwrite: true);
            File.Move(path, Gen(1), overwrite: true);
        }
        catch { /* keep appending to the current file */ }
    }

    public static bool TryParseLevel(string text, out LogLevel level)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "off": case "none": case "0": level = LogLevel.Off; return true;
            case "error": case "err": case "1": level = LogLevel.Error; return true;
            case "warn": case "warning": case "2": level = LogLevel.Warn; return true;
            case "info": case "3": level = LogLevel.Info; return true;
            case "debug": case "dbg": case "4": level = LogLevel.Debug; return true;
            case "trace": case "all": case "5": level = LogLevel.Trace; return true;
            default: level = LogLevel.Info; return false;
        }
    }
}
