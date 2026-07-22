using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rodent.Core.Hidpp;

namespace Rodent.App;

public partial class MacroEditor : Window
{
    // Spaced items get the standard delay inserted between their steps on save
    // (recorded/typed sequences carry no timing of their own — played back with
    // zero gaps, games and some apps drop keys).
    private sealed record Item(string Desc, List<Macro.Step> Steps, bool Spaced);

    private readonly List<Item> _items = new();

    public IReadOnlyList<Macro.Step> Result { get; private set; } = Array.Empty<Macro.Step>();
    public Macro.RepeatMode Repeat { get; private set; } = Macro.RepeatMode.Once;
    public string MacroName => NameBox.Text;

    public MacroEditor() : this(software: false) { }

    /// <summary>
    /// software=true: the macro is a per-app binding played by Rodent, not flashed
    /// to the mouse — Toggle is safe there (Rodent owns the loop and always stops),
    /// so the card is enabled.
    /// </summary>
    public MacroEditor(bool software)
    {
        InitializeComponent();
        if (software)
        {
            TypeHint.Text = "Macro type (runs in Rodent while this profile's app is in front)";
            TypeToggle.Content = "Toggle";
            TypeToggle.IsEnabled = true;
            TypeToggle.Opacity = 1;
            SaveBtn.Content = "Save to profile";
            ToggleNote.Text = "This macro runs in Rodent (software), so all types work. Toggle: press the button " +
                              "to start repeating, press again to stop — Esc also stops it, and it auto-stops after 30 s. " +
                              "Repeat While Held repeats while the side button is held.";
        }
        _software = software;
        RefreshSaved();
        Refresh();
    }

    private readonly bool _software;

    // ---- macro library ----
    private void RefreshSaved()
    {
        SavedCombo.ItemsSource = Rodent.Core.Automation.MacroStore.Load().Select(m => m.Name).OrderBy(n => n).ToList();
        if (SavedCombo.Items.Count > 0) SavedCombo.SelectedIndex = 0;
    }

    private void LoadSaved_Click(object sender, RoutedEventArgs e)
    {
        var m = Rodent.Core.Automation.MacroStore.Load()
            .FirstOrDefault(x => x.Name == SavedCombo.SelectedItem as string);
        if (m == null) return;
        NameBox.Text = m.Name;
        _items.Clear();
        // Delays are already baked into the stored steps — don't re-space on save.
        _items.Add(new Item($"Saved “{m.Name}” ({m.Steps.Count} steps)", new List<Macro.Step>(m.Steps), Spaced: false));
        var rep = (Macro.RepeatMode)m.Repeat;
        TypeHeld.IsChecked = rep == Macro.RepeatMode.WhileHeld;
        TypeToggle.IsChecked = rep == Macro.RepeatMode.Toggle && _software;
        TypeOnce.IsChecked = TypeHeld.IsChecked != true && TypeToggle.IsChecked != true;
        Refresh();
    }

    private void DeleteSaved_Click(object sender, RoutedEventArgs e)
    {
        if (SavedCombo.SelectedItem is not string name) return;
        Rodent.Core.Automation.MacroStore.Delete(name);

        // Buttons bound to the deleted macro go back to Default in every profile.
        var app = (App)Application.Current;
        bool changed = false;
        foreach (var p in app.Profiles.Profiles)
        {
            var bound = p.Buttons
                .Where(kv => kv.Value.Kind == Rodent.Core.Automation.BindingKind.Macro && kv.Value.Text == name)
                .Select(kv => kv.Key).ToList();
            foreach (var k in bound) { p.Buttons.Remove(k); changed = true; }
        }
        if (changed) app.SaveProfiles();
        RefreshSaved();
    }

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e) => DragMove();
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Refresh()
    {
        StepsList.Children.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var grid = new Grid { Margin = new Thickness(4, 3, 4, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = $"{i + 1}.  {item.Desc}", Foreground = (Brush)FindResource("Text"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var del = new Button { Content = "✕", Width = 26, Height = 24, Padding = new Thickness(0), Tag = item };
            del.Click += (_, _) => { _items.Remove(item); Refresh(); };
            Grid.SetColumn(del, 1);
            grid.Children.Add(del);
            StepsList.Children.Add(grid);
        }
        if (_items.Count == 0)
            StepsList.Children.Add(new TextBlock { Text = "No actions yet — add text, a key combo, or a delay.", Foreground = (Brush)FindResource("Muted"), FontSize = 12, Margin = new Thickness(6) });
    }

    // ---- live keystroke + mouse-click recording (G HUB's "record keystrokes") ----
    private Rodent.Core.Automation.LowLevelKeyboardHook? _recHook;
    private Rodent.Core.Automation.LowLevelMouseHook? _recMouse;
    private List<Macro.Step>? _recSteps;
    private byte _recMods;

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recHook != null) { StopRecording(); return; }

        var steps = new List<Macro.Step>();
        _recSteps = steps;
        _recMods = 0;
        _recHook = new Rodent.Core.Automation.LowLevelKeyboardHook();
        _recHook.OnKey += (vk, scan, extended, down) =>
        {
            // Record the physical key (scan code), not the virtual key: a virtual key
            // carries the character of the ACTIVE layout, so a Turkish ö would be
            // stored as the US character on that key (a comma) and replay wrong.
            byte mod = Macro.ScanToModifier(scan, extended);
            if (mod == 0) mod = Macro.VkToModifier(vk);
            if (mod != 0) { if (down) _recMods |= mod; else _recMods &= (byte)~mod; return; }
            byte hid = Macro.ScanToHid(scan, extended);
            if (hid == 0) hid = Macro.VkToHid(vk);
            if (hid == 0) return;
            lock (steps) steps.Add(new Macro.Step(down ? Macro.Kind.KeyDown : Macro.Kind.KeyUp, _recMods, hid));
        };
        _recHook.Start();

        // Editor bounds are checked with raw Win32 from the hook thread: a
        // Dispatcher.Invoke here would block the hook callback on the UI thread,
        // and a slow callback gets the whole hook silently removed by Windows
        // (LowLevelHooksTimeout) — which showed up as "clicks don't record".
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _recMouse = new Rodent.Core.Automation.LowLevelMouseHook();
        _recMouse.OnButton += (mask, down, x, y) =>
        {
            // Clicks on the editor itself (add/remove buttons…) are UI, not macro.
            if (GetWindowRect(hwnd, out RECT r) && x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom)
                return;
            lock (steps) steps.Add(new Macro.Step(down ? Macro.Kind.MouseDown : Macro.Kind.MouseUp, 0, (byte)mask));
        };
        _recMouse.Start();

        // Take focus off the controls so typed keys only feed the recording (a
        // focused button would otherwise be "clicked" by Space/Enter).
        System.Windows.Input.Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, null);
        RecordBtn.Content = "■ Stop recording (hover here)";
        // Stop on HOVER so the stopping click never lands in the recording.
        RecordBtn.MouseEnter += StopOnHover;
    }

    private void StopOnHover(object sender, System.Windows.Input.MouseEventArgs e) => StopRecording();

    private void StopRecording()
    {
        RecordBtn.MouseEnter -= StopOnHover;
        _recHook?.Stop();
        _recHook?.Dispose();
        _recHook = null;
        _recMouse?.Stop();
        _recMouse?.Dispose();
        _recMouse = null;
        RecordBtn.Content = "● Record keys + clicks";
        var steps = _recSteps ?? new List<Macro.Step>();
        _recSteps = null;
        if (steps.Count == 0) return;
        int keys = steps.Count(s => s.Kind == Macro.Kind.KeyDown);
        int clicks = steps.Count(s => s.Kind == Macro.Kind.MouseDown);
        string desc = "Recorded " + string.Join(", ",
            new[] { keys > 0 ? $"{keys} keystroke(s)" : null, clicks > 0 ? $"{clicks} click(s)" : null }
            .Where(s => s != null));
        _items.Add(new Item(desc, steps, Spaced: true));
        Refresh();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Alt+F4 while recording lands here — treat it as "stop", not "close".
        if (_recHook != null) { e.Cancel = true; StopRecording(); return; }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e) { _recHook?.Dispose(); _recMouse?.Dispose(); base.OnClosed(e); }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        string? t = Dialogs.Prompt(this, "Add text", "Text to type");
        if (string.IsNullOrEmpty(t)) return;
        var steps = new List<Macro.Step>();
        var dropped = new List<char>();
        foreach (char c in t)
        {
            // Resolve through the active keyboard layout so ö, ç, ğ… work.
            var (key, mods) = Macro.CharToKeyLayout(c);
            if (key == 0)
            {
                var (fallback, shift) = Macro.CharToKey(c);
                if (fallback == 0) { if (!dropped.Contains(c)) dropped.Add(c); continue; }
                (key, mods) = (fallback, shift ? Macro.ModShift : (byte)0);
            }
            steps.Add(new Macro.Step(Macro.Kind.KeyDown, mods, key));
            steps.Add(new Macro.Step(Macro.Kind.KeyUp, mods, key));
        }
        if (dropped.Count > 0)
            Dialogs.Info(this,
                $"Skipped characters no key on your current layout types directly: {string.Join(" ", dropped)}\n\n" +
                "A macro stores key positions, so the text replays through whatever keyboard layout is active.");
        if (steps.Count == 0) return;
        _items.Add(new Item($"Type: {t}", steps, Spaced: true));
        Refresh();
    }

    private void AddKey_Click(object sender, RoutedEventArgs e)
    {
        var got = Dialogs.CaptureCombo(this);   // press it, don't type its name
        if (got == null) return;
        var (mod, key, label) = got.Value;
        var steps = new List<Macro.Step>
        {
            new(Macro.Kind.KeyDown, mod, key),
            new(Macro.Kind.KeyUp, mod, key),
        };
        _items.Add(new Item($"Key: {label}", steps, Spaced: true));
        Refresh();
    }

    private void AddDelay_Click(object sender, RoutedEventArgs e)
    {
        string? s = Dialogs.Prompt(this, "Add delay", "Delay in milliseconds");
        if (!ushort.TryParse(s, out ushort ms)) return;
        _items.Add(new Item($"Delay {ms} ms", new List<Macro.Step> { new(Macro.Kind.Delay, DelayMs: ms) }, Spaced: false));
        Refresh();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_recHook != null) StopRecording();
        if (_items.Count == 0) { Dialogs.Info(this, "Add at least one action."); return; }
        var steps = new List<Macro.Step>();
        bool std = StdDelay.IsChecked == true;
        foreach (var item in _items)
        {
            if (steps.Count > 0 && std)
                steps.Add(new Macro.Step(Macro.Kind.Delay, DelayMs: Macro.StandardDelayMs));
            if (std && item.Spaced)
            {
                for (int k = 0; k < item.Steps.Count; k++)
                {
                    if (k > 0 && item.Steps[k].Kind != Macro.Kind.Delay && item.Steps[k - 1].Kind != Macro.Kind.Delay)
                        steps.Add(new Macro.Step(Macro.Kind.Delay, DelayMs: Macro.StandardDelayMs));
                    steps.Add(item.Steps[k]);
                }
            }
            else steps.AddRange(item.Steps);
        }
        Repeat = TypeHeld.IsChecked == true ? Macro.RepeatMode.WhileHeld
               : TypeToggle.IsChecked == true ? Macro.RepeatMode.Toggle
               : Macro.RepeatMode.Once;
        Result = steps;
        // Every save also lands in the library, so assigning elsewhere later
        // doesn't require rebuilding the macro.
        if (!string.IsNullOrWhiteSpace(NameBox.Text))
            Rodent.Core.Automation.MacroStore.Upsert(new Rodent.Core.Automation.SavedMacro
            {
                Name = NameBox.Text.Trim(), Steps = steps, Repeat = (int)Repeat,
            });
        DialogResult = true;
    }

}
