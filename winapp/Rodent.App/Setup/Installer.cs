using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Rodent.Core.Diagnostics;

namespace Rodent.App.Setup;

/// <summary>
/// Per-user install/uninstall for the standalone exe: it copies itself into
/// %LOCALAPPDATA%\Programs\Rodent, adds shortcuts and an Add/Remove Programs
/// entry, and can undo all of it. No admin rights, no MSI, no service.
/// </summary>
internal static class Installer
{
    public const string AppName = "Rodent";
    public const string Publisher = "Umut Çetinkaya";

    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Rodent";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Rodent";

    public static string InstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public static string InstalledExe => Path.Combine(InstallRoot, "Rodent.exe");

    public static string StartMenuLink => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    public static string DesktopLink => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    /// <summary>User data (profiles, macros) — kept on uninstall unless purging.</summary>
    public static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static bool IsInstalled => File.Exists(InstalledExe);

    public static bool RunningFromInstall =>
        string.Equals(Environment.ProcessPath, InstalledExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>Installed version string from the registry, or null.</summary>
    public static string? InstalledVersion
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
                return key?.GetValue("DisplayVersion") as string;
            }
            catch { return null; }
        }
    }

    // ---- install ----

    public static void Install(bool startup, bool desktopShortcut, Action<string>? progress = null)
    {
        void Step(string s) { Log.Info("install: " + s); progress?.Invoke(s); }

        string source = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot determine the running exe path");

        Step($"target {InstallRoot}");
        Directory.CreateDirectory(InstallRoot);

        if (!string.Equals(source, InstalledExe, StringComparison.OrdinalIgnoreCase))
        {
            StopOtherInstances(progress);
            Step("copying program files");
            CopyProgram(source);
        }
        else
        {
            Step("already running from the install folder — refreshing entries");
        }

        Step("creating Start Menu shortcut");
        TryDo(() => Shortcut.Create(StartMenuLink, InstalledExe, "", "Configure your mouse", InstallRoot),
              "Start Menu shortcut");

        if (desktopShortcut)
        {
            Step("creating desktop shortcut");
            TryDo(() => Shortcut.Create(DesktopLink, InstalledExe, "", "Configure your mouse", InstallRoot),
                  "desktop shortcut");
        }
        else
        {
            Shortcut.Delete(DesktopLink);
        }

        Step("registering with Apps & features");
        TryDo(WriteUninstallEntry, "uninstall entry");

        Step(startup ? "enabling start with Windows" : "start with Windows off");
        TryDo(() => SetStartup(startup), "startup entry");

        Log.Info("install: done");
    }

    /// <summary>Relaunch the installed copy and hand over.</summary>
    public static void LaunchInstalled(bool tray = false)
    {
        try
        {
            Process.Start(new ProcessStartInfo(InstalledExe)
            {
                Arguments = tray ? "--tray" : "",
                UseShellExecute = true,
                WorkingDirectory = InstallRoot,
            });
        }
        catch (Exception ex) { Log.Exception(ex, "launch after install"); }
    }

    private static void WriteUninstallEntry()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        long sizeKb = 0;
        try { sizeKb = new FileInfo(InstalledExe).Length / 1024; } catch { }

        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", App.VersionNumber);
        key.SetValue("Publisher", Publisher);
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", InstallRoot);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --silent");
        key.SetValue("EstimatedSize", (int)sizeKb, RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    // ---- uninstall ----

    public static void Uninstall(bool purge, Action<string>? progress = null)
    {
        void Step(string s) { Log.Info("uninstall: " + s); progress?.Invoke(s); }

        Step("removing startup entry");
        TryDo(() => SetStartup(false), "startup entry");

        Step("removing shortcuts");
        Shortcut.Delete(StartMenuLink);
        Shortcut.Delete(DesktopLink);

        Step("removing Apps & features entry");
        TryDo(() => Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false),
              "uninstall entry");

        if (purge)
        {
            Step("deleting profiles and macros");
            TryDo(() => { if (Directory.Exists(ConfigDir)) Directory.Delete(ConfigDir, recursive: true); }, "config");
            TryDo(() =>
            {
                Log.Close();   // release our own log handle first
                string logs = Log.DefaultDirectory;
                if (Directory.Exists(logs)) Directory.Delete(logs, recursive: true);
            }, "logs");
        }

        StopOtherInstances(progress);

        Step("removing program files");
        if (Directory.Exists(InstallRoot))
        {
            if (RunningFromInstall) ScheduleSelfDelete(InstallRoot);
            else TryDo(() => Directory.Delete(InstallRoot, recursive: true), "program files");
        }

        Log.Info("uninstall: done");
    }

    /// <summary>
    /// The exe cannot delete itself while running, so hand the folder to a detached
    /// cmd that waits a couple of seconds for this process to exit first.
    /// </summary>
    private static void ScheduleSelfDelete(string dir)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe")
            {
                // ping is the portable "sleep" available on every Windows box.
                Arguments = $"/c ping 127.0.0.1 -n 4 >nul & rmdir /s /q \"{dir}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex) { Log.Exception(ex, "scheduling program-file deletion"); }
    }

    // ---- shared helpers ----

    public static bool StartupEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(RunValueName) != null;
            }
            catch { return false; }
        }
    }

    /// <summary>Point the Run entry at the installed exe when there is one.</summary>
    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (!enabled) { key.DeleteValue(RunValueName, throwOnMissingValue: false); return; }
        string exe = IsInstalled ? InstalledExe : Environment.ProcessPath!;
        key.SetValue(RunValueName, $"\"{exe}\" --tray");
    }

    /// <summary>Close other Rodent processes so the exe can be replaced/removed.</summary>
    public static void StopOtherInstances(Action<string>? progress = null)
    {
        Process[] others;
        try { others = Process.GetProcessesByName("Rodent"); }
        catch { return; }

        int me = Environment.ProcessId;
        foreach (var p in others)
        {
            if (p.Id == me) { p.Dispose(); continue; }
            try
            {
                progress?.Invoke("closing the running copy");
                Log.Info($"stopping running instance pid {p.Id}");
                if (!p.CloseMainWindow() || !p.WaitForExit(3000))
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
            }
            catch (Exception ex) { Log.Exception(ex, $"stopping pid {p.Id}"); }
            finally { p.Dispose(); }
        }
    }

    /// <summary>
    /// Release builds are a single self-contained exe, so copying that one file is
    /// the whole install. A dev build sits next to its runtime DLLs — copy the
    /// folder in that case, so installing a locally-built Rodent also works.
    /// </summary>
    private static void CopyProgram(string sourceExe)
    {
        string sourceDir = Path.GetDirectoryName(sourceExe)!;
        bool singleFile = !File.Exists(Path.Combine(sourceDir, "Rodent.dll"));

        if (singleFile)
        {
            CopyWithRetry(sourceExe, InstalledExe);
            return;
        }

        Log.Info("dev layout detected — copying the whole output folder");
        foreach (string dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(InstallRoot, Path.GetRelativePath(sourceDir, dir)));
        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(InstallRoot, Path.GetRelativePath(sourceDir, file));
            CopyWithRetry(file, target);
        }
    }

    /// <summary>Antivirus and the just-closed instance can hold the file briefly.</summary>
    private static void CopyWithRetry(string source, string target)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { File.Copy(source, target, overwrite: true); return; }
            catch (IOException) when (attempt < 8)
            {
                Thread.Sleep(400);
            }
        }
    }

    private static void TryDo(Action action, string what)
    {
        try { action(); }
        catch (Exception ex) { Log.Exception(ex, what); }
    }
}
