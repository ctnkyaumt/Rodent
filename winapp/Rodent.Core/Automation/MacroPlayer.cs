using Rodent.Core.Hidpp;

namespace Rodent.Core.Automation;

/// <summary>
/// Plays onboard-format macro steps host-side with SendInput — the same Step list
/// the MacroEditor produces, so one editor feeds both the mouse flash (onboard
/// profile) and per-app software bindings. Keys are injected by scan code (HID
/// usages are physical key positions), keeping recorded/typed macros
/// layout-correct on any keyboard.
/// </summary>
public static class MacroPlayer
{
    // HID modifier bit → that modifier key's HID usage (left-hand variants).
    private static readonly (byte bit, byte hid)[] ModKeys =
    {
        (Macro.ModCtrl, 0xE0), (Macro.ModShift, 0xE1), (Macro.ModAlt, 0xE2), (Macro.ModGui, 0xE3),
    };

    public static void Play(IReadOnlyList<Macro.Step> steps, CancellationToken ct = default)
    {
        byte held = 0;
        try
        {
            foreach (var s in steps)
            {
                if (ct.IsCancellationRequested) return;
                switch (s.Kind)
                {
                    case Macro.Kind.KeyDown:
                        // Each step carries the full modifier state it was recorded
                        // with; sync the held modifiers to it before the key goes down.
                        SyncModifiers(ref held, s.Modifier);
                        Key(s.Key, down: true);
                        break;
                    case Macro.Kind.KeyUp:
                        Key(s.Key, down: false);
                        break;
                    case Macro.Kind.MouseDown:
                        InputInjector.MouseButton(s.Key, down: true);
                        break;
                    case Macro.Kind.MouseUp:
                        InputInjector.MouseButton(s.Key, down: false);
                        break;
                    case Macro.Kind.Delay:
                        for (int w = 0; w < s.DelayMs && !ct.IsCancellationRequested; w += 25)
                            Thread.Sleep(Math.Min(25, s.DelayMs - w));
                        break;
                }
            }
        }
        finally { SyncModifiers(ref held, 0); } // never leave Shift/Ctrl stuck down
    }

    /// <summary>What a hold-toggle left pressed, so the next press can undo it.</summary>
    public sealed class Held
    {
        internal readonly HashSet<byte> Keys = new();      // HID usages
        internal readonly HashSet<byte> Buttons = new();   // onboard button masks
        internal byte Mods;
        public bool Any => Keys.Count > 0 || Buttons.Count > 0 || Mods != 0;
    }

    /// <summary>
    /// Play only the press half of a macro and leave it held: key/button releases
    /// are skipped, delays still run. This is what makes a toggle behave like
    /// holding the key (sprint) instead of tapping it over and over. Feed the
    /// result to <see cref="Release"/> on the next press.
    /// </summary>
    public static Held PlayHold(IReadOnlyList<Macro.Step> steps, CancellationToken ct = default)
    {
        var held = new Held();
        byte mods = 0;
        foreach (var s in steps)
        {
            if (ct.IsCancellationRequested) break;
            switch (s.Kind)
            {
                case Macro.Kind.KeyDown:
                    if (s.Modifier != 0)
                    {
                        SyncModifiers(ref mods, (byte)(mods | s.Modifier));
                        held.Mods = mods;
                    }
                    if (s.Key != 0) { Key(s.Key, down: true); held.Keys.Add(s.Key); }
                    break;
                case Macro.Kind.MouseDown:
                    InputInjector.MouseButton(s.Key, down: true);
                    held.Buttons.Add(s.Key);
                    break;
                case Macro.Kind.Delay:
                    for (int w = 0; w < s.DelayMs && !ct.IsCancellationRequested; w += 25)
                        Thread.Sleep(Math.Min(25, s.DelayMs - w));
                    break;
                // KeyUp / MouseUp are deliberately skipped — that is the hold.
            }
        }
        return held;
    }

    /// <summary>Let go of everything a <see cref="PlayHold"/> left down.</summary>
    public static void Release(Held held)
    {
        foreach (var k in held.Keys) Key(k, down: false);
        foreach (var b in held.Buttons) InputInjector.MouseButton(b, down: false);
        byte mods = held.Mods;
        SyncModifiers(ref mods, 0);
        held.Keys.Clear();
        held.Buttons.Clear();
        held.Mods = 0;
    }

    private static void SyncModifiers(ref byte held, byte wanted)
    {
        foreach (var (bit, hid) in ModKeys)
        {
            if ((wanted & bit) != 0 && (held & bit) == 0) Key(hid, down: true);
            else if ((wanted & bit) == 0 && (held & bit) != 0) Key(hid, down: false);
        }
        held = wanted;
    }

    private static void Key(byte hid, bool down)
    {
        var (scan, ext) = Macro.HidToScan(hid);
        if (scan != 0) { InputInjector.KeyScan(scan, ext, down); return; }
        ushort vk = Macro.HidToVk(hid);           // e.g. F13+ have no set-1 scan here
        if (vk != 0) InputInjector.KeyVk(vk, down);
    }
}
