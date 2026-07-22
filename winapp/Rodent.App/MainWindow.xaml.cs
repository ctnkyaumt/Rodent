using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Rodent.Core.Automation;
using Rodent.Core.Devices;

namespace Rodent.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DeviceViewModel> _devices = new();
    private readonly DispatcherTimer _rescanDebounce;
    private bool _loading;
    private bool _reloadQueued;
    private App AppInstance => (App)Application.Current;

    public MainWindow()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        InitStartupToggle();
        PageButtons.ProfileSelected += OnAssignmentsProfileSelected;
        // Not on Loaded: when started with --tray the window stays hidden (Loaded
        // never fires) but devices and per-app profiles must still come up.
        Dispatcher.InvokeAsync(async () => { SetupPerApp(); await LoadDevicesAsync(); });

        // Hotplug: HidSharp raises Changed on any HID arrival/removal; debounce the
        // burst of events one physical plug generates, then rescan.
        _rescanDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _rescanDebounce.Tick += async (_, _) => { _rescanDebounce.Stop(); await LoadDevicesAsync(); };
        HidSharp.DeviceList.Local.Changed += (_, _) =>
            Dispatcher.BeginInvoke(() => { _rescanDebounce.Stop(); _rescanDebounce.Start(); });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            // Borderless (WindowStyle=None) windows maximize over the taskbar by
            // default; clamp the maximized size to the monitor's work area.
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref mi))
                {
                    mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
                    mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
                    mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
                    mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
                    Marshal.StructureToPtr(mmi, lParam, false);
                    handled = true;
                }
            }
        }
        else if (App.ShowWindowMessage != 0 && msg == (int)App.ShowWindowMessage)
        {
            // A second instance launched and asked us to come forward.
            Show();
            WindowState = WindowState.Normal;
            Activate();
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (MaxBtn != null)
            MaxBtn.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    // ---- devices ----
    private async System.Threading.Tasks.Task LoadDevicesAsync()
    {
        if (_loading) { _reloadQueued = true; return; }
        _loading = true;
        DeviceStatus.Text = "Scanning…";
        DeviceStatus.Visibility = Visibility.Visible;

        string? keep = (DeviceList.SelectedItem as DeviceViewModel)?.Device.DevicePath;
        var old = _devices.ToList();
        var found = await System.Threading.Tasks.Task.Run(() =>
            DeviceManager.Discover().Select(d => new DeviceViewModel(d)).ToList());

        _devices.Clear();
        foreach (var vm in found) _devices.Add(vm);
        foreach (var vm in old) vm.Device.Dispose();

        if (_devices.Count == 0)
        {
            DeviceStatus.Text = "No Logitech devices found.";
            ClearDevicePages();
        }
        else
        {
            DeviceStatus.Visibility = Visibility.Collapsed;
            int idx = keep != null ? found.FindIndex(v => v.Device.DevicePath == keep) : -1;
            DeviceList.SelectedIndex = idx >= 0 ? idx : 0;
        }

        _loading = false;
        if (_reloadQueued) { _reloadQueued = false; await LoadDevicesAsync(); }
    }

    private void ClearDevicePages()
    {
        TitleDevice.Text = "";
        HeaderName.Text = "";
        InfoStrip.ItemsSource = null;
        SettingsPanel.ItemsSource = null;
        PageButtons.ShowEmpty("No devices found");
    }

    /// <summary>Close all HID handles (app quit).</summary>
    public void DisposeDevices()
    {
        foreach (var vm in _devices) vm.Device.Dispose();
        _devices.Clear();
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await LoadDevicesAsync();

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceViewModel vm) return;
        TitleDevice.Text = vm.Name;
        HeaderName.Text = vm.Name;
        InfoStrip.ItemsSource = vm.Info;
        SettingsPanel.ItemsSource = vm.Settings;
        PageButtons.Load(vm);
        LoadLighting(vm);
        LoadDpiPanel(vm);
    }

    // ---- Lighting (live over 0x1300; follows the Assignments profile picker) ----
    private DeviceViewModel? _fxVm;
    private bool _fxLoading;
    private Rodent.Core.Hidpp.ProfileEdit.LightingConfig? _fxDeviceCfg; // what the mouse itself is set to

    // Effect list index 3 = hand the LEDs back to the mouse's own firmware.
    private const int FxFirmwareIndex = 3;

    private void LoadLighting(DeviceViewModel vm)
    {
        _fxVm = vm;
        System.Threading.Tasks.Task.Run(() =>
        {
            var cfg = vm.Device.ReadLighting();
            Dispatcher.Invoke(() =>
            {
                if (cfg == null)
                {
                    FxCard.Visibility = Visibility.Collapsed;
                    LightingNote.Text = "This device has no controllable lighting.";
                    return;
                }
                FxCard.Visibility = Visibility.Visible;
                _fxDeviceCfg = cfg;
                if (FxCombo.ItemsSource == null)
                {
                    _fxLoading = true;
                    FxCombo.ItemsSource = new[] { "Off", "Fixed", "Breathing", "Mouse default (DPI stripes)" };
                    BuildFxPresets();
                    _fxLoading = false;
                }
                ReloadFxProfiles(); // also loads the controls for the selected profile
            });
        });
    }

    /// <summary>Mirror the Assignments tab's profile list so both tabs edit the same profile.</summary>
    public void ReloadFxProfiles()
    {
        if (FxProfile == null) return;
        _fxLoading = true;
        var items = new List<string> { "Onboard (hardware)" };
        items.AddRange(AppInstance.Profiles.Profiles.Select(p =>
            p.App == "*" ? p.Name : $"{p.Name} ({p.App}.exe)"));
        FxProfile.ItemsSource = items;
        int want = PageButtons.SelectedProfileIndex;
        FxProfile.SelectedIndex = want >= 0 && want < items.Count ? want : 0;
        _fxLoading = false;
        LoadFxForSelectedProfile();
    }

    private AppProfile? SelectedFxProfile()
    {
        int i = FxProfile.SelectedIndex;
        return i <= 0 ? null : AppInstance.Profiles.Profiles.ElementAtOrDefault(i - 1);
    }

    /// <summary>
    /// Show the selected profile's stored lighting. Onboard (and a profile that has
    /// no lighting saved yet) shows what the mouse itself is set to — without this
    /// fallback, switching profiles often changed nothing on screen and the
    /// dropdown looked dead.
    /// </summary>
    private void LoadFxForSelectedProfile()
    {
        var light = SelectedFxProfile()?.Lighting;
        int mode; double shade; int period; bool stripAlways;
        if (light != null)
        {
            mode = light.FirmwareMode ? FxFirmwareIndex : light.Mode switch
            {
                Rodent.Core.Hidpp.ProfileEdit.LedFixed => 1,
                Rodent.Core.Hidpp.ProfileEdit.LedBreathing => 2,
                _ => 0,
            };
            (shade, period, stripAlways) = (light.Shade, light.PeriodMs, light.StripAlwaysOn);
        }
        else if (_fxDeviceCfg is { } cfg)
        {
            mode = cfg.FirmwareMode ? FxFirmwareIndex : cfg.Mode switch
            {
                Rodent.Core.Hidpp.ProfileEdit.LedFixed => 1,
                Rodent.Core.Hidpp.ProfileEdit.LedBreathing => 2,
                _ => 0,
            };
            (shade, period, stripAlways) = (Math.Max(cfg.G, cfg.B), cfg.PeriodMs, cfg.StripAlwaysOn);
        }
        else { FxSyncVisuals(); return; }

        _fxLoading = true;
        FxCombo.SelectedIndex = mode;
        FxShade.Value = shade;
        FxSpeed.Value = period is >= 1000 and <= 10000 ? period : 5000;
        FxStripAlways.IsChecked = stripAlways;
        _fxLoading = false;
        FxSyncVisuals();
    }

    /// <summary>Profile picked on the Lighting tab — keep the Assignments tab in step.</summary>
    private void FxProfile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_fxLoading) return;
        PageButtons.SelectProfile(FxProfile.SelectedIndex);
        LoadFxForSelectedProfile();
        ApplyFxLive();
    }

    /// <summary>Profile picked on the Assignments tab — follow it here.</summary>
    private void OnAssignmentsProfileSelected(int index)
    {
        if (FxProfile == null || index == FxProfile.SelectedIndex) return;
        _fxLoading = true;
        FxProfile.SelectedIndex = index;
        _fxLoading = false;
        LoadFxForSelectedProfile();
        ApplyFxLive();
    }

    /// <summary>
    /// Picking a profile switches the mouse to its lighting right away (live drive
    /// only, no flash wear) — Apply is for changing/saving the selected profile.
    /// </summary>
    private void ApplyFxLive()
    {
        var vm = _fxVm;
        if (vm == null || FxCard.Visibility != Visibility.Visible) return;
        var cfg = CurrentFxConfig();
        System.Threading.Tasks.Task.Run(() => vm.Device.WriteLighting(cfg, persist: false));
    }

    private void BuildFxPresets()
    {
        FxPresets.Children.Clear();
        foreach (int shade in new[] { 60, 120, 190, 255 })
        {
            var b = new Button
            {
                Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(0, (byte)shade, (byte)shade)),
                BorderBrush = (Brush)FindResource("Border"), Padding = new Thickness(0),
            };
            int sh = shade;
            b.Click += (_, _) => FxShade.Value = sh;
            FxPresets.Children.Add(b);
        }
    }

    private void FxSyncVisuals()
    {
        // ValueChanged fires during InitializeComponent before siblings exist.
        if (FxCombo == null || FxColorGroup == null || FxSwatch == null || FxHex == null
            || FxSpeedLabel == null || LightingNote == null || FxStripAlways == null) return;

        int mode = FxCombo.SelectedIndex;
        bool firmware = mode == FxFirmwareIndex;
        FxColorGroup.Visibility = mode is 1 or 2 ? Visibility.Visible : Visibility.Collapsed;
        FxSpeedGroup.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
        FxStripAlways.Visibility = firmware ? Visibility.Visible : Visibility.Collapsed;
        byte v = (byte)FxShade.Value;
        FxSwatch.Background = new SolidColorBrush(Color.FromRgb(0, v, v));
        FxHex.Text = $"#00{v:X2}{v:X2}";
        FxSpeedLabel.Text = $"{(int)FxSpeed.Value} ms";
        LightingNote.Text = firmware
            ? "The mouse drives both LEDs itself: the three stripes show which DPI slot is active (more lit = faster) and the G logo breathes at its factory setting. They cannot be lit any other way, so this rules out custom effects."
            : "Custom effects drive the G logo; the three stripes stay dark because the mouse only lights them itself. Lighting is saved per profile — pick one above.";
    }

    private void FxCombo_Changed(object sender, SelectionChangedEventArgs e) { if (!_fxLoading) FxSyncVisuals(); }
    private void FxShade_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_fxLoading) FxSyncVisuals(); }
    private void FxSpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_fxLoading) FxSyncVisuals(); }

    private Rodent.Core.Hidpp.ProfileEdit.LightingConfig CurrentFxConfig()
    {
        bool firmware = FxCombo.SelectedIndex == FxFirmwareIndex;
        int mode = FxCombo.SelectedIndex switch
        {
            1 => Rodent.Core.Hidpp.ProfileEdit.LedFixed,
            2 => Rodent.Core.Hidpp.ProfileEdit.LedBreathing,
            _ => Rodent.Core.Hidpp.ProfileEdit.LedOff,
        };
        byte v = (byte)FxShade.Value;
        return new Rodent.Core.Hidpp.ProfileEdit.LightingConfig(
            mode, 0, v, v, (int)FxSpeed.Value, firmware, FxStripAlways.IsChecked == true);
    }

    private void FxApply_Click(object sender, RoutedEventArgs e)
    {
        var vm = _fxVm;
        if (vm == null) return;
        var cfg = CurrentFxConfig();
        var profile = SelectedFxProfile();

        if (profile != null)
        {
            profile.Lighting = new LightingSetting
            {
                Mode = cfg.Mode, Shade = cfg.G, PeriodMs = cfg.PeriodMs,
                FirmwareMode = cfg.FirmwareMode, StripAlwaysOn = cfg.StripAlwaysOn,
            };
            AppInstance.SaveProfiles();
            RefreshProfilesList();
        }

        // Persist to the mouse only for the onboard profile; a per-app profile's
        // lighting is driven live whenever its app comes forward (no flash wear).
        bool persist = profile == null;
        if (profile == null) _fxDeviceCfg = cfg; // keep the Onboard fallback current
        FxApply.IsEnabled = false;
        FxStatus.Text = "Applying…";
        System.Threading.Tasks.Task.Run(() =>
        {
            bool ok = vm.Device.WriteLighting(cfg, persist);
            Dispatcher.Invoke(() =>
            {
                FxApply.IsEnabled = true;
                FxStatus.Text = !ok ? "Write failed. If G HUB is running, quit it."
                    : profile == null ? "Applied and saved to the mouse."
                    : $"Saved to {profile.Name} — applies when that app is in front.";
            });
        });
    }

    /// <summary>Foreground app changed: switch to that profile's lighting, if it has one.</summary>
    private string _fxLastApp = "";
    private void ApplyProfileLighting(string app)
    {
        var device = SelectedDevice;
        if (device == null || app == _fxLastApp) return;
        _fxLastApp = app;
        var light = AppInstance.Profiles.ResolveLighting(app);
        if (light == null) return;
        var cfg = new Rodent.Core.Hidpp.ProfileEdit.LightingConfig(
            light.Mode, 0, (byte)light.Shade, (byte)light.Shade, light.PeriodMs,
            light.FirmwareMode, light.StripAlwaysOn);
        System.Threading.Tasks.Task.Run(() => device.WriteLighting(cfg, persist: false));
    }

    // ---- DPI panel (G HUB-style slots, stored in the onboard profile) ----
    private DeviceViewModel? _dpiVm;
    private Rodent.Core.Hidpp.ProfileEdit.DpiConfig? _dpi;
    private List<int> _dpiChoices = new();
    private int _dpiSel;
    private bool _dpiLoading;

    private void LoadDpiPanel(DeviceViewModel vm)
    {
        _dpiVm = vm;
        System.Threading.Tasks.Task.Run(() =>
        {
            var cfg = vm.Device.ReadDpiProfile();
            var choices = vm.Device.DpiChoices();
            Dispatcher.Invoke(() =>
            {
                _dpi = cfg;
                _dpiChoices = choices;
                if (cfg == null || choices.Count == 0)
                {
                    DpiCard.Visibility = Visibility.Collapsed;
                    SettingsPanel.ItemsSource = vm.Settings;
                    return;
                }
                DpiCard.Visibility = Visibility.Visible;
                SettingsPanel.ItemsSource = vm.Settings.Where(s => s.Name != "dpi" && s.Name != "report_rate").ToList();
                _dpiSel = Math.Clamp(cfg.DefaultIndex, 0, 4);
                _dpiLoading = true; // changing Min/Max coerces Value and fires ValueChanged
                DpiSlider.Minimum = choices[0];
                DpiSlider.Maximum = choices[^1];
                _dpiLoading = false;
                BuildRateRow(vm);
                RenderDpi();
            });
        });
    }

    private void RenderDpi()
    {
        var cfg = _dpi;
        if (cfg == null) return;
        DpiSlotRow.Children.Clear();
        for (int i = 0; i < 5; i++)
        {
            int val = cfg.Slots[i];
            bool selected = i == _dpiSel;
            var text = new TextBlock
            {
                Text = val > 0 ? val.ToString() : "+",
                FontSize = 15, FontWeight = FontWeights.SemiBold,
                Foreground = i == cfg.ShiftIndex ? new SolidColorBrush(Color.FromRgb(0xE4, 0xA0, 0x35))
                                                 : (Brush)FindResource(val > 0 ? "Text" : "Muted"),
                TextDecorations = i == cfg.DefaultIndex && val > 0 ? TextDecorations.Underline : null,
            };
            var b = new Border
            {
                Child = text, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0),
                CornerRadius = new CornerRadius(6), Cursor = System.Windows.Input.Cursors.Hand,
                Background = selected ? new SolidColorBrush(Color.FromArgb(0x50, 0x35, 0x84, 0xE4))
                                      : (Brush)FindResource("Card"),
                BorderBrush = (Brush)FindResource(selected ? "Accent" : "Border"),
                BorderThickness = new Thickness(1),
            };
            int idx = i;
            b.MouseLeftButtonUp += (_, _) =>
            {
                if (_dpi == null) return;
                if (_dpi.Slots[idx] == 0)
                    _dpi.Slots[idx] = 800; // click an empty slot to add one, like G HUB
                _dpiSel = idx;
                RenderDpi();
            };
            DpiSlotRow.Children.Add(b);
        }

        int cur = cfg.Slots[_dpiSel];
        _dpiLoading = true;
        DpiSlider.Value = cur > 0 ? cur : 800;
        _dpiLoading = false;
        DpiValueLabel.Text = cur > 0 ? cur.ToString() : "";
    }

    private int SnapDpi(double raw)
    {
        if (_dpiChoices.Count == 0) return (int)raw;
        return _dpiChoices.OrderBy(c => Math.Abs(c - raw)).First();
    }

    private void DpiSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_dpiLoading || _dpi == null) return;
        int v = SnapDpi(e.NewValue);
        _dpi.Slots[_dpiSel] = v;
        DpiValueLabel.Text = v.ToString();
        if (DpiSlotRow.Children.Count > _dpiSel && DpiSlotRow.Children[_dpiSel] is Border b && b.Child is TextBlock t)
            t.Text = v.ToString();
    }

    private void DpiMakeCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_dpi == null || _dpi.Slots[_dpiSel] == 0) return;
        _dpi = _dpi with { DefaultIndex = _dpiSel };
        RenderDpi();
    }

    private void DpiMakeShift_Click(object sender, RoutedEventArgs e)
    {
        if (_dpi == null || _dpi.Slots[_dpiSel] == 0) return;
        _dpi = _dpi with { ShiftIndex = _dpiSel };
        RenderDpi();
    }

    private void BuildRateRow(DeviceViewModel vm)
    {
        RateRow.Children.Clear();
        var rate = vm.Settings.FirstOrDefault(s => s.Name == "report_rate") as ChoiceSettingViewModel;
        var msChoices = rate != null ? rate.Choices.Select(c => c.Value).ToList() : new List<int> { 1, 2, 4, 8 };
        foreach (int ms in msChoices.OrderBy(m => m))
        {
            var rb = new RadioButton
            {
                Content = $"{1000 / ms}", GroupName = "rate", Margin = new Thickness(0, 0, 16, 0),
                Foreground = (Brush)FindResource("Text"), FontSize = 13,
                IsChecked = _dpi != null && _dpi.ReportRateMs == ms,
            };
            int m = ms;
            rb.Checked += (_, _) => { if (_dpi != null) _dpi = _dpi with { ReportRateMs = m }; };
            RateRow.Children.Add(rb);
        }
    }

    private void DpiApply_Click(object sender, RoutedEventArgs e)
    {
        var vm = _dpiVm;
        var cfg = _dpi;
        if (vm == null || cfg == null) return;
        DpiApply.IsEnabled = false;
        DpiStatus.Text = "Writing…";
        System.Threading.Tasks.Task.Run(() =>
        {
            bool ok = vm.Device.WriteDpiProfile(cfg);
            Dispatcher.Invoke(() =>
            {
                DpiApply.IsEnabled = true;
                DpiStatus.Text = ok ? "Applied." : "Write failed. If G HUB is running, quit it.";
            });
        });
    }

    private void DpiRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_dpi == null) return;
        _dpi = _dpi with { Slots = new[] { 400, 800, 1600, 3200, 0 }, DefaultIndex = 2, ShiftIndex = 0 };
        _dpiSel = 2;
        RenderDpi();
        DpiStatus.Text = "Defaults staged - press Apply to write.";
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PageButtons == null) return; // during init
        PageButtons.Visibility = Vis(TabButtons);
        PageDpi.Visibility = Vis(TabDpi);
        PageLighting.Visibility = Vis(TabLighting);
        PagePerApp.Visibility = Vis(TabPerApp);
    }

    private static Visibility Vis(System.Windows.Controls.Primitives.ToggleButton t) =>
        t.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    // ---- Per-App profiles ----
    private bool _armSync; // true while code (not the user) sets ArmedCheck

    /// <summary>Device currently selected in the sidebar (arming target).</summary>
    public Rodent.Core.Devices.LogiDevice? SelectedDevice =>
        (DeviceList.SelectedItem as DeviceViewModel)?.Device;

    private void SetupPerApp()
    {
        AppInstance.Automation.AppChanged += app =>
            Dispatcher.Invoke(() =>
            {
                CurrentAppLabel.Text = $"Active app: {app}";
                ApplyProfileLighting(app);
            });
        CurrentAppLabel.Text = $"Active app: {AppInstance.Automation.CurrentApp}";
        _armSync = true;
        ArmedCheck.IsChecked = AppInstance.Profiles.Enabled;
        _armSync = false;
        RefreshProfilesList();
    }

    public void RefreshProfilesList()
    {
        ProfilesList.Children.Clear();
        foreach (var profile in AppInstance.Profiles.Profiles)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            int bound = profile.Buttons.Count(b => b.Value.Kind != BindingKind.Default);
            string app = profile.App == "*" ? "all other programs" : $"{profile.App}.exe";
            var txt = new TextBlock
            {
                Foreground = (Brush)FindResource("Text"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                Text = $"{profile.Name}  ·  {app}  ·  {bound} button(s) bound",
            };
            grid.Children.Add(txt);

            if (profile.App != "*") // the fallback profile must always exist
            {
                var del = new Button { Content = "Remove", Width = 80 };
                var p = profile;
                del.Click += (_, _) =>
                {
                    AppInstance.Profiles.Profiles.Remove(p);
                    AppInstance.SaveProfiles();
                    RefreshProfilesList();
                    PageButtons.ReloadProfiles();
                };
                Grid.SetColumn(del, 1);
                grid.Children.Add(del);
            }

            ProfilesList.Children.Add(new Border
            {
                Background = (Brush)FindResource("Card"), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 6),
                Child = grid,
            });
        }
        if (AppInstance.Profiles.Profiles.Count == 0)
            ProfilesList.Children.Add(new TextBlock
            {
                Text = "No profiles yet — add one, then assign its buttons on the Assignments tab.",
                Foreground = (Brush)FindResource("Muted"), FontSize = 12, Margin = new Thickness(2),
            });
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        string? app = Dialogs.PickApp(this);
        if (app == null) return;
        var cfg = AppInstance.Profiles;
        if (cfg.Profiles.All(p => p.App != app))
        {
            cfg.Wildcard();
            cfg.Profiles.Add(new AppProfile { Name = app, App = app });
            AppInstance.SaveProfiles();
        }
        RefreshProfilesList();
        PageButtons.ReloadProfiles();
        ReloadFxProfiles();
    }

    private void SaveProfiles_Click(object sender, RoutedEventArgs e) => AppInstance.SaveProfiles();

    private void Armed_Changed(object sender, RoutedEventArgs e)
    {
        if (_armSync) return;
        bool arm = ArmedCheck.IsChecked == true;
        var device = SelectedDevice;
        if (device == null)
        {
            _armSync = true; ArmedCheck.IsChecked = !arm; _armSync = false;
            Dialogs.Info(this, "Connect the mouse first.");
            return;
        }

        // Snapshot pre-arm labels for wildcard migration (RemapButton refreshes them).
        var labels = device.Buttons.ToDictionary(b => b.Index, b => b.Label);
        var cfg = AppInstance.Profiles;
        ArmedCheck.IsEnabled = false;

        System.Threading.Tasks.Task.Run(() =>
        {
            (bool ok, string? error) result = arm ? ProfileArmer.Arm(device, cfg)
                                                  : ProfileArmer.Disarm(device, cfg);
            if (arm && result.ok) MigrateWildcard(cfg, labels);

            Dispatcher.Invoke(() =>
            {
                ArmedCheck.IsEnabled = true;
                _armSync = true;
                ArmedCheck.IsChecked = cfg.Enabled;
                _armSync = false;
                AppInstance.SaveProfiles();
                RefreshProfilesList();
                PageButtons.ReloadProfiles();
                PageButtons.Load((DeviceViewModel)DeviceList.SelectedItem!);
                if (!result.ok) Dialogs.Info(this, $"Per-app profiles: {result.error}");
            });
        });
    }

    /// <summary>
    /// First arm: seed the fallback profile from what the side buttons used to do,
    /// so unlisted apps keep familiar behavior (clicks and key combos translate;
    /// DPI functions and complex macros stay hardware-only and are skipped).
    /// </summary>
    private static void MigrateWildcard(ProfilesConfig cfg, Dictionary<int, string> labels)
    {
        var w = cfg.Wildcard();
        if (w.Buttons.Count > 0) return;

        for (int b = ProfilesConfig.FirstButton; b <= ProfilesConfig.LastButton; b++)
        {
            if (!cfg.HwBackup.TryGetValue(b, out var hex)) continue;
            byte[] bytes;
            try { bytes = Convert.FromHexString(hex); } catch { continue; }
            if (bytes.Length < 4) continue;

            if (bytes[0] == 0x80 && bytes[1] == 0x01) // BUTTON: mouse-mask
            {
                int mask = (bytes[2] << 8) | bytes[3];
                string? name = mask switch
                {
                    0x0001 => "Left Click", 0x0002 => "Right Click", 0x0004 => "Middle Click",
                    0x0008 => "Back", 0x0010 => "Forward", _ => null,
                };
                if (name != null)
                    w.Buttons[b] = new ButtonBinding { Kind = BindingKind.MouseClick, Text = name };
            }
            else if (bytes[0] == 0x80 && bytes[1] == 0x02) // MODIFIER_AND_KEY
            {
                ushort vk = Rodent.Core.Hidpp.Macro.HidToVk(bytes[3]);
                if (vk == 0) continue;
                var mods = new List<ushort>();
                if ((bytes[2] & 0x01) != 0) mods.Add(InputInjector.VK_CONTROL);
                if ((bytes[2] & 0x02) != 0) mods.Add(InputInjector.VK_SHIFT);
                if ((bytes[2] & 0x04) != 0) mods.Add(InputInjector.VK_MENU);
                if ((bytes[2] & 0x08) != 0) mods.Add(InputInjector.VK_LWIN);
                w.Buttons[b] = new ButtonBinding
                {
                    Kind = BindingKind.KeyChord,
                    Text = (labels.TryGetValue(b, out var l) ? l : "chord").ToLowerInvariant(),
                    VirtualKey = vk, Modifiers = mods.ToArray(),
                };
            }
            else if ((bytes[0] >> 4) <= 0x1 && labels.TryGetValue(b, out var label))
            {
                // Macro whose decoded label reads like a chord ("Ctrl+Tab") → replayable.
                var parsed = InputInjector.ParseChord(label);
                if (parsed != null)
                    w.Buttons[b] = new ButtonBinding
                    {
                        Kind = BindingKind.KeyChord, Text = label.ToLowerInvariant(),
                        VirtualKey = parsed.Value.vk, Modifiers = parsed.Value.mods,
                    };
            }
        }
        cfg.Save();
    }

    // ---- launch at startup (HKCU Run key — per-user, no admin needed) ----
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Rodent";
    private bool _startupSync;

    private void InitStartupToggle()
    {
        _startupSync = true;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
            StartupCheck.IsChecked = key?.GetValue(RunValueName) != null;
        }
        catch { StartupCheck.IsEnabled = false; }
        _startupSync = false;
    }

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (_startupSync) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (StartupCheck.IsChecked == true)
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --tray"); // start hidden in the tray
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Revert the box if the registry write failed.
            _startupSync = true;
            StartupCheck.IsChecked = StartupCheck.IsChecked != true;
            _startupSync = false;
        }
    }

    // ---- window / tray ----
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AppInstance.Quitting) { e.Cancel = true; Hide(); } // minimize to tray
        base.OnClosing(e);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---- Win32 (work-area maximize) ----
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
}
