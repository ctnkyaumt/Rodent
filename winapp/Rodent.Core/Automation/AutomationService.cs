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

    // The one active repeat loop (null = none running).
    private readonly object _repeatLock = new();
    private CancellationTokenSource? _repeatCts;
    private int _heldRepeatButton = -1;               // button whose while-held loop is running
    private int _sniperButton = -1;                    // button holding DPI Shift (sniper)

    /// <summary>The device DPI bindings act on (wired by the app to the selected mouse).</summary>
    public Func<Rodent.Core.Devices.LogiDevice?>? DeviceProvider;

    public string CurrentApp => _watcher.CurrentApp;
    public bool RepeatActive { get { lock (_repeatLock) return _repeatCts != null; } }
    public event Action<string>? AppChanged;
    public event Action<int, ButtonBinding>? BindingFired; // (button, binding)
    public event Action<bool>? RepeatStateChanged;         // true = started, false = stopped

    public AutomationService(ProfilesConfig? profiles = null)
    {
        _profiles = profiles ?? ProfilesConfig.Load();
        _watcher.AppChanged += a => AppChanged?.Invoke(a);
        _hook.OnKeyDecide = HandleKey;
    }

    public void SetProfiles(ProfilesConfig profiles)
    {
        _profiles = profiles;
        if (!profiles.Enabled) StopRepeat();                // disarming kills any loop
    }

    public void Start() { _watcher.Start(); _hook.Start(); }
    public void Stop() { StopRepeat(); _hook.Stop(); _watcher.Stop(); }

    private bool HandleKey(int vk, bool down)
    {
        try { return HandleKeyCore(vk, down); }
        catch { return false; }                             // never throw inside the hook
    }

    private bool HandleKeyCore(int vk, bool down)
    {
        // Esc is a universal panic stop for a runaway repeat — never swallowed.
        if (down && vk == VK_ESCAPE && RepeatActive) { StopRepeat(); return false; }

        if (vk < VK_F13 || vk > VK_F13 + (ProfilesConfig.LastButton - ProfilesConfig.FirstButton))
            return false;                                   // not one of our signal keys
        if (!_profiles.Enabled) return false;               // disarmed: let F13+ through

        int button = ProfilesConfig.FirstButton + (vk - VK_F13);
        if (!down)
        {
            _signalHeld[button] = false;
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
            else Task.Run(() =>
            {
                try { Execute(binding); } catch { /* never kill the worker */ }
                BindingFired?.Invoke(button, binding);
            });
        }
        return true;                                        // signal keys are always ours
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
                InputInjector.KeyChord(b.VirtualKey, b.Modifiers);
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

    public void Dispose() { StopRepeat(); _hook.Dispose(); _watcher.Dispose(); }
}
