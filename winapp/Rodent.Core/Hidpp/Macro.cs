namespace Rodent.Core.Hidpp;

/// <summary>
/// Encodes Logitech onboard macros (feature 0x8100 macro sectors). Instruction
/// format from libratbag src/hidpp20.c: 3-byte opcodes, terminated by END (0xFF),
/// stored inside 16-byte chunks. v1 supports single-chunk macros (up to 5
/// instructions) which cover typical mouse macros (key combos, double-click,
/// short sequences). Longer macros need JUMP chaining across chunks (future).
/// </summary>
public static class Macro
{
    // Onboard-macro opcodes (libratbag hidpp20.h). 3-byte unless noted.
    public const byte NOOP = 0x00;                   // 1 byte
    public const byte WAIT_FOR_RELEASE = 0x01;       // 1 byte
    public const byte REPEAT_WHILE_PRESSED = 0x02;   // 1 byte — loop back to start while held
    public const byte REPEAT_UNTIL_CANCELED = 0x03;  // 1 byte — loop until re-pressed (toggle)
    private const byte DELAY = 0x40;
    private const byte BUTTON_DOWN = 0x41, BUTTON_UP = 0x42;
    private const byte KEY_PRESS = 0x43, KEY_RELEASE = 0x44;
    private const byte CONS_DOWN = 0x45, CONS_UP = 0x46;
    private const byte JUMP = 0x60;
    public const byte END = 0xFF;                     // 1 byte

    /// <summary>Byte length of the instruction at data[pos], or 0 if unrecognised.</summary>
    public static int OpLength(byte op) => op switch
    {
        NOOP or WAIT_FOR_RELEASE or REPEAT_WHILE_PRESSED or REPEAT_UNTIL_CANCELED or END => 1,
        DELAY or BUTTON_DOWN or BUTTON_UP or KEY_PRESS or KEY_RELEASE or CONS_DOWN or CONS_UP or JUMP => 3,
        _ => 0,
    };

    /// <summary>Playback behavior (mirrors G HUB's macro type selector).</summary>
    public enum RepeatMode { Once, WhileHeld, Toggle, Sequence }

    // HID modifier bitmask
    public const byte ModCtrl = 0x01, ModShift = 0x02, ModAlt = 0x04, ModGui = 0x08;

    // Mouse kinds appended so profiles.json step values stay stable. For mouse
    // steps, Key holds the onboard button mask (1/2/4/8/16 = L/R/M/Back/Forward).
    public enum Kind { KeyDown, KeyUp, Delay, MouseDown, MouseUp }

    public sealed record Step(Kind Kind, byte Modifier = 0, byte Key = 0, ushort DelayMs = 0);

    /// <summary>Display name for a mouse-step button mask.</summary>
    public static string MouseButtonName(byte mask) => mask switch
    {
        0x01 => "Left Click", 0x02 => "Right Click", 0x04 => "Middle Click",
        0x08 => "Back", 0x10 => "Forward", _ => $"Button 0x{mask:X2}",
    };

    /// <summary>Standard inter-event delay G HUB inserts (ms).</summary>
    public const ushort StandardDelayMs = 50;

    /// <summary>Gap before a loop jumps back, so a held/toggled macro doesn't run
    /// its next iteration before the last key releases (that collision is what
    /// leaves keys — e.g. Shift — stuck and garbles the output).</summary>
    public const ushort LoopGapMs = 120;

    // G HUB stores macros linearly (instructions may cross 16-byte boundaries,
    // no JUMP), so length is limited only by free space in the macro sector.
    // The repeat opcode is a loop TERMINATOR: "run the body, then if the condition
    // still holds jump back to the macro's start, else fall through to END". So it
    // goes at the END, not the front (the earlier prefix placement never looped).
    public static byte[] Encode(IReadOnlyList<Step> steps, RepeatMode repeat = RepeatMode.Once)
    {
        var b = new List<byte>(steps.Count * 3 + 4);
        // Toggle: wait for the starting click to be released before looping, so the
        // NEXT press is seen as a fresh event the firmware can cancel on. Without
        // this the loop swallows the re-press and never stops.
        if (repeat == RepeatMode.Toggle) b.Add(WAIT_FOR_RELEASE);
        foreach (var s in steps)
        {
            switch (s.Kind)
            {
                case Kind.KeyDown: b.Add(KEY_PRESS); b.Add(s.Modifier); b.Add(s.Key); break;
                case Kind.KeyUp: b.Add(KEY_RELEASE); b.Add(s.Modifier); b.Add(s.Key); break;
                case Kind.Delay: b.Add(DELAY); b.Add((byte)(s.DelayMs >> 8)); b.Add((byte)(s.DelayMs & 0xFF)); break;
                case Kind.MouseDown: b.Add(BUTTON_DOWN); b.Add(0x00); b.Add(s.Key); break; // u16 BE button mask
                case Kind.MouseUp: b.Add(BUTTON_UP); b.Add(0x00); b.Add(s.Key); break;
            }
        }
        if (repeat is RepeatMode.WhileHeld or RepeatMode.Toggle)
        {
            // Space the loop iterations apart (unless the body already ends on a delay).
            if (steps.Count == 0 || steps[^1].Kind != Kind.Delay)
            {
                b.Add(DELAY); b.Add((byte)(LoopGapMs >> 8)); b.Add((byte)(LoopGapMs & 0xFF));
            }
            b.Add(repeat == RepeatMode.WhileHeld ? REPEAT_WHILE_PRESSED : REPEAT_UNTIL_CANCELED);
        }
        b.Add(END);
        return b.ToArray();
    }

    /// <summary>Press then release a key (with optional modifier) — one keystroke = 2 steps.</summary>
    public static IEnumerable<Step> Keystroke(byte key, byte modifier = 0)
    {
        yield return new Step(Kind.KeyDown, modifier, key);
        yield return new Step(Kind.KeyUp, modifier, key);
    }

    /// <summary>Build steps that type ASCII text (US layout), with G HUB-style delays.</summary>
    public static List<Step> TypeText(string text)
    {
        var steps = new List<Step>();
        foreach (char c in text)
        {
            var (key, shift) = CharToKey(c);
            if (key == 0) continue;
            steps.Add(new Step(Kind.KeyDown, shift ? ModShift : (byte)0, key));
            steps.Add(new Step(Kind.Delay, DelayMs: StandardDelayMs));
            steps.Add(new Step(Kind.KeyUp, shift ? ModShift : (byte)0, key));
            steps.Add(new Step(Kind.Delay, DelayMs: StandardDelayMs));
        }
        return steps;
    }

    // Modifier keycodes (0xE0-0xE7) and printable-key naming, for decoding macros.
    public static string KeyName(byte key, byte modifier = 0)
    {
        string name = key switch
        {
            0xE0 or 0xE4 => "Ctrl",
            0xE1 or 0xE5 => "Shift",
            0xE2 or 0xE6 => "Alt",
            0xE3 or 0xE7 => "Win",
            0x28 => "Enter", 0x29 => "Esc", 0x2A => "Backspace", 0x2B => "Tab", 0x2C => "Space",
            0x49 => "Insert", 0x4A => "Home", 0x4B => "PgUp", 0x4C => "Delete", 0x4D => "End", 0x4E => "PgDn",
            0x4F => "Right", 0x50 => "Left", 0x51 => "Down", 0x52 => "Up",
            >= 0x68 and <= 0x73 => $"F{13 + key - 0x68}",
            _ => KeyToCharName(key),
        };
        return name;
    }

    /// <summary>HID usage code → Windows virtual key (inverse of VkToHid), 0 if unmapped.</summary>
    public static ushort HidToVk(byte hid)
    {
        if (hid >= 0x04 && hid <= 0x1D) return (ushort)(0x41 + (hid - 0x04)); // A-Z
        if (hid >= 0x1E && hid <= 0x26) return (ushort)(0x31 + (hid - 0x1E)); // 1-9
        if (hid >= 0x3A && hid <= 0x45) return (ushort)(0x70 + (hid - 0x3A)); // F1-F12
        if (hid >= 0x68 && hid <= 0x73) return (ushort)(0x7C + (hid - 0x68)); // F13-F24
        return hid switch
        {
            0x27 => 0x30,                       // 0
            0x28 => 0x0D, 0x29 => 0x1B, 0x2A => 0x08, 0x2B => 0x09, 0x2C => 0x20,
            0x2D => 0xBD, 0x2E => 0xBB,
            0x2F => 0xDB, 0x30 => 0xDD, 0x31 => 0xDC, 0x35 => 0xC0,
            0x33 => 0xBA, 0x34 => 0xDE,
            0x36 => 0xBC, 0x37 => 0xBE, 0x38 => 0xBF,
            0x49 => 0x2D, 0x4A => 0x24, 0x4B => 0x21, 0x4C => 0x2E, 0x4D => 0x23, 0x4E => 0x22,
            0x4F => 0x27, 0x50 => 0x25, 0x51 => 0x28, 0x52 => 0x26,
            _ => 0,
        };
    }

    /// <summary>True if the key produces a printable character (typed-text macro).</summary>
    public static bool IsPrintable(byte key) =>
        (key >= 0x04 && key <= 0x27) || key == 0x2C ||
        (key >= 0x2D && key <= 0x31) || (key >= 0x33 && key <= 0x38);

    public static char KeyToChar(byte key, bool shift)
    {
        if (key >= 0x04 && key <= 0x1D) return (char)((shift ? 'A' : 'a') + (key - 0x04));
        if (key >= 0x1E && key <= 0x26)
            return shift ? "!@#$%^&*("[key - 0x1E] : (char)('1' + (key - 0x1E));
        return (key, shift) switch
        {
            (0x27, false) => '0', (0x27, true) => ')',
            (0x2C, _) => ' ',
            (0x2D, false) => '-', (0x2D, true) => '_',
            (0x2E, false) => '=', (0x2E, true) => '+',
            (0x2F, false) => '[', (0x2F, true) => '{',
            (0x30, false) => ']', (0x30, true) => '}',
            (0x31, false) => '\\', (0x31, true) => '|',
            (0x33, false) => ';', (0x33, true) => ':',
            (0x34, false) => '\'', (0x34, true) => '"',
            (0x35, false) => '`', (0x35, true) => '~',
            (0x36, false) => ',', (0x36, true) => '<',
            (0x37, false) => '.', (0x37, true) => '>',
            (0x38, false) => '/', (0x38, true) => '?',
            _ => '\0',
        };
    }

    /// <summary>Windows virtual-key → HID usage code (for recording real keystrokes).</summary>
    public static byte VkToHid(int vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return (byte)(0x04 + (vk - 0x41)); // A-Z
        if (vk >= 0x31 && vk <= 0x39) return (byte)(0x1E + (vk - 0x31)); // 1-9
        if (vk >= 0x70 && vk <= 0x7B) return (byte)(0x3A + (vk - 0x70)); // F1-F12
        return vk switch
        {
            0x30 => 0x27,                       // 0
            0x0D => 0x28,                       // Enter
            0x1B => 0x29,                       // Esc
            0x08 => 0x2A,                       // Backspace
            0x09 => 0x2B,                       // Tab
            0x20 => 0x2C,                       // Space
            0xBD => 0x2D, 0xBB => 0x2E,         // - =
            0xBA => 0x33, 0xDE => 0x34,         // ; '
            0xBC => 0x36, 0xBE => 0x37, 0xBF => 0x38, // , . /
            0xDB => 0x2F, 0xDD => 0x30, 0xDC => 0x31, 0xC0 => 0x35, // [ ] \ `
            0x2E => 0x4C,                       // Delete
            0x27 => 0x4F, 0x25 => 0x50, 0x28 => 0x51, 0x26 => 0x52, // arrows R L D U
            0x24 => 0x4A, 0x23 => 0x4D, 0x21 => 0x4B, 0x22 => 0x4E, // Home End PgUp PgDn
            _ => 0,
        };
    }

    /// <summary>
    /// PS/2 set-1 scan code → HID usage. HID usages are physical key positions, so
    /// recording by scan code keeps macros layout-correct: pressing the key marked ö
    /// on a Turkish keyboard stores that position and replays as ö, whereas mapping
    /// the virtual-key would store the US character on that key (a comma) instead.
    /// </summary>
    public static byte ScanToHid(uint scan, bool extended)
    {
        if (extended)
            return scan switch
            {
                0x1C => 0x58, 0x1D => 0xE4, 0x35 => 0x54, 0x38 => 0xE6,
                0x47 => 0x4A, 0x48 => 0x52, 0x49 => 0x4B, 0x4B => 0x50,
                0x4D => 0x4F, 0x4F => 0x4D, 0x50 => 0x51, 0x51 => 0x4E,
                0x52 => 0x49, 0x53 => 0x4C, 0x5B => 0xE3, 0x5C => 0xE7,
                _ => 0,
            };
        return scan switch
        {
            0x01 => 0x29,                                                   // Esc
            >= 0x02 and <= 0x0B => (byte)(0x1E + (scan - 0x02)),            // 1-9, 0
            0x0C => 0x2D, 0x0D => 0x2E, 0x0E => 0x2A, 0x0F => 0x2B,         // - = Backspace Tab
            0x10 => 0x14, 0x11 => 0x1A, 0x12 => 0x08, 0x13 => 0x15, 0x14 => 0x17,  // Q W E R T
            0x15 => 0x1C, 0x16 => 0x18, 0x17 => 0x0C, 0x18 => 0x12, 0x19 => 0x13,  // Y U I O P
            0x1A => 0x2F, 0x1B => 0x30, 0x1C => 0x28, 0x1D => 0xE0,         // [ ] Enter LCtrl
            0x1E => 0x04, 0x1F => 0x16, 0x20 => 0x07, 0x21 => 0x09, 0x22 => 0x0A,  // A S D F G
            0x23 => 0x0B, 0x24 => 0x0D, 0x25 => 0x0E, 0x26 => 0x0F,         // H J K L
            0x27 => 0x33, 0x28 => 0x34, 0x29 => 0x35, 0x2A => 0xE1, 0x2B => 0x31,  // ; ' ` LShift \
            0x2C => 0x1D, 0x2D => 0x1B, 0x2E => 0x06, 0x2F => 0x19,         // Z X C V
            0x30 => 0x05, 0x31 => 0x11, 0x32 => 0x10,                       // B N M
            0x33 => 0x36, 0x34 => 0x37, 0x35 => 0x38,                       // , . /
            0x36 => 0xE5, 0x38 => 0xE2, 0x39 => 0x2C, 0x3A => 0x39,         // RShift LAlt Space Caps
            >= 0x3B and <= 0x44 => (byte)(0x3A + (scan - 0x3B)),            // F1-F10
            0x57 => 0x44, 0x58 => 0x45,                                     // F11 F12
            _ => 0,
        };
    }

    private static Dictionary<byte, (ushort scan, bool ext)>? _hidToScan;

    /// <summary>
    /// HID usage → (set-1 scan code, extended flag); inverse of ScanToHid, built by
    /// walking that table. Used to replay macro steps host-side with SendInput —
    /// injecting by scan code keeps them layout-correct, like recording. (0, false)
    /// if unmapped (e.g. F13+ — inject those by virtual key instead).
    /// </summary>
    public static (ushort scan, bool extended) HidToScan(byte hid)
    {
        if (_hidToScan == null)
        {
            var map = new Dictionary<byte, (ushort, bool)>();
            for (uint s = 0x01; s <= 0x58; s++)
            {
                byte h = ScanToHid(s, false);
                if (h != 0 && !map.ContainsKey(h)) map[h] = ((ushort)s, false);
            }
            foreach (uint s in new uint[] { 0x1C, 0x1D, 0x35, 0x38, 0x47, 0x48, 0x49,
                                            0x4B, 0x4D, 0x4F, 0x50, 0x51, 0x52, 0x53, 0x5B, 0x5C })
            {
                byte h = ScanToHid(s, true);
                if (h != 0 && !map.ContainsKey(h)) map[h] = ((ushort)s, true);
            }
            _hidToScan = map;
        }
        return _hidToScan.TryGetValue(hid, out var hit) ? hit : ((ushort)0, false);
    }

    /// <summary>Modifier bit for a scan code (layout-independent), else 0.</summary>
    public static byte ScanToModifier(uint scan, bool extended) => (scan, extended) switch
    {
        (0x1D, false) or (0x1D, true) => ModCtrl,
        (0x2A, false) or (0x36, false) => ModShift,
        (0x38, false) or (0x38, true) => ModAlt,
        (0x5B, true) or (0x5C, true) => ModGui,
        _ => 0,
    };

    /// <summary>Modifier bit for a virtual-key, or 0 if it isn't a modifier.</summary>
    public static byte VkToModifier(int vk) => vk switch
    {
        0x11 or 0xA2 or 0xA3 => ModCtrl,
        0x10 or 0xA0 or 0xA1 => ModShift,
        0x12 or 0xA4 or 0xA5 => ModAlt,
        0x5B or 0x5C => ModGui,
        _ => 0,
    };

    private static string KeyToCharName(byte key)
    {
        char c = KeyToChar(key, false);
        return c != '\0' ? char.ToUpper(c).ToString() : $"0x{key:X2}";
    }

    /// <summary>
    /// Map a character to (HID key, modifier bits) through the user's ACTIVE keyboard
    /// layout, so text macros containing ö, ç, ğ… store the physical key that types
    /// them rather than being dropped as "not US-ASCII".
    /// </summary>
    public static (byte key, byte modifiers) CharToKeyLayout(char c) =>
        LayoutMap().TryGetValue(c, out var hit) ? hit : ((byte)0, (byte)0);

    private static Dictionary<char, (byte key, byte mods)>? _layoutMap;

    /// <summary>
    /// char → (physical key, modifiers) for every installed layout, built by asking
    /// the layout what each key position actually types. Uses ToUnicodeEx rather than
    /// VkKeyScanEx: the latter can only resolve characters that fit the layout's ANSI
    /// codepage, so on a Turkish keyboard ü/ö/ç worked but ş/ğ/ı (outside Latin-1)
    /// came back "unmappable".
    /// </summary>
    private static Dictionary<char, (byte, byte)> LayoutMap()
    {
        if (_layoutMap != null) return _layoutMap;
        var map = new Dictionary<char, (byte, byte)>();

        foreach (IntPtr hkl in CandidateLayouts())
        {
            for (uint scan = 0x01; scan <= 0x58; scan++)
            {
                byte hid = ScanToHid(scan, false);
                if (hid == 0) continue;
                uint vk = MapVirtualKeyExW(scan, MAPVK_VSC_TO_VK_EX, hkl);
                if (vk == 0) continue;

                // VK_TO_CHAR gives this key's character (uppercase for letter keys,
                // top bit set for dead keys).
                uint ch = MapVirtualKeyExW(vk, MAPVK_VK_TO_CHAR, hkl);
                if (ch == 0 || (ch & 0x80000000) != 0) continue;
                char c = (char)(ch & 0xFFFF);
                if (char.IsControl(c)) continue;

                // Case-fold in the LAYOUT's language: Turkish pairs I/ı and i/İ, which
                // sit on different keys, so folding with English rules maps them wrong.
                var culture = CultureFor(hkl);
                char lower = char.ToLower(c, culture), upper = char.ToUpper(c, culture);
                if (!map.ContainsKey(lower)) map[lower] = (hid, 0);
                if (upper != lower && !map.ContainsKey(upper)) map[upper] = (hid, ModShift);
            }
        }
        return _layoutMap = map;
    }

    /// <summary>The language a keyboard layout types in (low word of the HKL).</summary>
    private static System.Globalization.CultureInfo CultureFor(IntPtr hkl)
    {
        try { return System.Globalization.CultureInfo.GetCultureInfo((int)((long)hkl & 0xFFFF)); }
        catch { return System.Globalization.CultureInfo.InvariantCulture; }
    }

    /// <summary>Layouts to try: the foreground window's, ours, then everything installed.</summary>
    private static IEnumerable<IntPtr> CandidateLayouts()
    {
        var seen = new List<IntPtr>();
        void Add(IntPtr h) { if (h != IntPtr.Zero && !seen.Contains(h)) seen.Add(h); }

        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero) Add(GetKeyboardLayout(GetWindowThreadProcessId(fg, IntPtr.Zero)));
        Add(GetKeyboardLayout(0));

        var list = new IntPtr[16];
        int n = GetKeyboardLayoutList(list.Length, list);
        for (int i = 0; i < n && i < list.Length; i++) Add(list[i]);
        return seen;
    }

    private const uint MAPVK_VK_TO_CHAR = 2, MAPVK_VSC_TO_VK_EX = 3;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint MapVirtualKeyExW(uint code, uint mapType, IntPtr hkl);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int count, [System.Runtime.InteropServices.Out] IntPtr[] list);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);

    /// <summary>Map an ASCII char to (HID keycode, needsShift). Returns (0,false) if unmapped.</summary>
    public static (byte key, bool shift) CharToKey(char c)
    {
        if (c >= 'a' && c <= 'z') return ((byte)(0x04 + (c - 'a')), false);
        if (c >= 'A' && c <= 'Z') return ((byte)(0x04 + (c - 'A')), true);
        if (c >= '1' && c <= '9') return ((byte)(0x1E + (c - '1')), false);
        int sym = "!@#$%^&*(".IndexOf(c);
        if (sym >= 0) return ((byte)(0x1E + sym), true);
        return c switch
        {
            '0' => (0x27, false), ')' => (0x27, true),
            ' ' => (0x2C, false),
            '\n' => (0x28, false), // Enter
            '\t' => (0x2B, false), // Tab
            '-' => (0x2D, false), '_' => (0x2D, true),
            '=' => (0x2E, false), '+' => (0x2E, true),
            '[' => (0x2F, false), '{' => (0x2F, true),
            ']' => (0x30, false), '}' => (0x30, true),
            '\\' => (0x31, false), '|' => (0x31, true),
            ';' => (0x33, false), ':' => (0x33, true),
            '\'' => (0x34, false), '"' => (0x34, true),
            '`' => (0x35, false), '~' => (0x35, true),
            ',' => (0x36, false), '<' => (0x36, true),
            '.' => (0x37, false), '>' => (0x37, true),
            '/' => (0x38, false), '?' => (0x38, true),
            _ => ((byte)0, false),
        };
    }
}
