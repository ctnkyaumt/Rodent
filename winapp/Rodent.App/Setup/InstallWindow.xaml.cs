using System.Windows;
using Rodent.Core.Diagnostics;

namespace Rodent.App.Setup;

public enum SetupMode
{
    /// <summary>First run from an un-installed copy: offers "run without installing".</summary>
    FirstRun,
    /// <summary>Launched with --install.</summary>
    Install,
    /// <summary>Launched with --uninstall (or from Apps &amp; features).</summary>
    Uninstall,
}

public enum SetupResult { Cancelled, Installed, RunPortable, Uninstalled }

public partial class InstallWindow : Window
{
    private readonly SetupMode _mode;
    public SetupResult Result { get; private set; } = SetupResult.Cancelled;

    public InstallWindow(SetupMode mode)
    {
        InitializeComponent();
        _mode = mode;
        PathText.Text = Installer.InstallRoot;

        switch (mode)
        {
            case SetupMode.Uninstall:
                Title = "Uninstall Rodent";
                Headline.Text = "Remove Rodent";
                Subhead.Text = "Rodent will be removed from this PC, along with its shortcuts and " +
                               "start-with-Windows entry. Device settings already written to your " +
                               "mouse stay on the mouse.";
                StartupCheck.Visibility = Visibility.Collapsed;
                DesktopCheck.Visibility = Visibility.Collapsed;
                PurgeCheck.Visibility = Visibility.Visible;
                SkipBtn.Visibility = Visibility.Collapsed;
                OkBtn.Content = "Uninstall";
                break;

            case SetupMode.Install:
                SkipBtn.Visibility = Visibility.Collapsed;
                if (Installer.IsInstalled)
                {
                    Headline.Text = "Update Rodent";
                    Subhead.Text = $"Version {Installer.InstalledVersion ?? "?"} is already installed and " +
                                   $"will be replaced with {App.VersionNumber}.";
                    OkBtn.Content = "Update";
                }
                StartupCheck.IsChecked = Installer.StartupEnabled || !Installer.IsInstalled;
                break;

            case SetupMode.FirstRun:
                Subhead.Text = "Rodent is running from " +
                               (System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? "its current folder") +
                               ". Install it to get a Start menu entry, an uninstaller, and a stable " +
                               "location for the start-with-Windows option. No admin rights needed.";
                break;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            if (_mode == SetupMode.Uninstall)
            {
                Installer.Uninstall(PurgeCheck.IsChecked == true, Status);
                Result = SetupResult.Uninstalled;
                MessageBox.Show(this, "Rodent has been removed.", "Rodent",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                Installer.Install(StartupCheck.IsChecked == true, DesktopCheck.IsChecked == true, Status);
                Result = SetupResult.Installed;
            }
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Exception(ex, _mode == SetupMode.Uninstall ? "uninstall" : "install");
            SetBusy(false);
            Status("Failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, _mode == SetupMode.Uninstall ? "Uninstall failed" : "Install failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Result = SetupResult.RunPortable;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = SetupResult.Cancelled;
        DialogResult = false;
        Close();
    }

    private void Status(string text)
    {
        StatusText.Text = text;
        // Setup runs on the UI thread; pump so progress lines actually appear.
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void SetBusy(bool busy)
    {
        OkBtn.IsEnabled = !busy;
        SkipBtn.IsEnabled = !busy;
        CancelBtn.IsEnabled = !busy;
        StartupCheck.IsEnabled = !busy;
        DesktopCheck.IsEnabled = !busy;
        PurgeCheck.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }
}
