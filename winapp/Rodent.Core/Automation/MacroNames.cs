using System.Text.Json;

namespace Rodent.Core.Automation;

/// <summary>
/// Remembers what the user called each onboard macro. The device's macro format has
/// no name field — only the instructions — so a macro read back from the mouse can
/// only be described by its contents ("Type: testing"). This keeps the labels the
/// user typed, keyed by device + button.
/// </summary>
public static class MacroNames
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static Dictionary<string, string>? _cache;

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rodent", "macronames.json");

    private static string Key(ushort vid, ushort pid, int button) => $"{vid:X4}:{pid:X4}:{button}";

    private static Dictionary<string, string> Load()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(Path))
                _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path));
        }
        catch { /* fall through to empty */ }
        return _cache ??= new Dictionary<string, string>();
    }

    public static string? Get(ushort vid, ushort pid, int button) =>
        Load().TryGetValue(Key(vid, pid, button), out var name) && name.Length > 0 ? name : null;

    public static void Set(ushort vid, ushort pid, int button, string? name)
    {
        var map = Load();
        if (string.IsNullOrWhiteSpace(name)) map.Remove(Key(vid, pid, button));
        else map[Key(vid, pid, button)] = name.Trim();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(map, JsonOpts));
        }
        catch { /* naming is cosmetic — never fail a remap over it */ }
    }
}
