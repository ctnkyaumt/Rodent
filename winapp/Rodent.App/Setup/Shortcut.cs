using System.IO;

namespace Rodent.App.Setup;

/// <summary>
/// Writes .lnk files through WScript.Shell by late binding — no COM reference and
/// no extra package, which keeps the single-file publish self-contained.
/// </summary>
internal static class Shortcut
{
    public static void Create(string linkPath, string target, string arguments, string description, string? workingDir = null)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) throw new InvalidOperationException("WScript.Shell is unavailable");

        object? shell = Activator.CreateInstance(shellType);
        if (shell == null) throw new InvalidOperationException("could not create WScript.Shell");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            object link = Invoke(shell, "CreateShortcut", linkPath)!;
            try
            {
                Set(link, "TargetPath", target);
                Set(link, "Arguments", arguments);
                Set(link, "Description", description);
                Set(link, "WorkingDirectory", workingDir ?? Path.GetDirectoryName(target)!);
                Set(link, "IconLocation", target + ",0");
                Invoke(link, "Save");
            }
            finally { Release(link); }
        }
        finally { Release(shell); }
    }

    public static void Delete(string linkPath)
    {
        try { if (File.Exists(linkPath)) File.Delete(linkPath); } catch { }
    }

    private static object? Invoke(object target, string method, params object[] args) =>
        target.GetType().InvokeMember(method, System.Reflection.BindingFlags.InvokeMethod, null, target, args);

    private static void Set(object target, string property, object value) =>
        target.GetType().InvokeMember(property, System.Reflection.BindingFlags.SetProperty, null, target, new[] { value });

    private static void Release(object comObject)
    {
        try { System.Runtime.InteropServices.Marshal.ReleaseComObject(comObject); } catch { }
    }
}
