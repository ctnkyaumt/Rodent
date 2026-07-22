using System.Text.Json;
using Rodent.Core.Hidpp;

namespace Rodent.Core.Automation;

/// <summary>A reusable macro in the user's library (name + steps + repeat mode).</summary>
public sealed class SavedMacro
{
    public string Name { get; set; } = "";
    public List<Macro.Step> Steps { get; set; } = new();
    public int Repeat { get; set; }                   // (int)Macro.RepeatMode
}

/// <summary>
/// The macro library, persisted in %AppData%\Rodent\macros.json. Assigning a
/// macro to a button only copies it, so reassigning no longer loses the
/// definition — the library keeps it for other buttons/profiles.
/// </summary>
public static class MacroStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static List<SavedMacro>? _cache;          // menus rebuild often; avoid re-reading the file

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rodent", "macros.json");

    public static List<SavedMacro> Load()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(Path))
                return _cache = JsonSerializer.Deserialize<List<SavedMacro>>(File.ReadAllText(Path)) ?? new();
        }
        catch { /* fall through to empty */ }
        return _cache = new();
    }

    private static void Save(List<SavedMacro> macros)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(macros, JsonOpts));
        _cache = macros;
    }

    /// <summary>Add or replace (by name).</summary>
    public static void Upsert(SavedMacro m)
    {
        var all = Load();
        all.RemoveAll(x => x.Name == m.Name);
        all.Add(m);
        Save(all);
    }

    public static void Delete(string name)
    {
        var all = Load();
        all.RemoveAll(x => x.Name == name);
        Save(all);
    }
}
