using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rodent.Core.Hidpp;

namespace Rodent.App;

/// <summary>
/// Small dark-themed modal dialogs matching the app chrome (native MessageBox and
/// default-styled windows would pop up with light title bars).
/// </summary>
public static class Dialogs
{
    /// <summary>Modal text prompt. Returns null when cancelled.</summary>
    public static string? Prompt(Window? owner, string title, string hint = "", string initial = "")
    {
        var box = new TextBox { Margin = new Thickness(16, 12, 16, 4), FontSize = 14, MinWidth = 320, Text = initial };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 84, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 84 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 10, 16, 14) };
        row.Children.Add(ok); row.Children.Add(cancel);

        var panel = new StackPanel();
        if (!string.IsNullOrEmpty(hint))
            panel.Children.Add(new TextBlock
            {
                Text = hint, Foreground = Res<Brush>("Muted"), FontSize = 12,
                Margin = new Thickness(16, 12, 16, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 340,
            });
        panel.Children.Add(box);
        panel.Children.Add(row);

        var dlg = Frame(owner, title, panel);
        ok.Click += (_, _) => dlg.DialogResult = true;
        dlg.Loaded += (_, _) => { box.Focus(); box.CaretIndex = box.Text.Length; };
        return dlg.ShowDialog() == true ? box.Text : null;
    }

    /// <summary>
    /// Pick a program: dropdown of running windowed processes (refreshed when
    /// opened) + Browse… file picker for any exe. Returns the exe name lowercase
    /// without extension, or null when cancelled.
    /// </summary>
    public static string? PickApp(Window? owner)
    {
        var combo = new ComboBox { IsEditable = true, Margin = new Thickness(16, 12, 16, 4), MinWidth = 280 };
        void Fill()
        {
            string text = combo.Text;
            combo.ItemsSource = System.Diagnostics.Process.GetProcesses()
                .Where(p => { try { return p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0; } catch { return false; } })
                .Select(p => p.ProcessName.ToLowerInvariant())
                .Distinct().OrderBy(x => x).ToList();
            combo.Text = text;
        }
        Fill();
        combo.DropDownOpened += (_, _) => Fill();

        var browse = new Button { Content = "Browse…", Width = 96, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(16, 6, 16, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 84, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 84 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 12, 16, 14) };
        row.Children.Add(ok); row.Children.Add(cancel);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Pick a running program, type an exe name, or browse for one.",
            Foreground = Res<Brush>("Muted"), FontSize = 12,
            Margin = new Thickness(16, 12, 16, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 320,
        });
        panel.Children.Add(combo);
        panel.Children.Add(browse);
        panel.Children.Add(row);

        var dlg = Frame(owner, "Select program", panel);
        browse.Click += (_, _) =>
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select a program",
                Filter = "Programs (*.exe)|*.exe",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            };
            if (ofd.ShowDialog(dlg) == true)
                combo.Text = System.IO.Path.GetFileNameWithoutExtension(ofd.FileName).ToLowerInvariant();
        };
        ok.Click += (_, _) => dlg.DialogResult = true;
        dlg.Loaded += (_, _) => combo.Focus();
        if (dlg.ShowDialog() != true) return null;

        string app = combo.Text.Trim().ToLowerInvariant();
        if (app.EndsWith(".exe")) app = app[..^4];
        return app.Length > 0 ? app : null;
    }

    /// <summary>
    /// Capture a key combination by actually pressing it (like G HUB), instead of
    /// typing its name. Every key is marked handled so window hotkeys (Alt+F4,
    /// Tab navigation…) record instead of firing. Returns (HID modifier mask,
    /// HID key, display label) or null when cancelled.
    /// </summary>
    public static (byte mods, byte key, string label)? CaptureCombo(Window? owner)
    {
        byte mods = 0, key = 0;
        string label = "";

        var display = new TextBlock
        {
            Text = "Press a key combination…", Foreground = Res<Brush>("Text"),
            FontSize = 16, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(16, 16, 16, 4), MinWidth = 320,
        };
        var hint = new TextBlock
        {
            Text = "e.g. hold Ctrl+Shift and tap T. The last combination pressed wins.",
            Foreground = Res<Brush>("Muted"), FontSize = 12,
            Margin = new Thickness(16, 0, 16, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 340,
        };
        var ok = new Button { Content = "OK", Width = 84, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cancel = new Button { Content = "Cancel", Width = 84 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 14, 16, 14) };
        row.Children.Add(ok); row.Children.Add(cancel);

        var panel = new StackPanel();
        panel.Children.Add(display);
        panel.Children.Add(hint);
        panel.Children.Add(row);

        var dlg = Frame(owner, "Add key combo", panel);
        // Buttons are mouse-only on purpose: Enter/Esc/Space must be capturable.
        ok.Click += (_, _) => dlg.DialogResult = true;
        cancel.Click += (_, _) => dlg.DialogResult = false;

        static byte CurrentMods()
        {
            var m = Keyboard.Modifiers;
            byte b = 0;
            if ((m & ModifierKeys.Control) != 0) b |= Macro.ModCtrl;
            if ((m & ModifierKeys.Shift) != 0) b |= Macro.ModShift;
            if ((m & ModifierKeys.Alt) != 0) b |= Macro.ModAlt;
            if ((m & ModifierKeys.Windows) != 0) b |= Macro.ModGui;
            return b;
        }
        static string ModsLabel(byte b) =>
            (((b & Macro.ModCtrl) != 0) ? "ctrl+" : "") + (((b & Macro.ModShift) != 0) ? "shift+" : "") +
            (((b & Macro.ModAlt) != 0) ? "alt+" : "") + (((b & Macro.ModGui) != 0) ? "win+" : "");

        dlg.PreviewKeyDown += (_, e) =>
        {
            var k = e.Key == Key.System ? e.SystemKey : e.Key;
            e.Handled = true;
            int vk = KeyInterop.VirtualKeyFromKey(k);
            if (Macro.VkToModifier(vk) != 0)
            {
                display.Text = ModsLabel(CurrentMods()) + "…";      // show held modifiers live
                return;
            }
            byte hid = Macro.VkToHid(vk);
            if (hid == 0) { display.Text = "That key can't be stored — try another"; return; }
            mods = CurrentMods();
            key = hid;
            label = ModsLabel(mods) + Macro.KeyName(hid).ToLowerInvariant();
            display.Text = label;
            ok.IsEnabled = true;
        };

        return dlg.ShowDialog() == true && key != 0 ? (mods, key, label) : null;
    }

    /// <summary>Modal OK/Cancel question. True only when confirmed.</summary>
    public static bool Confirm(Window? owner, string text, string title = "Rodent")
    {
        var ok = new Button { Content = "OK", IsDefault = true, Width = 84, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 84 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 12, 16, 14) };
        row.Children.Add(ok); row.Children.Add(cancel);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = text, Foreground = Res<Brush>("Text"), FontSize = 13,
            Margin = new Thickness(16, 14, 16, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 380,
        });
        panel.Children.Add(row);

        var dlg = Frame(owner, title, panel);
        ok.Click += (_, _) => dlg.DialogResult = true;
        return dlg.ShowDialog() == true;
    }

    /// <summary>Modal message with a single OK button.</summary>
    public static void Info(Window? owner, string text, string title = "Rodent")
    {
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, Width = 84 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 12, 16, 14) };
        row.Children.Add(ok);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = text, Foreground = Res<Brush>("Text"), FontSize = 13,
            Margin = new Thickness(16, 14, 16, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 380,
        });
        panel.Children.Add(row);

        var dlg = Frame(owner, title, panel);
        ok.Click += (_, _) => dlg.DialogResult = true;
        dlg.ShowDialog();
    }

    /// <summary>Chromeless dark window: slim draggable title row + content.</summary>
    private static Window Frame(Window? owner, string title, UIElement content)
    {
        var dlg = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner ?? Application.Current.MainWindow,
            Background = Res<Brush>("Bg"),
            ShowInTaskbar = false,
        };

        var titleText = new TextBlock
        {
            Text = title, Foreground = Res<Brush>("Text"), FontSize = 13, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 40, 0),
        };
        var titleBar = new Border { Background = Res<Brush>("Panel"), Height = 36, Child = titleText };
        titleBar.MouseLeftButtonDown += (_, _) => dlg.DragMove();

        var dock = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        dock.Children.Add(titleBar);
        dock.Children.Add(content);

        dlg.Content = new Border
        {
            BorderBrush = Res<Brush>("Border"), BorderThickness = new Thickness(1), Child = dock,
        };
        return dlg;
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);
}
