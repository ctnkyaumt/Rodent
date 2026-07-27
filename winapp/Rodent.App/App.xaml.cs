using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Rodent.App.Cli;
using Rodent.App.Setup;
using Rodent.Core.Automation;
using Rodent.Core.Diagnostics;

namespace Rodent.App;

public partial class App : Application
{
    /// <summary>"v0.9" — from the assembly version (CI stamps releases via -p:Version).</summary>
    public static string VersionTag
    {
        get
        {
            var v = typeof(App).Assembly.GetName().Version;
            return v == null ? "" : v.Build > 0 ? $"v{v.Major}.{v.Minor}.{v.Build}" : $"v{v.Major}.{v.Minor}";
        }
    }

    /// <summary>"0.9.0" — what Apps &amp; features shows.</summary>
    public static string VersionNumber
    {
        get
        {
            var v = typeof(App).Assembly.GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public CliOptions Options { get; }
    public ProfilesConfig Profiles { get; private set; } = new();
    public AutomationService Automation { get; private set; } = null!;
    public bool Quitting { get; private set; }

    /// <summary>Broadcast message a second instance posts to surface the first one.</summary>
    internal static uint ShowWindowMessage { get; private set; }

    private TrayIcon? _tray;
    private Mutex? _mutex;
    private bool _ownsMutex;

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    public App() : this(new CliOptions()) { }

    public App(CliOptions options)
    {
        Options = options;
        InstallCrashHandlers();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Setup runs windowed but never starts the engine: no HID handles, no hooks.
        if (Options.Uninstall) { RunSetup(SetupMode.Uninstall); return; }
        if (Options.Install)
        {
            if (!EnsureRuntimeForInstall()) { Quitting = true; Shutdown(3); return; }
            RunSetup(SetupMode.Install);
            return;
        }

        // Single instance: a second launch would install a second mouse hook
        // (rules firing twice) and a second tray icon. Surface the first instead.
        ShowWindowMessage = RegisterWindowMessage("RodentShowWindow");
        _mutex = new Mutex(true, @"Local\RodentSingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            Log.Info("another instance is running — asking it to come forward");
            PostMessage(HWND_BROADCAST, ShowWindowMessage, IntPtr.Zero, IntPtr.Zero);
            Quitting = true;
            Shutdown();
            return;
        }

        if (!OfferInstall()) return;

        Profiles = ProfilesConfig.Load();
        Log.Info($"profiles loaded: {Profiles.Profiles.Count} per-app rule(s)");
        Automation = new AutomationService(Profiles);
        Automation.DeviceProvider = () =>
            Dispatcher.Invoke(() => (MainWindow as MainWindow)?.SelectedDpiDevice);
        Automation.Start();
        SetupTray();

        // Created manually (no StartupUri) so a startup launch can begin hidden
        // in the tray. Device/profile init runs either way — only Show() differs.
        var win = new MainWindow();
        MainWindow = win;
        if (Options.Tray)
            // The window needs an HWND even while hidden, or the second-instance
            // "surface yourself" broadcast would have no listener.
            new System.Windows.Interop.WindowInteropHelper(win).EnsureHandle();
        else
            win.Show();
    }

    /// <summary>
    /// First run of a downloaded exe: offer to install. Returns false when the
    /// app should not continue starting (installed and relaunched, or cancelled).
    /// </summary>
    private bool OfferInstall()
    {
        if (Options.Portable || Options.Tray || Installer.IsInstalled || Installer.RunningFromInstall)
            return true;

        // Don't offer an install that would produce an app which only starts from
        // this shell (see RuntimeCheck) — say so and let the user run in place.
        if (!EnsureRuntimeForInstall()) return true;

        Log.Info("not installed — showing the first-run setup prompt");
        var dlg = new InstallWindow(SetupMode.FirstRun);
        dlg.ShowDialog();

        switch (dlg.Result)
        {
            case SetupResult.Installed:
                Installer.LaunchInstalled();
                Quitting = true;
                Shutdown();
                return false;
            case SetupResult.RunPortable:
                Log.Info("running portable");
                return true;
            default:
                Quitting = true;
                Shutdown();
                return false;
        }
    }

    /// <summary>
    /// Installing is only sound when the runtime is installed for the whole
    /// machine; otherwise the installed copy would start from here and nowhere
    /// else. Warn, offer the download page, and don't install.
    /// </summary>
    private bool EnsureRuntimeForInstall()
    {
        if (RuntimeCheck.IsDesktopRuntimeInstalled(out string? found))
        {
            Log.Info($"desktop runtime {found} found — install can proceed");
            return true;
        }

        Log.Warn($"no machine-wide .NET Desktop Runtime {RuntimeCheck.RequiredMajor} " +
                 $"(highest seen: {found ?? "none"}) — not installing");
        var answer = MessageBox.Show(
            $"Rodent needs the Microsoft .NET Desktop Runtime {RuntimeCheck.RequiredMajor} " +
            "(x64), and it isn't installed for this PC.\n\n" +
            "Rodent has not been installed. Install the runtime, then run this again.\n\n" +
            "Open the download page now?",
            "Rodent — .NET Desktop Runtime required",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Yes)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    RuntimeCheck.DownloadUrl) { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Exception(ex, "opening the runtime download page"); }
        }
        return false;
    }

    private void RunSetup(SetupMode mode)
    {
        var dlg = new InstallWindow(mode);
        dlg.ShowDialog();
        if (dlg.Result == SetupResult.Installed)
            Installer.LaunchInstalled();
        Quitting = true;
        Shutdown(dlg.Result == SetupResult.Cancelled ? 1 : 0);
    }

    // ---- crash handling ----

    private void InstallCrashHandlers()
    {
        // A background HID read that fails while the mouse is being unplugged used
        // to reach the dispatcher and kill the process; now it is logged and shown.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Exception(args.Exception, "unhandled (UI thread)");
            args.Handled = true;
            if (!Quitting) ShowCrashNotice(args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log.Exception(ex, "unhandled (fatal)");
            else Log.Error("unhandled non-exception fault: " + args.ExceptionObject);
            Log.Close();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Exception(args.Exception, "unobserved task");
            args.SetObserved();
        };
    }

    private void ShowCrashNotice(Exception ex)
    {
        string where = Log.FilePath is { } p ? $"\n\nDetails were written to:\n{p}" : "";
        MessageBox.Show($"Something went wrong:\n\n{ex.Message}{where}", "Rodent",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Persist edited profiles and hand them to the running engine.</summary>
    public void SaveProfiles()
    {
        Profiles.Save();
        Automation.SetProfiles(Profiles);
    }

    private void SetupTray()
    {
        _tray = new TrayIcon($"Rodent {VersionTag}",
            new Uri("pack://application:,,,/Assets/rodent.ico"));
        _tray.Activated += ShowMain;
        _tray.AddMenuItem("Open Rodent", ShowMain);
        _tray.AddMenuItem("Quit", () => { Quitting = true; Shutdown(); });
    }

    private void ShowMain()
    {
        var w = MainWindow;
        if (w == null) return;
        w.Show();
        w.WindowState = WindowState.Normal;
        w.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (MainWindow as MainWindow)?.DisposeDevices();
        Automation?.Dispose();
        _tray?.Dispose();
        if (_mutex != null && _ownsMutex) { try { _mutex.ReleaseMutex(); } catch { } }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
