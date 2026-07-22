using System.Runtime.InteropServices;
using System.Windows;
using Rodent.Core.Automation;

namespace Rodent.App;

public partial class App : Application
{
    public ProfilesConfig Profiles { get; private set; } = new();
    public AutomationService Automation { get; private set; } = null!;
    public bool Quitting { get; private set; }

    /// <summary>Broadcast message a second instance posts to surface the first one.</summary>
    internal static uint ShowWindowMessage { get; private set; }

    private System.Windows.Forms.NotifyIcon? _tray;
    private Mutex? _mutex;
    private bool _ownsMutex;

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance: a second launch would install a second mouse hook
        // (rules firing twice) and a second tray icon. Surface the first instead.
        ShowWindowMessage = RegisterWindowMessage("RodentShowWindow");
        _mutex = new Mutex(true, @"Local\RodentSingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            PostMessage(HWND_BROADCAST, ShowWindowMessage, IntPtr.Zero, IntPtr.Zero);
            Quitting = true;
            Shutdown();
            return;
        }

        Profiles = ProfilesConfig.Load();
        Automation = new AutomationService(Profiles);
        Automation.DeviceProvider = () =>
            Dispatcher.Invoke(() => (MainWindow as MainWindow)?.SelectedDevice);
        Automation.Start();
        SetupTray();
    }

    /// <summary>Persist edited profiles and hand them to the running engine.</summary>
    public void SaveProfiles()
    {
        Profiles.Save();
        Automation.SetProfiles(Profiles);
    }

    private void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = "Rodent",
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Rodent", null, (_, _) => ShowMain());
        menu.Items.Add("Quit", null, (_, _) => { Quitting = true; Shutdown(); });
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, a) =>
        {
            if (a.Button == System.Windows.Forms.MouseButtons.Left) ShowMain();
        };
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var sri = GetResourceStream(new Uri("pack://application:,,,/Assets/rodent.ico"));
            if (sri != null) return new System.Drawing.Icon(sri.Stream);
        }
        catch { /* fall back to the stock icon */ }
        return System.Drawing.SystemIcons.Application;
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
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        if (_mutex != null && _ownsMutex) { try { _mutex.ReleaseMutex(); } catch { } }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
