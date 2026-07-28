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
            if (RunSchtasks($"/Query /TN \"{TaskName}\"") == 0) return true;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(RunValueName) != null;     // pre-task installs
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Start with Windows, via a scheduled task that runs with the highest
    /// privileges. Rodent needs to be elevated to work in apps that are (see
    /// app.manifest), and Windows silently skips Run-key entries for an app that
    /// asks for elevation — so the task replaces that entry, and any leftover one
    /// is cleaned up. Falls back to the Run key only if the task can't be made.
    /// </summary>
    public static void SetStartup(bool enabled)
    {
        RunSchtasks($"/Delete /F /TN \"{TaskName}\"");
        using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        if (!enabled) return;

        string exe = IsInstalled ? InstalledExe : Environment.ProcessPath!;
        if (CreateStartupTask(exe)) return;

        Log.Warn("couldn't register the startup task — falling back to the Run key " +
                 "(Windows may skip it because Rodent asks for elevation)");
        using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        run.SetValue(RunValueName, $"\"{exe}\" --tray");
    }

    private const string TaskName = "Rodent";

    /// <summary>
    /// Register the logon task from XML: an InteractiveToken principal needs no
    /// stored password, and HighestAvailable is what makes the elevated start work.
    /// </summary>
    private static bool CreateStartupTask(string exe)
    {
        // Every value interpolated below is XML-escaped: a Windows account or
        // machine name may legally contain '&', which would otherwise produce XML
        // schtasks rejects — the task silently never gets created.
        string user = System.Security.SecurityElement.Escape(
            Environment.UserDomainName + "\\" + Environment.UserName);
        string author = System.Security.SecurityElement.Escape(Publisher);
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>{author}</Author>
                <Description>Start Rodent in the notification area at logon.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{System.Security.SecurityElement.Escape(exe)}</Command>
                  <Arguments>--tray</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        string path = Path.Combine(Path.GetTempPath(), "rodent-startup.xml");
        try
        {
            File.WriteAllText(path, xml, System.Text.Encoding.Unicode);   // schtasks wants UTF-16
            return RunSchtasks($"/Create /F /TN \"{TaskName}\" /XML \"{path}\"") == 0;
        }
        catch (Exception ex) { Log.Exception(ex, "creating the startup task"); return false; }
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>Run schtasks.exe silently and return its exit code (-1 if it won't start).</summary>
    private static int RunSchtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return -1;
            p.WaitForExit(15_000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch (Exception ex) { Log.Exception(ex, "running schtasks"); return -1; }
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
