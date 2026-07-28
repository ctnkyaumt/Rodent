using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Rodent.Core.Automation;
using Rodent.Core.Hidpp;

namespace Rodent.App.Controls;

/// <summary>
/// G HUB-style assignments view. The profile dropdown switches between the
/// hardware "Onboard" profile (flash writes, as before) and per-app software
/// profiles (bindings for buttons 4-8, executed by the signal-key engine).
/// </summary>
public partial class MouseButtonsView : UserControl
{
    // One flash write at a time per view; labels are disabled while one runs so a
    // second click can't start a competing read-modify-write cycle.
    private bool _busy;
    private readonly List<Border> _labels = new();
    private readonly DispatcherTimer _toastTimer;

    private DeviceViewModel? _vm;
    private AppProfile? _profile;      // null = Onboard (hardware) profile
    private bool _rebuildingCombo;

    private App AppInstance => (App)Application.Current;

    public MouseButtonsView()
    {
        InitializeComponent();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
    }

    public void Load(DeviceViewModel vm)
    {
        // Needs a driver that can list and remap buttons; brands whose protocol
        // isn't ported that far are recognised but not editable here.
        if (vm.ButtonDev == null) { _vm = null; ShowEmpty($"Button assignments aren't supported on {vm.Name} yet."); return; }
        _vm = vm;
        UntestedBanner.Visibility = vm.Untested ? Visibility.Visible : Visibility.Collapsed;
        if (vm.Untested)
            UntestedText.Text = $"Experimental: {vm.Name} hasn't been verified. Reading works, but onboard " +
                "flash writes (button remaps, macros, DPI slots, saved lighting) assume the G402 memory layout " +
                "and could be wrong on this device. Software per-app profiles are safe.";
        ReloadProfiles();
        Render();
        RefreshModeBanner();
    }

    /// <summary>Rebuild the profile dropdown (keeps selection when possible).</summary>
    public void ReloadProfiles()
    {
        _rebuildingCombo = true;
        var items = new List<string> { "Onboard (hardware)" };
        items.AddRange(AppInstance.Profiles.Profiles.Select(p => ProfileLabel(p)));
        string? keep = ProfileCombo.SelectedItem as string;
        ProfileCombo.ItemsSource = items;
        int idx = keep != null ? items.IndexOf(keep) : -1;
        ProfileCombo.SelectedIndex = idx >= 0 ? idx : 0;
        _rebuildingCombo = false;
        SyncSelectedProfile();
    }

    private static string ProfileLabel(AppProfile p) =>
        p.App == "*" ? $"{p.Name}" : $"{p.Name} ({p.App}.exe)";

    private void SyncSelectedProfile()
    {
        int i = ProfileCombo.SelectedIndex;
        _profile = i <= 0 ? null : AppInstance.Profiles.Profiles.ElementAtOrDefault(i - 1);
    }

    /// <summary>Raised when the user picks a profile, so the Lighting tab follows along.</summary>
    public event Action<int>? ProfileSelected;

    /// <summary>Index of the selected profile (0 = the onboard/hardware profile).</summary>
    public int SelectedProfileIndex => ProfileCombo.SelectedIndex;

    /// <summary>Select a profile without echoing the change back to the caller.</summary>
    public void SelectProfile(int index)
    {
        if (index < 0 || index >= ProfileCombo.Items.Count || index == ProfileCombo.SelectedIndex) return;
        _rebuildingCombo = true;
        ProfileCombo.SelectedIndex = index;
        _rebuildingCombo = false;
        SyncSelectedProfile();
        Render();
        RefreshModeBanner();
    }

    private void ProfileCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingCombo) return;
        SyncSelectedProfile();
        Render();
        RefreshModeBanner();
        ProfileSelected?.Invoke(ProfileCombo.SelectedIndex);
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        string? app = Dialogs.PickApp(Window.GetWindow(this));
        if (app == null) return;
        var cfg = AppInstance.Profiles;
        var existing = cfg.Profiles.FirstOrDefault(p => p.App == app);
        if (existing == null)
        {
            cfg.Wildcard(); // make sure the fallback exists first
            existing = new AppProfile { Name = app, App = app };
            cfg.Profiles.Add(existing);
            AppInstance.SaveProfiles();
        }
        ReloadProfiles();
        ProfileCombo.SelectedIndex = 1 + cfg.Profiles.IndexOf(existing);
        var main = Window.GetWindow(this) as MainWindow;
        main?.RefreshProfilesList();
        main?.ReloadFxProfiles(); // the Lighting tab mirrors this list
    }

    private void EnableMode_Click(object sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm == null) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            bool ok = vm.ButtonDev!.EnableOnboardMode();
            Dispatcher.Invoke(() =>
            {
                RefreshModeBanner();
                ShowToast(ok ? "Onboard mode enabled — assignments are live."
                             : "Couldn't enable onboard mode. If G HUB is running, quit it.");
            });
        });
    }

    /// <summary>Show/hide the "onboard mode off" warning (async device read).</summary>
    public void RefreshModeBanner()
    {
        var vm = _vm;
        if (vm == null || _profile != null) { ModeBanner.Visibility = Visibility.Collapsed; return; }
        System.Threading.Tasks.Task.Run(() =>
        {
            bool onboard;
            try { onboard = vm.ButtonDev!.IsOnboardMode(); } catch { onboard = true; }
            Dispatcher.Invoke(() =>
                ModeBanner.Visibility = onboard ? Visibility.Collapsed : Visibility.Visible);
        });
    }

    private void Render()
    {
        CopyOnboardBtn.Visibility = _profile != null ? Visibility.Visible : Visibility.Collapsed;
        Root.Children.Clear();
        _labels.Clear();
        var vm = _vm;
        if (vm == null) { ShowEmpty("No devices found"); return; }

        var art = SvgArt.Load(vm.VendorId, vm.ProductId);
        if (art == null) { ShowEmpty("No illustration for this device"); return; }
        NoArt.Visibility = Visibility.Collapsed;

        Root.Width = art.Width;
        Root.Height = art.Height;

        var img = new Image { Source = art.Image, Width = art.Width, Height = art.Height, IsHitTestVisible = false };
        Canvas.SetLeft(img, 0);
        Canvas.SetTop(img, 0);
        Root.Children.Add(img);

        bool armed = AppInstance.Profiles.Enabled;
        foreach (var btn in vm.ButtonDev!.Buttons)
        {
            var a = art.Anchors.FirstOrDefault(x => x.Index == btn.Index);
            if (a == null) continue;

            if (_profile == null)
            {
                // Hardware profile. While armed, 4-8 belong to the signal keys —
                // except the ones the user asked to keep on the mouse.
                bool locked = armed && btn.Index >= ProfilesConfig.FirstButton
                                    && !AppInstance.Profiles.IsHardware(btn.Index);
                // A macro has no name on the device, only instructions — show the name
                // the user gave it and keep the decoded contents as the tooltip.
                string? macroName = btn.IsMacro ? MacroNames.Get(vm.VendorId, vm.ProductId, btn.Index) : null;
                DrawLabel(a, locked ? "Per-app" : macroName ?? btn.Label,
                    locked ? "Managed by per-app profiles — disable them in the Per-App tab to edit"
                           : macroName != null ? $"Macro “{macroName}” — {btn.Label}" : null,
                    locked ? null : BuildOnboardMenu(vm, btn.Index));
            }
            else
            {
                if (btn.Index < ProfilesConfig.FirstButton)
                {
                    // 1-3 stay physical clicks — remapping Left Click per-app is a footgun.
                    DrawLabel(a, btn.Label, "Hardware click — edit in the Onboard profile", null);
                }
                else if (AppInstance.Profiles.IsHardware(btn.Index))
                {
                    // Kept on the mouse: the device sends the key itself, so it is
                    // the same in every app — including games that ignore injected
                    // input — and no profile can change it.
                    DrawLabel(a, btn.Label + "  ⌨",
                        $"{btn.Label} — kept on the mouse, so games that ignore injected keys still see it. " +
                        "The same in every app.", BuildHardwareMenu(vm, btn.Index));
                }
                else
                {
                    var binding = _profile.Buttons.TryGetValue(btn.Index, out var b) ? b : null;
                    string text = binding == null || binding.Kind == BindingKind.Default ? "Default" : binding.Describe();
                    DrawLabel(a, text, null, BuildBindingMenu(_profile, btn.Index));
                }
            }
        }
    }

    /// <summary>No device / no art: clear the canvas and show a hint instead.</summary>
    public void ShowEmpty(string message)
    {
        Root.Children.Clear();
        _labels.Clear();
        UntestedBanner.Visibility = Visibility.Collapsed;
        NoArt.Text = message;
        NoArt.Visibility = Visibility.Visible;
    }

    private void DrawLabel(Anchor a, string current, string? lockedTooltip, ContextMenu? menu)
    {
        var text = new TextBlock
        {
            Text = current, Foreground = (Brush)FindResource("Text"),
            FontSize = 21, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        };
        var border = new Border
        {
            Background = (Brush)FindResource("Panel"), BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(11, 5, 11, 5), Child = text,
            ToolTip = lockedTooltip ?? current,
        };
        if (menu != null)
        {
            border.Cursor = Cursors.Hand;
            border.MouseLeftButtonUp += (_, _) =>
            {
                if (_busy) return;
                menu.PlacementTarget = border; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
            };
            border.MouseEnter += (_, _) => { if (!_busy) border.BorderBrush = (Brush)FindResource("Accent"); };
            border.MouseLeave += (_, _) => border.BorderBrush = (Brush)FindResource("Border");
        }
        else
        {
            border.Opacity = 0.55;
        }

        // Center the label box (~40px tall) on the leader endpoint.
        Canvas.SetTop(border, a.Y - 20);
        if (a.RightSide) Canvas.SetLeft(border, a.X + 6);
        else Canvas.SetRight(border, Root.Width - a.X + 6);
        _labels.Add(border);
        Root.Children.Add(border);
    }

    // ===== Onboard (hardware) profile menu =====================================

    private ContextMenu BuildOnboardMenu(DeviceViewModel vm, int index)
    {
        var menu = new ContextMenu();
        foreach (var act in OnboardProfiles.Catalog)
        {
            var item = new MenuItem { Header = act.Label };
            var bytes = act.Bytes;
            item.Click += (_, _) => ApplyFlash(vm, index, () =>
            {
                var (ok, newLabel) = vm.ButtonDev!.RemapButton(index, bytes);
                if (ok) MacroNames.Set(vm.VendorId, vm.ProductId, index, null); // no longer a macro
                return (ok, ok ? newLabel : null, ok ? null : "Couldn't write to the device. If G HUB is running, quit it and try again.");
            });
            menu.Items.Add(item);
        }

        // Every keyboard key the chip can send, as a plain MODIFIER_AND_KEY action.
        menu.Items.Add(BuildKeyboardMenu(
            vk => KeyCatalog.OnboardBytes(vk) != null,
            (name, vk) => ApplyFlash(vm, index, () =>
            {
                var (ok, newLabel) = vm.ButtonDev!.RemapButton(index, KeyCatalog.OnboardBytes(vk)!);
                if (ok) MacroNames.Set(vm.VendorId, vm.ProductId, index, null); // no longer a macro
                return (ok, ok ? newLabel : null, ok ? null : "Couldn't write to the device. If G HUB is running, quit it and try again.");
            })));

        menu.Items.Add(new Separator());
        var macros = new MenuItem { Header = "Macros" };

        void AssignOnboard(string name, IReadOnlyList<Macro.Step> steps, Macro.RepeatMode repeat)
        {
            // The onboard chip can never cancel a Toggle loop (proven dead end), and
            // a hold toggle needs a host to let go again — both run once on hardware.
            if (repeat is Macro.RepeatMode.Toggle or Macro.RepeatMode.HoldToggle) repeat = Macro.RepeatMode.Once;
            if (vm.MacroDev == null)
            {
                ShowToast($"{vm.Name} can't store macros on the device yet.");
                return;
            }
            ApplyFlash(vm, index, () =>
            {
                var (ok, _, _, error) = vm.MacroDev.AssignMacro(index, steps, repeat);
                if (ok) MacroNames.Set(vm.VendorId, vm.ProductId, index, name);
                return (ok, ok ? name : null, ok ? null : $"Macro not saved: {error}.");
            });
        }

        foreach (var saved in MacroStore.Load().OrderBy(m => m.Name))
        {
            var mi = new MenuItem { Header = saved.Name };
            var m = saved;
            mi.Click += (_, _) => AssignOnboard(m.Name, m.Steps, (Macro.RepeatMode)m.Repeat);
            macros.Items.Add(mi);
        }
        if (macros.Items.Count > 0) macros.Items.Add(new Separator());

        var macroItem = new MenuItem { Header = "Create macro…" };
        macroItem.Click += (_, _) =>
        {
            var editor = new MacroEditor { Owner = Window.GetWindow(this) };
            bool saved = editor.ShowDialog() == true && editor.Result.Count > 0;
            Render(); // the library may have changed (saves/deletes) even on cancel
            if (saved) AssignOnboard(editor.MacroName, editor.Result, editor.Repeat);
        };
        macros.Items.Add(macroItem);
        menu.Items.Add(macros);
        return menu;
    }

    private void ApplyFlash(DeviceViewModel vm, int index, Func<(bool ok, string? label, string? error)> work)
    {
        _busy = true;
        foreach (var b in _labels) b.Opacity = 0.5;
        var dispatcher = Dispatcher;
        System.Threading.Tasks.Task.Run(() =>
        {
            (bool ok, string? label, string? error) result;
            try { result = work(); }
            catch { result = (false, null, "Device I/O failed — was it unplugged?"); }

            // The whole point of assigning is that the button then works: onboard
            // mappings only run in onboard mode, so switch it on if it was off.
            bool enabledMode = false;
            if (result.ok)
            {
                try
                {
                    if (!vm.ButtonDev!.IsOnboardMode()) enabledMode = vm.ButtonDev!.EnableOnboardMode();
                }
                catch { }
            }

            dispatcher.Invoke(() =>
            {
                _busy = false;
                Render();
                RefreshModeBanner();
                if (result.error != null) ShowToast(result.error);
                else if (enabledMode) ShowToast("Onboard mode was off — enabled so the assignment works.");
            });
        });
    }

    // ===== Buttons kept on the mouse ===========================================

    /// <summary>
    /// Write a key into the button's onboard action and take it out of the per-app
    /// engine. Some games (GTA IV and other DirectInput titles) ignore injected
    /// keystrokes entirely — a key the mouse sends itself is the only kind they see.
    /// </summary>
    private void KeepOnMouse(DeviceViewModel vm, int index, byte[] action) =>
        ApplyFlash(vm, index, () =>
        {
            var (ok, error) = ProfileArmer.KeepOnMouse(vm.ButtonDev!, AppInstance.Profiles, index, action);
            if (ok) MacroNames.Set(vm.VendorId, vm.ProductId, index, null);
            AppInstance.Automation.SetProfiles(AppInstance.Profiles);
            return (ok, null, error);
        });

    /// <summary>Menu for a button that is kept on the mouse.</summary>
    private ContextMenu BuildHardwareMenu(DeviceViewModel vm, int index)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem
        {
            Header = "On the mouse — the same key in every app", IsEnabled = false,
        });
        menu.Items.Add(new Separator());

        var keys = BuildKeyboardMenu(vk => KeyCatalog.OnboardBytes(vk) != null,
            (_, vk) => KeepOnMouse(vm, index, KeyCatalog.OnboardBytes(vk)!));
        keys.Header = "Change the key";
        menu.Items.Add(keys);

        var clicks = new MenuItem { Header = "Change to a click" };
        foreach (var act in OnboardProfiles.Catalog.Take(5))       // the five mouse buttons
        {
            var mi = new MenuItem { Header = act.Label };
            var bytes = act.Bytes;
            mi.Click += (_, _) => KeepOnMouse(vm, index, bytes);
            clicks.Items.Add(mi);
        }
        menu.Items.Add(clicks);
        menu.Items.Add(new Separator());

        var back = new MenuItem { Header = "Give back to per-app profiles" };
        back.Click += (_, _) => ApplyFlash(vm, index, () =>
        {
            var (ok, error) = ProfileArmer.GiveBackToProfiles(vm.ButtonDev!, AppInstance.Profiles, index);
            AppInstance.Automation.SetProfiles(AppInstance.Profiles);
            return (ok, null, error);
        });
        menu.Items.Add(back);
        return menu;
    }

    // ===== App profile (software) menu =========================================

    private ContextMenu BuildBindingMenu(AppProfile profile, int index)
    {
        var menu = new ContextMenu();

        void Set(ButtonBinding? binding)
        {
            if (binding == null) profile.Buttons.Remove(index);
            else profile.Buttons[index] = binding;
            AppInstance.SaveProfiles();
            Render();
        }

        var def = new MenuItem { Header = "Default (fallback profile)" };
        def.Click += (_, _) => Set(null);
        menu.Items.Add(def);
        menu.Items.Add(new Separator());

        foreach (var name in InputInjector.MouseButtons)
        {
            var mi = new MenuItem { Header = name };
            string n = name;
            mi.Click += (_, _) => Set(new ButtonBinding { Kind = BindingKind.MouseClick, Text = n });
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());

        // Any single keyboard key, grouped (letters, numpad, modifiers, …).
        menu.Items.Add(BuildKeyboardMenu(_ => true, (name, vk) => Set(new ButtonBinding
        {
            Kind = BindingKind.KeyChord, Text = name, VirtualKey = vk,
        })));

        // The same keys, but written into the mouse. A per-app key is injected by
        // Rodent, and DirectInput games (GTA IV) ignore injected input no matter
        // how it is sent; a key the mouse sends is a real HID report, so it always
        // lands — at the cost of being the same key in every app.
        var vmNow = _vm;
        if (vmNow?.ButtonDev != null)
        {
            var onMouse = BuildKeyboardMenu(vk => KeyCatalog.OnboardBytes(vk) != null,
                (_, vk) => KeepOnMouse(vmNow, index, KeyCatalog.OnboardBytes(vk)!));
            onMouse.Header = "Keep on the mouse (works in every game)";
            menu.Items.Add(onMouse);
        }
        menu.Items.Add(new Separator());

        // Key chords, typed text and toggle-repeat all live in the macro editor now
        // ("Create macro…") — the loose prompt items duplicated it.
        var launch = new MenuItem { Header = "Launch app…" };
        launch.Click += (_, _) =>
        {
            var ofd = new Microsoft.Win32.OpenFileDialog { Title = "Program to launch", Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*" };
            if (ofd.ShowDialog(Window.GetWindow(this)) != true) return;
            Set(new ButtonBinding { Kind = BindingKind.LaunchApp, Text = ofd.FileName });
        };
        menu.Items.Add(launch);
        menu.Items.Add(new Separator());

        // Same DPI actions the onboard catalog offers, executed host-side (live
        // 0x2201 writes stepping through the profile's DPI slots).
        var dpi = new MenuItem { Header = "DPI" };
        foreach (var name in new[] { "DPI +", "DPI -", "Cycle DPI", "DPI Shift (sniper)" })
        {
            var mi = new MenuItem { Header = name };
            string n = name;
            mi.Click += (_, _) => Set(new ButtonBinding { Kind = BindingKind.Dpi, Text = n });
            dpi.Items.Add(mi);
        }
        menu.Items.Add(dpi);

        // G HUB's command catalog: named key chords, grouped by category.
        var commands = new MenuItem { Header = "Commands" };
        MenuItem? catMenu = null;
        string? lastCat = null;
        foreach (var (cat, name, chord) in CommandCatalog)
        {
            if (cat != lastCat)
            {
                catMenu = new MenuItem { Header = cat };
                commands.Items.Add(catMenu);
                lastCat = cat;
            }
            var parsed = InputInjector.ParseChord(chord);
            if (parsed == null) continue;
            var mi = new MenuItem { Header = $"{name}  ({chord})" };
            string n = name;
            var (vk, mods) = parsed.Value;
            mi.Click += (_, _) => Set(new ButtonBinding
            {
                Kind = BindingKind.KeyChord, Text = n, VirtualKey = vk, Modifiers = mods,
            });
            catMenu!.Items.Add(mi);
        }
        menu.Items.Add(commands);

        // Grouped like a submenu instead of seven loose items.
        var system = new MenuItem { Header = "System" };
        foreach (var sys in SystemActions.All)
        {
            var mi = new MenuItem { Header = sys };
            string n = sys;
            mi.Click += (_, _) => Set(new ButtonBinding { Kind = BindingKind.System, Text = n });
            system.Items.Add(mi);
        }
        menu.Items.Add(system);
        menu.Items.Add(new Separator());

        // Same editor as the onboard profile; the macro is stored in the profile
        // and played by Rodent, so Toggle / While-Held work (software owns the loop).
        var macros = new MenuItem { Header = "Macros" };
        foreach (var saved in MacroStore.Load().OrderBy(m => m.Name))
        {
            var mi = new MenuItem { Header = saved.Name };
            var m = saved;
            mi.Click += (_, _) => Set(new ButtonBinding
            {
                Kind = BindingKind.Macro, Text = m.Name,
                MacroSteps = new List<Macro.Step>(m.Steps), MacroRepeat = m.Repeat,
            });
            macros.Items.Add(mi);
        }
        if (macros.Items.Count > 0) macros.Items.Add(new Separator());

        var macroItem = new MenuItem { Header = "Create macro…" };
        macroItem.Click += (_, _) =>
        {
            var editor = new MacroEditor(software: true) { Owner = Window.GetWindow(this) };
            bool saved = editor.ShowDialog() == true && editor.Result.Count > 0;
            Render(); // the library may have changed (saves/deletes) even on cancel
            if (saved) Set(new ButtonBinding
            {
                Kind = BindingKind.Macro, Text = editor.MacroName,
                MacroSteps = editor.Result.ToList(), MacroRepeat = (int)editor.Repeat,
            });
        };
        macros.Items.Add(macroItem);
        menu.Items.Add(macros);
        return menu;
    }

    /// <summary>
    /// The "Keyboard" submenu: every key in <see cref="KeyCatalog"/>, one submenu
    /// per group. <paramref name="supported"/> filters keys the target can't take
    /// (the onboard chip has no usage code for a few), and <paramref name="assign"/>
    /// receives the display name and virtual key of the chosen one.
    /// </summary>
    private static MenuItem BuildKeyboardMenu(Func<ushort, bool> supported, Action<string, ushort> assign)
    {
        var root = new MenuItem { Header = "Keyboard" };
        foreach (var g in KeyCatalog.Groups)
        {
            var group = new MenuItem { Header = g.Name };
            foreach (var k in g.Keys)
            {
                if (!supported(k.Vk)) continue;
                var mi = new MenuItem { Header = k.Name };
                var key = k;
                mi.Click += (_, _) => assign(key.Name, key.Vk);
                group.Items.Add(mi);
            }
            if (group.Items.Count > 0) root.Items.Add(group);
        }
        return root;
    }

    // G HUB's "Commands" list: (category, display name, chord). Executed as
    // plain key chords; the name is what shows on the button label.
    private static readonly (string Cat, string Name, string Chord)[] CommandCatalog =
    {
        ("Windows", "Open Task View", "win+tab"),
        ("Windows", "Open File Explorer", "win+e"),
        ("Windows", "Open Narrator", "win+ctrl+enter"),
        ("Windows", "Open Connect Quick Action", "win+k"),
        ("Windows", "Run Dialog", "win+r"),
        ("Windows", "Open Magnifier", "win+plus"),
        ("Windows", "Lock PC", "win+l"),
        ("Windows", "Hide/Show Desktop", "win+d"),
        ("Windows", "Minimize All Windows", "win+m"),
        ("Windows", "Set Focus in Notification Area", "win+b"),
        ("Windows", "Open Windows Settings", "win+i"),
        ("Windows", "Open Action Center", "win+a"),
        ("Windows", "Cycle Task Bar Apps", "win+t"),
        ("Windows", "Open Windows Game Bar", "win+g"),
        ("Windows", "Open Search", "win+s"),
        ("Windows", "Open Ease of Access Center", "win+u"),
        ("Windows", "Open Quick Links", "win+x"),
        ("Windows", "Open Emoji Panel", "win+."),
        ("Windows", "Open Copilot", "win+shift+f23"),
        ("Productivity", "Switch Between Apps", "alt+tab"),
        ("Productivity", "Cycle Through Apps", "alt+esc"),
        ("Productivity", "Exit Active App", "alt+f4"),
        ("Productivity", "Open Task Manager", "ctrl+shift+esc"),
        ("Productivity", "Open Start", "ctrl+esc"),
        ("Editing", "Copy", "ctrl+c"),
        ("Editing", "Paste", "ctrl+v"),
        ("Editing", "Cut", "ctrl+x"),
        ("Editing", "Undo", "ctrl+z"),
        ("Editing", "Redo", "ctrl+y"),
        ("Editing", "Select All", "ctrl+a"),
        ("Editing", "Save", "ctrl+s"),
        ("Editing", "New", "ctrl+n"),
        ("Editing", "Open", "ctrl+o"),
        ("Editing", "New Tab", "ctrl+t"),
        ("Editing", "Close Tab", "ctrl+w"),
        ("Editing", "Zoom In", "ctrl+plus"),
        ("Editing", "Zoom Out", "ctrl+minus"),
        ("Editing", "Zoom Reset", "ctrl+0"),
        ("Navigation", "Go Back", "alt+left"),
        ("Navigation", "Go Forward", "alt+right"),
        ("Navigation", "Refresh", "f5"),
        ("Navigation", "Hard Refresh", "ctrl+f5"),
        ("Navigation", "Next Tab", "ctrl+tab"),
        ("Navigation", "Previous Tab", "ctrl+shift+tab"),
        ("Navigation", "Reopen Closed Tab", "ctrl+shift+t"),
        ("Navigation", "Address Bar", "ctrl+l"),
        ("Navigation", "Find on Page", "ctrl+f"),
        ("Navigation", "Find Next", "f3"),
        ("Navigation", "Find Previous", "shift+f3"),
        ("Navigation", "Scroll to Top", "ctrl+home"),
        ("Navigation", "Scroll to Bottom", "ctrl+end"),
        ("Navigation", "Page Up", "pageup"),
        ("Navigation", "Page Down", "pagedown"),
        ("Navigation", "Up One Folder", "alt+up"),
        ("Navigation", "Full Screen", "f11"),
        ("Navigation", "Developer Tools", "f12"),
        ("Navigation", "Bookmark Page", "ctrl+d"),
        ("Navigation", "History", "ctrl+h"),
        ("Navigation", "Downloads", "ctrl+j"),
        ("Navigation", "Private Window", "ctrl+shift+n"),
        ("Navigation", "Print", "ctrl+p"),
        ("Navigation", "Rename (Explorer)", "f2"),
    };

    // ===== Copy a software profile into the mouse flash ========================

    /// <summary>
    /// Translate a software binding into an onboard action: either a 4-byte button
    /// action, or macro steps for things that need one. (null, null) = software-only
    /// (launch app, toggle-repeat text, lock PC) — those can't live on the mouse.
    /// </summary>
    private static (byte[]? bytes, IReadOnlyList<Macro.Step>? steps, Macro.RepeatMode repeat)
        ToOnboard(ButtonBinding b)
    {
        switch (b.Kind)
        {
            // The chip has no multi-click action — express it as a short macro.
            case BindingKind.MouseClick when b.Text is "Double Click" or "Triple Click":
                var clicks = new List<Macro.Step>();
                for (int i = 0; i < (b.Text == "Double Click" ? 2 : 3); i++)
                {
                    clicks.Add(new Macro.Step(Macro.Kind.MouseDown, Key: 0x01));
                    clicks.Add(new Macro.Step(Macro.Kind.Delay, DelayMs: 20));
                    clicks.Add(new Macro.Step(Macro.Kind.MouseUp, Key: 0x01));
                    clicks.Add(new Macro.Step(Macro.Kind.Delay, DelayMs: 20));
                }
                return (null, clicks, Macro.RepeatMode.Once);

            case BindingKind.MouseClick:
                int mask = b.Text switch
                {
                    "Left Click" => 0x0001, "Right Click" => 0x0002, "Middle Click" => 0x0004,
                    "Back" => 0x0008, "Forward" => 0x0010, _ => 0,
                };
                return mask == 0 ? default : (new byte[] { 0x80, 0x01, (byte)(mask >> 8), (byte)mask }, null, default);

            case BindingKind.KeyChord:
                byte hid = Macro.VkToHid(b.VirtualKey);
                byte mods = 0;
                foreach (var vk in b.Modifiers) mods |= Macro.VkToModifier(vk);
                // A bare modifier key (Left Shift, …) is sent as the modifier alone.
                if (hid == 0) mods |= Macro.VkToModifier(b.VirtualKey);
                if (hid == 0 && mods == 0) return default;
                return (new byte[] { 0x80, 0x02, mods, hid }, null, default);

            case BindingKind.TypeText:
                var typed = Macro.TypeText(b.Text);
                return typed.Count == 0 ? default : (null, typed, Macro.RepeatMode.Once);

            case BindingKind.Macro when b.MacroSteps is { Count: > 0 }:
                // The chip can't cancel a Toggle loop, nor let go of a hold — run once.
                var rep = (Macro.RepeatMode)b.MacroRepeat is Macro.RepeatMode.Toggle or Macro.RepeatMode.HoldToggle
                    ? Macro.RepeatMode.Once : (Macro.RepeatMode)b.MacroRepeat;
                return (null, b.MacroSteps, rep);

            case BindingKind.Dpi:
                byte fn = b.Text switch
                {
                    "DPI +" => 0x03, "DPI -" => 0x04, "Cycle DPI" => 0x05, _ => 0x07, // Shift (sniper)
                };
                return (new byte[] { 0x90, fn, 0xFF, 0x00 }, null, default);

            case BindingKind.System:
                int cons = b.Text switch
                {
                    SystemActions.VolumeUp => 0x00E9, SystemActions.VolumeDown => 0x00EA,
                    SystemActions.Mute => 0x00E2, SystemActions.PlayPause => 0x00CD,
                    SystemActions.NextTrack => 0x00B5, SystemActions.PrevTrack => 0x00B6,
                    _ => 0, // Lock PC has no consumer key
                };
                return cons == 0 ? default : (new byte[] { 0x80, 0x03, (byte)(cons >> 8), (byte)cons }, null, default);

            default:
                return default; // LaunchApp / RepeatText / unknown — host-side only
        }
    }

    private void CopyOnboard_Click(object sender, RoutedEventArgs e)
    {
        var vm = _vm;
        var profile = _profile;
        if (vm == null || profile == null || _busy) return;
        if (AppInstance.Profiles.Enabled)
        {
            ShowToast("Turn off per-app profiles first (Per-App tab) — the side buttons currently hold the signal keys.");
            return;
        }
        if (!Dialogs.Confirm(Window.GetWindow(this),
            $"Write “{profile.Name}” side-button bindings (4–8) into the mouse flash?\n\n" +
            "They become the Onboard profile and work without Rodent, on any PC. " +
            "Software-only actions (launch app, repeat text, Lock PC) are skipped, and a " +
            "Toggle macro runs once per press onboard.")) return;

        _busy = true;
        foreach (var l in _labels) l.Opacity = 0.5;
        System.Threading.Tasks.Task.Run(() =>
        {
            int written = 0;
            var skipped = new List<string>();
            for (int b = ProfilesConfig.FirstButton; b <= ProfilesConfig.LastButton; b++)
            {
                var binding = profile.Get(b);
                if (binding == null) continue; // Default: leave the onboard action alone
                var (bytes, steps, repeat) = ToOnboard(binding);
                bool ok;
                if (bytes != null)
                {
                    ok = vm.ButtonDev!.RemapButton(b, bytes).ok;
                    if (ok) MacroNames.Set(vm.VendorId, vm.ProductId, b, null);
                }
                else if (steps != null && vm.MacroDev != null)
                {
                    ok = vm.MacroDev.AssignMacro(b, steps, repeat).ok;
                    if (ok) MacroNames.Set(vm.VendorId, vm.ProductId, b, binding.Describe());
                }
                else { skipped.Add($"{b}: {binding.Describe()}"); continue; }
                if (ok) written++; else skipped.Add($"{b}: write failed");
            }
            bool enabledMode = false;
            try { if (!vm.ButtonDev!.IsOnboardMode()) enabledMode = vm.ButtonDev!.EnableOnboardMode(); } catch { }

            Dispatcher.Invoke(() =>
            {
                _busy = false;
                Render();
                RefreshModeBanner();
                string msg = $"Copied {written} binding(s) to the mouse.";
                if (skipped.Count > 0) msg += $" Skipped: {string.Join(", ", skipped)}.";
                if (enabledMode) msg += " Onboard mode was off — enabled.";
                ShowToast(msg);
            });
        });
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }
}
