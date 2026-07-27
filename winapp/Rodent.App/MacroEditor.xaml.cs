using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rodent.Core.Hidpp;

namespace Rodent.App;

public partial class MacroEditor : Window
{
    // The macro is edited as the flat step list it really is: every key press and
    // release, click and delay is a chip you can select, move or delete. (It used
    // to be opaque groups — "Recorded 6 steps" — which you could only delete whole.)
    private readonly List<Macro.Step> _steps = new();
    private int _sel = -1;                       // selected chip (index into the cell list)

    public IReadOnlyList<Macro.Step> Result { get; private set; } = Array.Empty<Macro.Step>();
    public Macro.RepeatMode Repeat { get; private set; } = Macro.RepeatMode.Once;
    public string MacroName => NameBox.Text;

    public MacroEditor() : this(software: false) { }

    /// <summary>
    /// software=true: the macro is a per-app binding played by Rodent, not flashed
    /// to the mouse — Toggle and Hold are safe there (Rodent owns the loop and
    /// always lets go), so those cards are enabled.
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
            TypeHold.IsEnabled = true;
            TypeHold.Opacity = 1;
            SaveBtn.Content = "Save to profile";
            ToggleNote.Text = "This macro runs in Rodent (software), so all types work. Toggle: press to start " +
                              "repeating the sequence, press again to stop. Hold Until Pressed Again: the keys go " +
                              "DOWN and stay down (sprint) until you press the button again — Esc and switching " +
                              "app also let go. Repeat While Held repeats while the side button is held.";
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
        _steps.Clear();
        _steps.AddRange(m.Steps);            // shown step by step, editable
        _sel = -1;
        var rep = (Macro.RepeatMode)m.Repeat;
        TypeHeld.IsChecked = rep == Macro.RepeatMode.WhileHeld;
        TypeToggle.IsChecked = rep == Macro.RepeatMode.Toggle && _software;
        TypeHold.IsChecked = rep == Macro.RepeatMode.HoldToggle && _software;
        TypeOnce.IsChecked = TypeHeld.IsChecked != true && TypeToggle.IsChecked != true && TypeHold.IsChecked != true;
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

    // ---- the sequence, as chips ------------------------------------------------

    /// <summary>One chip: the steps it stands for, and its label.</summary>
    private sealed record Cell(List<int> Indices, string Label);

    /// <summary>
    /// Group the steps into chips. With "show key movement" off, a press and its
    /// release collapse into one chip ("Shift"); with it on, every half is its own
    /// chip ("Shift ↓", "Shift ↑") so a hold can be built by deleting the release.
    /// </summary>
    private List<Cell> BuildCells()
    {
        var cells = new List<Cell>();
        bool movement = ShowMovement.IsChecked == true;
        var taken = new bool[_steps.Count];
        for (int i = 0; i < _steps.Count; i++)
        {
            if (taken[i]) continue;
            var s = _steps[i];
            if (!movement && (s.Kind == Macro.Kind.KeyDown || s.Kind == Macro.Kind.MouseDown))
            {
                var upKind = s.Kind == Macro.Kind.KeyDown ? Macro.Kind.KeyUp : Macro.Kind.MouseUp;
                int j = FindRelease(i, upKind, s.Key);
                if (j > 0)
                {
                    taken[j] = true;
                    cells.Add(new Cell(new List<int> { i, j }, Name(s)));
                    continue;
                }
            }
            cells.Add(new Cell(new List<int> { i }, Label(s)));
        }
        return cells;
    }

    /// <summary>Index of this press's release, if only delays sit between them.</summary>
    private int FindRelease(int from, Macro.Kind up, byte key)
    {
        for (int j = from + 1; j < _steps.Count; j++)
        {
            if (_steps[j].Kind == Macro.Kind.Delay) continue;
            return _steps[j].Kind == up && _steps[j].Key == key ? j : -1;
        }
        return -1;
    }

    /// <summary>Key/button name of a step, modifiers folded in ("Ctrl+C").</summary>
    private static string Name(Macro.Step s)
    {
        if (s.Kind == Macro.Kind.MouseDown || s.Kind == Macro.Kind.MouseUp)
            return Macro.MouseButtonName(s.Key);
        string mods = ((s.Modifier & Macro.ModCtrl) != 0 ? "Ctrl+" : "") +
                      ((s.Modifier & Macro.ModShift) != 0 ? "Shift+" : "") +
                      ((s.Modifier & Macro.ModAlt) != 0 ? "Alt+" : "") +
                      ((s.Modifier & Macro.ModGui) != 0 ? "Win+" : "");
        return mods + Macro.KeyName(s.Key);
    }

    private static string Label(Macro.Step s) => s.Kind switch
    {
        Macro.Kind.KeyDown or Macro.Kind.MouseDown => Name(s) + " ↓",
        Macro.Kind.KeyUp or Macro.Kind.MouseUp => Name(s) + " ↑",
        Macro.Kind.Delay => $"{s.DelayMs} ms",
        _ => "?",
    };

    private void ShowMovement_Click(object sender, RoutedEventArgs e) { _sel = -1; Refresh(); }

    private void Refresh()
    {
        StepsList.Children.Clear();
        var cells = BuildCells();
        if (_sel >= cells.Count) _sel = cells.Count - 1;

        for (int i = 0; i < cells.Count; i++)
        {
            int pos = i;
            var cell = cells[i];
            bool selected = pos == _sel;
            bool delay = _steps[cell.Indices[0]].Kind == Macro.Kind.Delay;

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (selected)
            {
                row.Children.Add(Tool("◀", "Move earlier", pos > 0, () => Move(pos, -1)));
                row.Children.Add(new TextBlock { Text = cell.Label, Foreground = (Brush)FindResource("Text"), FontSize = 13, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(Tool("✕", "Delete", true, () => Delete(pos)));
                row.Children.Add(Tool("▶", "Move later", pos < cells.Count - 1, () => Move(pos, +1)));
            }
            else
            {
                row.Children.Add(new TextBlock { Text = cell.Label, Foreground = (Brush)FindResource(delay ? "Muted" : "Text"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            }

            var chip = new Border
            {
                Background = (Brush)FindResource("Card"),
                BorderBrush = (Brush)FindResource(selected ? "Accent" : "Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(3),
                Opacity = selected ? 1 : 0.72,          // half-transparent until picked
                Cursor = Cursors.Hand,
                Child = row,
                ToolTip = "Click to move or delete this action",
            };
            chip.MouseLeftButtonUp += (_, _) => { _sel = _sel == pos ? -1 : pos; Refresh(); };
            StepsList.Children.Add(chip);
        }

        if (cells.Count == 0)
            StepsList.Children.Add(new TextBlock
            {
                Text = "No actions yet — record, or add text, a key combo or a delay.",
                Foreground = (Brush)FindResource("Muted"), FontSize = 12, Margin = new Thickness(6),
            });
    }

    /// <summary>Small inline ◀ ✕ ▶ control inside a selected chip.</summary>
    private TextBlock Tool(string glyph, string tip, bool enabled, Action click)
    {
        var t = new TextBlock
        {
            Text = glyph, FontSize = 13, Margin = new Thickness(2, 0, 2, 0),
            Foreground = (Brush)FindResource(enabled ? "Text" : "Muted"),
            Opacity = enabled ? 1 : 0.35,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
            ToolTip = tip,
        };
        if (enabled)
            t.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return t;
    }

    /// <summary>Swap a chip with its neighbour, carrying all of its steps along.</summary>
    private void Move(int pos, int dir)
    {
        var cells = BuildCells();
        int other = pos + dir;
        if (other < 0 || other >= cells.Count) return;
        // Always lift the later of the two and drop it in front of the earlier one.
        var (lift, target) = dir > 0 ? (cells[other], cells[pos]) : (cells[pos], cells[other]);
        var moving = lift.Indices.Select(i => _steps[i]).ToList();
        int insertAt = target.Indices[0];
        foreach (int i in lift.Indices.OrderByDescending(i => i)) _steps.RemoveAt(i);
        _steps.InsertRange(insertAt, moving);
        _sel = other;
        Refresh();
    }

    private void Delete(int pos)
    {
        var cells = BuildCells();
        if (pos < 0 || pos >= cells.Count) return;
        foreach (int i in cells[pos].Indices.OrderByDescending(i => i)) _steps.RemoveAt(i);
        _sel = -1;
        Refresh();
    }

    // ---- live keystroke + mouse-click recording (G HUB's "record keystrokes") ----
    private Rodent.Core.Automation.LowLevelKeyboardHook? _recHook;
    private Rodent.Core.Automation.LowLevelMouseHook? _recMouse;
    private List<Macro.Step>? _recSteps;
    private List<(int x, int y)>? _recAt;    // where each step happened (clicks only)
    private byte _recMods;
    private System.Windows.Threading.DispatcherTimer? _recTimer;

    private static readonly (int x, int y) NoPoint = (int.MinValue, int.MinValue);

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recHook != null) { StopRecording(); return; }

        var steps = new List<Macro.Step>();
        var at = new List<(int x, int y)>();
        _recSteps = steps;
        _recAt = at;
        _recMods = 0;
        _recHook = new Rodent.Core.Automation.LowLevelKeyboardHook();
        _recHook.OnKey += (vk, scan, extended, down) =>
        {
            // Record the physical key (scan code), not the virtual key: a virtual key
            // carries the character of the ACTIVE layout, so a Turkish ö would be
            // stored as the US character on that key (a comma) and replay wrong.
            byte mod = Macro.ScanToModifier(scan, extended);
            if (mod == 0) mod = Macro.VkToModifier(vk);
            byte hid = Macro.ScanToHid(scan, extended);
            if (hid == 0) hid = Macro.VkToHid(vk);

            if (mod != 0)
            {
                // A modifier is recorded twice over: as a key in its own right, so
                // holding or tapping Shift alone lands in the macro, AND folded into
                // the modifier byte of the keys pressed while it is held, so Ctrl+C
                // still encodes as one onboard action. Belt and braces: the chip
                // reads a modifier usage in the key field as that modifier, and the
                // player replays whichever it sees.
                if (down && (_recMods & mod) != 0) return;   // keyboard auto-repeat
                _recMods = down ? (byte)(_recMods | mod) : (byte)(_recMods & ~mod);
                if (hid == 0) hid = Macro.VkToModifierHid(vk);
                if (hid == 0) return;
                lock (steps)
                {
                    steps.Add(new Macro.Step(down ? Macro.Kind.KeyDown : Macro.Kind.KeyUp, 0, hid));
                    at.Add(NoPoint);
                }
                return;
            }

            if (hid == 0) return;
            lock (steps)
            {
                steps.Add(new Macro.Step(down ? Macro.Kind.KeyDown : Macro.Kind.KeyUp, _recMods, hid));
                at.Add(NoPoint);
            }
        };
        _recHook.Start();

        // Clicks are taken everywhere, with the screen point kept alongside: the
        // click that ends the recording (on Save, or anywhere else in this window)
        // is trimmed afterwards instead of filtering the whole window out, which is
        // what made "record a click" look broken. Nothing here touches the UI —
        // a Dispatcher.Invoke from the hook thread would block the callback, and a
        // slow callback gets the hook silently removed by Windows.
        _recMouse = new Rodent.Core.Automation.LowLevelMouseHook();
        _recMouse.OnButton += (mask, down, x, y) =>
        {
            lock (steps)
            {
                steps.Add(new Macro.Step(down ? Macro.Kind.MouseDown : Macro.Kind.MouseUp, 0, (byte)mask));
                at.Add((x, y));
            }
        };
        _recMouse.Start();

        // Take focus off the controls so typed keys only feed the recording (a
        // focused button would otherwise be "clicked" by Space/Enter).
        System.Windows.Input.Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, null);
        RecordBtn.Content = "■ Stop recording (hover here)";
        // Stop on HOVER so the stopping click never lands in the recording.
        RecordBtn.MouseEnter += StopOnHover;

        RecHint.Visibility = Visibility.Visible;
        _recTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _recTimer.Tick += (_, _) => UpdateHint();
        _recTimer.Start();
        UpdateHint();
    }

    /// <summary>Live "what have I captured so far" line while recording.</summary>
    private void UpdateHint()
    {
        var steps = _recSteps;
        if (steps == null) return;
        int keys, clicks;
        lock (steps)
        {
            keys = steps.Count(s => s.Kind == Macro.Kind.KeyDown);
            clicks = steps.Count(s => s.Kind == Macro.Kind.MouseDown);
        }
        RecHint.Text = $"● Recording — {keys} keystroke(s), {clicks} click(s). " +
            "Keys (Shift and the other modifiers included) and clicks are captured anywhere on screen. " +
            "Move the mouse over “Stop recording” to finish — the click that stops it is dropped.";
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
        _recTimer?.Stop();
        _recTimer = null;
        RecHint.Visibility = Visibility.Collapsed;
        RecordBtn.Content = "● Record keys + clicks";
        var steps = _recSteps ?? new List<Macro.Step>();
        var at = _recAt ?? new List<(int x, int y)>();
        _recSteps = null;
        _recAt = null;
        TrimStoppingClick(steps, at);
        if (steps.Count == 0)
        {
            Dialogs.Info(this, "Nothing was recorded.\n\nPress the keys and click where you want them replayed — " +
                "the recorder takes them anywhere on screen. Clicks inside this window that end the recording are dropped.");
            return;
        }
        _steps.AddRange(steps);
        _sel = -1;
        Refresh();
    }

    /// <summary>
    /// Drop the trailing clicks that landed on the editor window — the press on
    /// Save (or Cancel, or the Record button) that ended the recording is UI, not
    /// part of the macro. Only the tail is trimmed: a deliberate click on this
    /// window earlier in the sequence is kept.
    /// </summary>
    private void TrimStoppingClick(List<Macro.Step> steps, List<(int x, int y)> at)
    {
        if (!GetWindowRect(new System.Windows.Interop.WindowInteropHelper(this).Handle, out RECT r)) return;
        for (int i = Math.Min(steps.Count, at.Count) - 1; i >= 0; i--)
        {
            if (steps[i].Kind != Macro.Kind.MouseDown && steps[i].Kind != Macro.Kind.MouseUp) break;
            var (x, y) = at[i];
            if (x < r.Left || x > r.Right || y < r.Top || y > r.Bottom) break;
            steps.RemoveAt(i);
            at.RemoveAt(i);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Alt+F4 while recording lands here — treat it as "stop", not "close".
        if (_recHook != null) { e.Cancel = true; StopRecording(); return; }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _recTimer?.Stop();
        _recHook?.Dispose();
        _recMouse?.Dispose();
        base.OnClosed(e);
    }

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
        _steps.AddRange(steps);
        _sel = -1;
        Refresh();
    }

    private void AddKey_Click(object sender, RoutedEventArgs e)
    {
        var got = Dialogs.CaptureCombo(this);   // press it, don't type its name
        if (got == null) return;
        var (mod, key, _) = got.Value;
        _steps.Add(new Macro.Step(Macro.Kind.KeyDown, mod, key));
        _steps.Add(new Macro.Step(Macro.Kind.KeyUp, mod, key));
        _sel = -1;
        Refresh();
    }

    private void AddDelay_Click(object sender, RoutedEventArgs e)
    {
        string? s = Dialogs.Prompt(this, "Add delay", "Delay in milliseconds");
        if (!ushort.TryParse(s, out ushort ms)) return;
        _steps.Add(new Macro.Step(Macro.Kind.Delay, DelayMs: ms));
        _sel = -1;
        Refresh();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_recHook != null) StopRecording();
        if (_steps.Count == 0) { Dialogs.Info(this, "Add at least one action."); return; }

        // Recorded and typed sequences carry no timing of their own; played back
        // with zero gaps, games and some apps drop keys. Space them out unless the
        // user has put an explicit delay there already.
        var steps = new List<Macro.Step>(_steps.Count * 2);
        bool std = StdDelay.IsChecked == true;
        for (int i = 0; i < _steps.Count; i++)
        {
            if (std && i > 0 && _steps[i].Kind != Macro.Kind.Delay && _steps[i - 1].Kind != Macro.Kind.Delay)
                steps.Add(new Macro.Step(Macro.Kind.Delay, DelayMs: Macro.StandardDelayMs));
            steps.Add(_steps[i]);
        }

        Repeat = TypeHeld.IsChecked == true ? Macro.RepeatMode.WhileHeld
               : TypeToggle.IsChecked == true ? Macro.RepeatMode.Toggle
               : TypeHold.IsChecked == true ? Macro.RepeatMode.HoldToggle
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
