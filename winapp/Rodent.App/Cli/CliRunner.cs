using System.Text;
using Rodent.Core.Devices;
using Rodent.Core.Diagnostics;
using Rodent.Core.Model;

namespace Rodent.App.Cli;

/// <summary>
/// Commands that finish without ever showing a window. Returns an exit code, or
/// null when the request needs the GUI (normal launch).
/// </summary>
internal static class CliRunner
{
    public static int? Run(CliOptions o)
    {
        if (o.Errors.Count > 0)
        {
            foreach (string e in o.Errors) ConsoleHost.Error("rodent: " + e);
            ConsoleHost.Error("Try 'Rodent.exe --help'.");
            ConsoleHost.Finish();
            return 2;
        }

        if (o.Help) { ConsoleHost.Out(CliOptions.HelpText); ConsoleHost.Finish(); return 0; }
        if (o.Version) { ConsoleHost.Out($"Rodent {App.VersionTag}"); ConsoleHost.Finish(); return 0; }
        if (o.ShowLogPath) { ConsoleHost.Out(LogPathReport()); ConsoleHost.Finish(); return 0; }
        if (o.ListDevices) return ListDevices();

        return null;
    }

    private static string LogPathReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"level:     {Log.Level}");
        sb.AppendLine($"log file:  {Log.FilePath ?? "(disabled)"}");
        sb.Append($"directory: {Log.DefaultDirectory}");
        return sb.ToString();
    }

    private static int ListDevices()
    {
        ConsoleHost.Attach();
        List<IDeviceDriver> devices;
        try
        {
            devices = DeviceManager.Discover();
        }
        catch (Exception ex)
        {
            Log.Exception(ex, "--list");
            ConsoleHost.Error("Device scan failed: " + ex.Message);
            ConsoleHost.Finish();
            return 1;
        }

        if (devices.Count == 0)
        {
            ConsoleHost.Out("No supported devices found.");
            ConsoleHost.Finish();
            return 1;
        }

        var sb = new StringBuilder();
        foreach (var d in devices)
        {
            sb.AppendLine($"{d.Name}  [{d.Brand} {d.VendorId:X4}:{d.ProductId:X4}]  {d.Kind}, support: {d.Support}");
            foreach (var info in d.Info)
                sb.AppendLine($"    {info.Label}: {info.Value}");
            foreach (var s in d.Settings)
                sb.AppendLine($"    {s.Label} = {Describe(s)}");
            d.Dispose();
        }
        ConsoleHost.Out(sb.ToString().TrimEnd());
        ConsoleHost.Finish();
        return 0;
    }

    /// <summary>Current value of a setting, rendered the way the UI labels it.</summary>
    private static string Describe(Setting s)
    {
        try
        {
            switch (s)
            {
                case ToggleSetting t:
                    return t.Read() switch { true => "on", false => "off", _ => "?" };
                case RangeSetting r:
                    return r.Read()?.ToString() ?? "?";
                case ChoiceSetting c:
                    int? raw = c.Read();
                    if (raw == null) return "?";
                    foreach (var choice in c.Choices)
                        if (choice.Value == raw) return choice.Label;
                    return raw.Value.ToString();
                default:
                    return "?";
            }
        }
        catch (Exception ex)
        {
            Log.Exception(ex, $"reading setting {s.Name}");
            return "(read failed)";
        }
    }
}
