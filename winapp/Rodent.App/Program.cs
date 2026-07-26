using Rodent.App.Cli;
using Rodent.Core.Diagnostics;

namespace Rodent.App;

/// <summary>
/// Custom entry point (App.xaml is a Page, not ApplicationDefinition) so console
/// commands can answer and exit with a proper exit code without WPF ever starting.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = CliOptions.Parse(args);

        if (options.Console) ConsoleHost.Attach();
        Log.Init(options.LogLevel, options.LogFile, options.Console ? ConsoleHost.EchoLine : null);
        Log.Info($"Rodent {App.VersionTag} — args: {(args.Length == 0 ? "(none)" : string.Join(' ', args))}");
        Log.Debug($"exe: {Environment.ProcessPath}");
        Log.Debug($"os: {Environment.OSVersion} clr: {Environment.Version}");
        Setup.RuntimeCheck.LogState();

        int? code = CliRunner.Run(options);
        if (code.HasValue)
        {
            Log.Info($"exit {code.Value}");
            Log.Close();
            return code.Value;
        }

        var app = new App(options);
        app.InitializeComponent();
        int result = app.Run();
        Log.Info($"exit {result}");
        Log.Close();
        return result;
    }
}
