using System.Runtime.InteropServices;

namespace Rodent.Core.Automation;

/// <summary>
/// Injects keystrokes and text via SendInput — the mechanism that runs software
/// macros/remaps on the host (what G HUB does for per-app macros like "yazi").
/// </summary>
public static class InputInjector
{
    /// <summary>Type Unicode text (each char sent as a key-down/up with the Unicode flag).</summary>
    public static void TypeText(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            inputs.Add(KeyboardUnicode(c, down: true));
            inputs.Add(KeyboardUnicode(c, down: false));
        }
        Send(inputs);
    }

    /// <summary>Press a virtual-key chord (modifiers held around the key), e.g. Ctrl+C.</summary>
    public static void KeyChord(ushort virtualKey, params ushort[] modifiers)
    {
        var inputs = new List<INPUT>();
        foreach (var m in modifiers) inputs.Add(KeyboardVk(m, down: true));
        inputs.Add(KeyboardVk(virtualKey, down: true));
        inputs.Add(KeyboardVk(virtualKey, down: false));
        for (int i = modifiers.Length - 1; i >= 0; i--) inputs.Add(KeyboardVk(modifiers[i], down: false));
        Send(inputs);
    }

    private static void Send(List<INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        var arr = inputs.ToArray();
        SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyboardUnicode(char c, bool down) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (down ? 0u : KEYEVENTF_KEYUP),
            }
        }
    };

    private static INPUT KeyboardVk(ushort vk, bool down) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0u : KEYEVENTF_KEYUP } }
    };

    /// <summary>Tap a single virtual key (used for media / volume keys).</summary>
    public static void TapKey(ushort vk) =>
        Send(new List<INPUT> { KeyboardVk(vk, true), KeyboardVk(vk, false) });

    /// <summary>Press/release one key by scan code (layout-correct macro playback).</summary>
    public static void KeyScan(ushort scan, bool extended, bool down) => Send(new List<INPUT>
    {
        new()
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT
            {
                wScan = scan,
                dwFlags = KEYEVENTF_SCANCODE | (extended ? KEYEVENTF_EXTENDEDKEY : 0u)
                                             | (down ? 0u : KEYEVENTF_KEYUP),
            } }
        }
    });

    /// <summary>Press/release one key by virtual key (fallback for keys without a scan code).</summary>
    public static void KeyVk(ushort vk, bool down) => Send(new List<INPUT> { KeyboardVk(vk, down) });

    /// <summary>Known injectable mouse buttons for profile bindings.</summary>
    public static readonly string[] MouseButtons = { "Left Click", "Right Click", "Middle Click", "Back", "Forward" };

    /// <summary>"ctrl+shift+t" → (virtual key, modifier virtual keys) for SendInput. Null if unparsable.</summary>
    public static (ushort vk, ushort[] mods)? ParseChord(string s)
    {
        var mods = new List<ushort>();
        ushort vk = 0;
        foreach (var raw in s.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = raw.Trim().ToLowerInvariant();
            switch (p)
            {
                case "ctrl": case "control": mods.Add(VK_CONTROL); break;
                case "shift": mods.Add(VK_SHIFT); break;
                case "alt": mods.Add(VK_MENU); break;
                case "win": case "gui": case "meta": mods.Add(VK_LWIN); break;
                default:
                    if (p.Length == 1)
                    {
                        char c = char.ToUpperInvariant(p[0]);
                        if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) vk = c;
                        else vk = c switch
                        {
                            '-' => 0xBD, '+' or '=' => 0xBB, '.' => 0xBE, ',' => 0xBC,
                            ';' => 0xBA, '/' => 0xBF, '`' => 0xC0, '\'' => 0xDE,
                            '[' => 0xDB, ']' => 0xDD, '\\' => 0xDC, _ => (ushort)0,
                        };
                    }
                    else if (p.StartsWith('f') && int.TryParse(p[1..], out int n) && n is >= 1 and <= 24)
                        vk = (ushort)(n <= 12 ? 0x70 + n - 1 : 0x7C + n - 13); // F13-F24 follow at 0x7C
                    else vk = p switch
                    {
                        "enter" or "return" => 0x0D, "esc" or "escape" => 0x1B, "tab" => 0x09,
                        "space" => 0x20, "delete" or "del" => 0x2E, "backspace" => 0x08,
                        "home" => 0x24, "end" => 0x23, "pageup" => 0x21, "pagedown" => 0x22,
                        "up" => 0x26, "down" => 0x28, "left" => 0x25, "right" => 0x27,
                        "plus" => 0xBB, "minus" => 0xBD,
                        _ => 0,
                    };
                    break;
            }
        }
        return vk != 0 ? (vk, mods.ToArray()) : null;
    }

    /// <summary>Inject a full mouse button click (down+up) by catalog name.</summary>
    public static void ClickMouse(string name)
    {
        (uint down, uint up, uint data) = name switch
        {
            "Left Click" => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, 0u),
            "Right Click" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, 0u),
            "Middle Click" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP, 0u),
            "Back" => (MOUSEEVENTF_XDOWN, MOUSEEVENTF_XUP, 1u),      // XBUTTON1
            "Forward" => (MOUSEEVENTF_XDOWN, MOUSEEVENTF_XUP, 2u),   // XBUTTON2
            _ => (0u, 0u, 0u),
        };
        if (down == 0) return;
        Send(new List<INPUT> { Mouse(down, data), Mouse(up, data) });
    }

    /// <summary>Press/release a mouse button by onboard mask (1/2/4/8/16 = L/R/M/Back/Forward).</summary>
    public static void MouseButton(int mask, bool down)
    {
        (uint dn, uint up, uint data) = mask switch
        {
            0x01 => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, 0u),
            0x02 => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, 0u),
            0x04 => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP, 0u),
            0x08 => (MOUSEEVENTF_XDOWN, MOUSEEVENTF_XUP, 1u),
            0x10 => (MOUSEEVENTF_XDOWN, MOUSEEVENTF_XUP, 2u),
            _ => (0u, 0u, 0u),
        };
        if (dn == 0) return;
        Send(new List<INPUT> { Mouse(down ? dn : up, data) });
    }

    private static INPUT Mouse(uint flags, uint data) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } }
    };

    /// <summary>Lock the workstation.</summary>
    public static void LockWorkstation() => LockWorkStation();

    // Common virtual-key codes for chords.
    public const ushort VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LWIN = 0x5B;
    // Media / volume virtual keys.
    public const ushort VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
    public const ushort VK_MEDIA_NEXT = 0xB0, VK_MEDIA_PREV = 0xB1, VK_MEDIA_PLAY_PAUSE = 0xB3;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    // ---- Win32 ----
    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public int type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
