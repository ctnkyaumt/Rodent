using System.Diagnostics;
using Rodent.Core.Hidpp;

namespace Rodent.Core.Automation;

/// <summary>
/// The host-side per-app engine. The mouse's onboard buttons 4-8 are remapped to
/// signal keys F13-F17 (see ProfilesConfig); this service watches the foreground
/// app, intercepts those signal keys with a low-level keyboard hook, swallows
/// them, and runs the active profile's binding for that button — the software
/// layer G HUB uses for per-app behavior.
/// </summary>
public sealed class AutomationService : IDisposable
{
    public const ushort VK_F13 = 0x7C; // .. F17 = 0x80, mapping buttons 4..8
    private const ushort VK_ESCAPE = 0x1B;

    // Toggle-repeat safety: pace between repeats so the stop press always gets a
    // slice (a tight loop starves it — that is exactly why G HUB's toggle couldn't
    // be stopped), and a hard cap so it can never run forever. 50ms matches G HUB's
    // pace; our stop is a hook-thread swallow, decoupled from the injection loop,
    // so it stays responsive.
    private const int RepeatGapMs = 50;
    private const int RepeatMaxSeconds = 30;

    private readonly ForegroundWatcher _watcher = new();
    private readonly LowLevelKeyboardHook _hook = new();
    private ProfilesConfig _profiles;

    // Suppress keyboard auto-repeat while a signal key is held.
    private readonly bool[] _signalHeld = new bool[ProfilesConfig.LastButton + 1];

    // Key/click a button is currently holding down (null = nothing held).
    private readonly ButtonBinding?[] _pressed = new ButtonBinding?[ProfilesConfig.LastButton + 1];
    private readonly MacroPlayer.Held?[] _held = new MacroPlayer.Held?[ProfilesConfig.LastButton + 1];

    // Every injection runs here, one at a time, in the order it was asked for.
    // Two reasons: the hook callback must return fast (a slow one is unhooked by
    // Windows), and a press and its release must never overtake each other.
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _work = new();
    private Thread? _worker;

    private void Post(Action a) { try { _work.Add(a); } catch { /* shutting down */ } }

    // The one active repeat loop (null = none running).
    private readonly object _repeatLock = new();
    private CancellationTokenSource? _repeatCts;
    private MacroPlayer.Held? _hold;                   // keys a hold toggle is keeping down
    private int _heldRepeatButton = -1;               // button whose while-held loop is running
    private int _sniperButton = -1;                    // button holding DPI Shift (sniper)

    /// <summary>The device DPI bindings act on (wired by the app to the selected
    /// mouse). Any driver that implements <see cref="Rodent.Core.Devices.IDpiDevice"/>
    /// works; others simply ignore DPI bindings.</summary>
    public Func<Rodent.Core.Devices.IDpiDevice?>? DeviceProvider;

    public string CurrentApp => _watcher.CurrentApp;

    /// <summary>
    /// The app in front runs elevated and Rodent doesn't: Windows hides its key
    /// events from our hook and blocks anything we inject, so bindings do nothing
    /// there until Rodent is restarted as administrator.
    /// </summary>
    public bool ForegroundOutOfReach => _watcher.CurrentAppOutOfReach;
    /// <summary>A repeat loop or a hold toggle is running (both stop on Esc).</summary>
    public bool RepeatActive { get { lock (_repeatLock) return _repeatCts != null || _hold != null; } }
    public event Action<string>? AppChanged;
    public event Action<int, ButtonBinding>? BindingFired; // (button, binding)
    public event Action<bool>? RepeatStateChanged;         // true = started, false = stopped

    public AutomationService(ProfilesConfig? profiles = null)
    {
        _profiles = profiles ?? ProfilesConfig.Load();
        // Alt-tabbing out of the game must not leave Shift held in the next app.
        _watcher.AppChanged += a => { ReleaseHold(); AppChanged?.Invoke(a); };
        _hook.OnKeyDecide = HandleKey;
    }

    public void SetProfiles(ProfilesConfig profiles)
    {
        _profiles = profiles;
        if (!profiles.Enabled) { StopRepeat(); ReleaseAllButtonKeys(); }   // disarming kills any loop
    }

    public void Start()
    {
        if (_worker == null)
        {
            _worker = new Thread(RunWork) { IsBackground = true, Name = "RodentInject" };
            _worker.Start();
        }
        _watcher.Start();
        _hook.Start();
    }

    private void RunWork()
    {
        foreach (var job in _work.GetConsumingEnumerable())
        {
            try { job(); } catch { /* one bad injection must not end the queue */ }
        }
    }
    public void Stop() { StopRepeat(); ReleaseAllButtonKeys(); _hook.Stop(); _watcher.Stop(); }

    private bool HandleKey(int vk, bool down)
    {
        try { return HandleKeyCore(vk, down); }
        catch { return false; }                             // never throw inside the hook
    }

    private bool HandleKeyCore(int vk, bool down)
    {
        // Esc is the universal panic stop: it kills a runaway repeat and lets go of
        // anything a button is holding (in case its release was ever missed).
        if (down && vk == VK_ESCAPE)
        {
            if (RepeatActive) StopRepeat();
            ReleaseAllButtonKeys();
            return false;                                   // never swallowed
        }

        if (vk < VK_F13 || vk > VK_F13 + (ProfilesConfig.LastButton - ProfilesConfig.FirstButton))
            return false;                                   // not one of our signal keys
        if (!_profiles.Enabled) return false;               // disarmed: let F13+ through

        int button = ProfilesConfig.FirstButton + (vk - VK_F13);
        if (!down)
        {
            _signalHeld[button] = false;
            ReleaseButtonKey(button);                       // remapped key/click follows the button

            if (_heldRepeatButton == button) { _heldRepeatButton = -1; StopRepeat(); }
            if (_sniperButton == button)
            {
                _sniperButton = -1;
                // Resolve the device inside the worker — DeviceProvider may hop to
                // the UI thread, which must never happen on the hook thread.
                Task.Run(() => DeviceProvider?.Invoke()?.DpiAction("DPI Shift (sniper)", down: false));
            }
            return true;                                    // swallow the up, too
        }
        if (_signalHeld[button]) return true;               // keyboard auto-repeat
        _signalHeld[button] = true;

        var binding = _profiles.Resolve(_watcher.CurrentApp, button);
        // The signal key arriving and the binding resolving are the two things that
        // can't be seen from outside; log both, so "nothing happened in the game"
        // can be told apart from "the game ignored what we sent".
        Rodent.Core.Diagnostics.Log.Info(binding == null
            ? $"button {button} (F{13 + button - ProfilesConfig.FirstButton}) pressed in '{_watcher.CurrentApp}' — no binding"
            : $"button {button} (F{13 + button - ProfilesConfig.FirstButton}) pressed in '{_watcher.CurrentApp}' — " +
              $"{binding.Kind}: {binding.Describe()}");
        if (binding != null)
        {
            if (binding.Kind == BindingKind.Dpi)
            {
                if (binding.Text.Contains("Shift")) _sniperButton = button; // restore on the up
                string act = binding.Text;
                Task.Run(() => DeviceProvider?.Invoke()?.DpiAction(act, down: true));
            }
            else if (binding.Kind == BindingKind.RepeatText)
                ToggleRepeat(_ => InputInjector.TypeText(binding.Text));
            else if (binding.Kind == BindingKind.Macro && binding.MacroSteps is { Count: > 0 } steps)
            {
                switch ((Macro.RepeatMode)binding.MacroRepeat)
                {
                    case Macro.RepeatMode.Toggle:
                        ToggleRepeat(ct => MacroPlayer.Play(steps, ct));
                        break;
                    case Macro.RepeatMode.HoldToggle:
                        ToggleHold(steps);
                        break;
                    case Macro.RepeatMode.WhileHeld:
                        // Runs while the side button is held; the up event stops it.
                        StopRepeat();
                        _heldRepeatButton = button;
                        StartRepeat(ct => MacroPlayer.Play(steps, ct));
                        break;
                    default:
                        Task.Run(() =>
                        {
                            try { MacroPlayer.Play(steps); } catch { /* never kill the worker */ }
                            BindingFired?.Invoke(button, binding);
                        });
                        break;
                }
            }
            // A key or a click bound to a button IS that button while it is held:
            // pressed on the way down, released on the way up. The press itself goes
            // through the macro player, off the hook thread — the one path proven to
            // reach games (GTA IV took macro keys and ignored the same key sent
            // straight from the hook).
            else if (binding.Kind == BindingKind.KeyChord)
            {
                _pressed[button] = binding;
                var steps = KeySteps(binding);
                if (steps != null) Post(() => _held[button] = MacroPlayer.PlayHold(steps));
                else Post(() => InputInjector.KeyChordDown(binding.VirtualKey, binding.Modifiers));
                Notify(button, binding);
            }
            else if (binding.Kind == BindingKind.MouseClick && InputInjector.MaskOf(binding.Text) is var m and > 0)
            {
                _pressed[button] = binding;
                Post(() => InputInjector.MouseButton(m, down: true));
                Notify(button, binding);
            }
            else Task.Run(() =>
            {
                try { Execute(binding); } catch { /* never kill the worker */ }
                BindingFired?.Invoke(button, binding);
            });
        }
        return true;                                        // signal keys are always ours
    }

    /// <summary>Fire the UI event off the hook thread — handlers may hop to the UI.</summary>
    private void Notify(int button, ButtonBinding binding) =>
        Task.Run(() => { try { BindingFired?.Invoke(button, binding); } catch { } });

    /// <summary>
    /// A bound key as macro steps: the same HID-usage press a recorded macro
    /// produces, so both take the identical route to the screen. Null for keys the
    /// onboard usage table doesn't cover (F13+, media keys) — those still go
    /// through the plain injector.
    /// </summary>
    private static IReadOnlyList<Macro.Step>? KeySteps(ButtonBinding b)
    {
        byte hid = Macro.VkToHid(b.VirtualKey);
        if (hid == 0) hid = Macro.VkToModifierHid(b.VirtualKey);   // a bare Shift/Ctrl/Alt/Win
        if (hid == 0) return null;
        byte mods = 0;
        foreach (var m in b.Modifiers) mods |= Macro.VkToModifier(m);
        return new[] { new Macro.Step(Macro.Kind.KeyDown, mods, hid) };
    }

    /// <summary>Let go of the key or click a button is holding down, if any.</summary>
    private void ReleaseButtonKey(int button)
    {
        var binding = _pressed[button];
        if (binding == null) return;
        _pressed[button] = null;
        try
        {
            if (binding.Kind == BindingKind.KeyChord)
                // Decided inside the queue, not here: the press may still be waiting
                // its turn, and only the worker knows whether it left a key held.
                Post(() =>
                {
                    if (_held[button] is { } h) { _held[button] = null; MacroPlayer.Release(h); }
                    else InputInjector.KeyChordUp(binding.VirtualKey, binding.Modifiers);
                });
            else
            {
                int mask = InputInjector.MaskOf(binding.Text);
                Post(() => InputInjector.MouseButton(mask, down: false));
            }
        }
        catch { /* injection failed — nothing else to do */ }
    }

    /// <summary>Release everything any button is holding (disarm, stop, panic).</summary>
    private void ReleaseAllButtonKeys()
    {
        for (int b = ProfilesConfig.FirstButton; b <= ProfilesConfig.LastButton; b++)
            ReleaseButtonKey(b);
    }

    // ---- hold toggle: press to hold the keys down, press again to let go ----
    private void ToggleHold(IReadOnlyList<Macro.Step> steps)
    {
        lock (_repeatLock)
        {
            if (_hold != null) { var h = _hold; _hold = null; Task.Run(() => { MacroPlayer.Release(h); RepeatStateChanged?.Invoke(false); }); return; }
        }
        Task.Run(() =>
        {
            MacroPlayer.Held held;
            try { held = MacroPlayer.PlayHold(steps); }
            catch { return; }
            if (!held.Any) return;                          // nothing to hold: no state to keep
            lock (_repeatLock) _hold = held;
            RepeatStateChanged?.Invoke(true);
        });
    }

    /// <summary>Let go of a hold toggle, if one is active (Esc, app switch, disarm).</summary>
    public void ReleaseHold()
    {
        MacroPlayer.Held? h;
        lock (_repeatLock) { h = _hold; _hold = null; }
        if (h == null) return;
        try { MacroPlayer.Release(h); } catch { /* injection failed — nothing else to do */ }
        RepeatStateChanged?.Invoke(false);
    }

    // ---- repeat loops: toggle (press again / Esc / 30s) and while-held ----
    private void ToggleRepeat(Action<CancellationToken> iteration)
    {
        lock (_repeatLock)
            if (_repeatCts != null) { StopRepeat(); return; }   // second press = stop
        StartRepeat(iteration);
    }

    private void StartRepeat(Action<CancellationToken> iteration)
    {
        CancellationTokenSource cts;
        lock (_repeatLock)
        {
            _repeatCts?.Cancel();                           // replace any running loop
            _repeatCts?.Dispose();
            cts = new CancellationTokenSource();
            _repeatCts = cts;
        }
        RepeatStateChanged?.Invoke(true);
        Task.Run(() => RepeatLoop(iteration, cts));
    }

    public void StopRepeat()
    {
        ReleaseHold();                                      // Esc/disarm also lets go of a hold
        lock (_repeatLock)
        {
            if (_repeatCts == null) return;
            _repeatCts.Cancel();
            _repeatCts.Dispose();
            _repeatCts = null;
        }
        RepeatStateChanged?.Invoke(false);
    }

    private void RepeatLoop(Action<CancellationToken> iteration, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var deadline = DateTime.UtcNow.AddSeconds(RepeatMaxSeconds);
        try
        {
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                iteration(ct);
                for (int waited = 0; waited < RepeatGapMs && !ct.IsCancellationRequested; waited += 25)
                    Thread.Sleep(25);                       // paced so the stop press registers
            }
        }
        catch { /* device/injection error — just stop */ }
        finally
        {
            bool cleared = false;
            lock (_repeatLock)
            {
                if (_repeatCts == cts) { _repeatCts.Dispose(); _repeatCts = null; cleared = true; }
            }
            if (cleared) RepeatStateChanged?.Invoke(false); // e.g. hit the 30s cap
        }
    }

    /// <summary>Run a binding host-side (also used by UI "test" affordances).</summary>
    public static void Execute(ButtonBinding b)
    {
        switch (b.Kind)
        {
            case BindingKind.MouseClick:
                InputInjector.ClickMouse(b.Text);
                break;
            case BindingKind.KeyChord:
                // Same route as a macro key (see KeySteps), held long enough to be
                // seen by a game that samples the keyboard once a frame.
                if (KeySteps(b) is { } steps)
                {
                    var held = MacroPlayer.PlayHold(steps);
                    Thread.Sleep(InputInjector.TapMs);
                    MacroPlayer.Release(held);
                }
                else InputInjector.KeyChord(b.VirtualKey, b.Modifiers);
                break;
            case BindingKind.TypeText:
                InputInjector.TypeText(b.Text);
                break;
            case BindingKind.Macro:
                if (b.MacroSteps is { Count: > 0 }) MacroPlayer.Play(b.MacroSteps);
                break;
            case BindingKind.LaunchApp:
                try { Process.Start(new ProcessStartInfo(b.Text) { UseShellExecute = true }); }
                catch { /* bad path — ignore */ }
                break;
            case BindingKind.System:
                RunSystem(b.Text);
                break;
        }
    }

    private static void RunSystem(string name)
    {
        switch (name)
        {
            case SystemActions.VolumeUp: InputInjector.TapKey(InputInjector.VK_VOLUME_UP); break;
            case SystemActions.VolumeDown: InputInjector.TapKey(InputInjector.VK_VOLUME_DOWN); break;
            case SystemActions.Mute: InputInjector.TapKey(InputInjector.VK_VOLUME_MUTE); break;
            case SystemActions.PlayPause: InputInjector.TapKey(InputInjector.VK_MEDIA_PLAY_PAUSE); break;
            case SystemActions.NextTrack: InputInjector.TapKey(InputInjector.VK_MEDIA_NEXT); break;
            case SystemActions.PrevTrack: InputInjector.TapKey(InputInjector.VK_MEDIA_PREV); break;
            case SystemActions.LockPc: InputInjector.LockWorkstation(); break;
        }
    }

    public void Dispose()
    {
        StopRepeat();
        ReleaseAllButtonKeys();
        _work.CompleteAdding();          // let the queued releases run before we go
        _worker?.Join(1000);
        _hook.Dispose();
        _watcher.Dispose();
    }
}
