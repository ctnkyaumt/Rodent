using Rodent.Core.Hidpp;

namespace Rodent.Core.Automation;

/// <summary>
/// Every keyboard key a mouse button can be bound to, grouped the way the
/// assignments menu shows them ("Keyboard" submenu).
///
/// F13-F24 are deliberately absent: F13-F17 are the signal keys per-app profiles
/// put on buttons 4-8, so binding one here would fight the automation engine.
/// </summary>
public static class KeyCatalog
{
    public sealed record Key(string Name, ushort Vk);
    public sealed record Group(string Name, IReadOnlyList<Key> Keys);

    public static readonly IReadOnlyList<Group> Groups = Build();

    /// <summary>
    /// The 4-byte onboard action for a key, or null when the chip has no usage
    /// code for it (those keys stay software-only). Modifier keys are encoded as
    /// the modifier byte with no key, which is how the firmware sends them.
    /// </summary>
    public static byte[]? OnboardBytes(ushort vk)
    {
        byte mod = Macro.VkToModifier(vk);
        if (mod != 0) return new byte[] { 0x80, 0x02, mod, 0x00 };
        byte hid = Macro.VkToHid(vk);
        return hid == 0 ? null : new byte[] { 0x80, 0x02, 0x00, hid };
    }

    private static List<Group> Build()
    {
        var letters = new List<Key>();
        for (char c = 'A'; c <= 'Z'; c++) letters.Add(new Key(c.ToString(), c));

        var digits = new List<Key>();
        for (char c = '0'; c <= '9'; c++) digits.Add(new Key(c.ToString(), c));

        var fkeys = new List<Key>();
        for (int i = 1; i <= 12; i++) fkeys.Add(new Key($"F{i}", (ushort)(0x70 + i - 1)));

        var numpad = new List<Key>();
        for (int i = 0; i <= 9; i++) numpad.Add(new Key($"Num {i}", (ushort)(0x60 + i)));
        numpad.AddRange(new[]
        {
            new Key("Num /", 0x6F), new Key("Num *", 0x6A), new Key("Num -", 0x6D),
            new Key("Num +", 0x6B), new Key("Num .", 0x6E), new Key("Num Lock", 0x90),
        });

        return new List<Group>
        {
            new("Letters", letters),
            new("Numbers", digits),
            new("Function Keys", fkeys),
            new("Navigation", new[]
            {
                new Key("Up", 0x26), new Key("Down", 0x28), new Key("Left", 0x25), new Key("Right", 0x27),
                new Key("Home", 0x24), new Key("End", 0x23),
                new Key("Page Up", 0x21), new Key("Page Down", 0x22),
            }),
            new("Editing", new[]
            {
                new Key("Enter", 0x0D), new Key("Tab", 0x09), new Key("Space", 0x20),
                new Key("Backspace", 0x08), new Key("Delete", 0x2E), new Key("Insert", 0x2D),
                new Key("Esc", 0x1B),
            }),
            new("Symbols", new[]
            {
                new Key("- (minus)", 0xBD), new Key("= (equals)", 0xBB),
                new Key("[", 0xDB), new Key("]", 0xDD), new Key("\\", 0xDC),
                new Key("; (semicolon)", 0xBA), new Key("' (quote)", 0xDE),
                new Key(", (comma)", 0xBC), new Key(". (period)", 0xBE), new Key("/ (slash)", 0xBF),
                new Key("` (backtick)", 0xC0),
            }),
            new("Numpad", numpad),
            new("Modifiers", new[]
            {
                new Key("Left Ctrl", 0xA2), new Key("Right Ctrl", 0xA3),
                new Key("Left Shift", 0xA0), new Key("Right Shift", 0xA1),
                new Key("Left Alt", 0xA4), new Key("Right Alt", 0xA5),
                new Key("Left Win", 0x5B), new Key("Right Win", 0x5C),
            }),
            new("System Keys", new[]
            {
                new Key("Caps Lock", 0x14), new Key("Menu (context)", 0x5D),
                new Key("Print Screen", 0x2C), new Key("Scroll Lock", 0x91), new Key("Pause", 0x13),
            }),
        };
    }
}
