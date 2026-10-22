using System.IO;
using System.Windows;

namespace ControllerAudioSwitcher.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            LogCrash("AppDomain", args.ExceptionObject as Exception);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            LogCrash("Dispatcher", args.Exception);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            LogCrash("TaskScheduler", args.Exception);
            args.SetObserved();
        };
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appDir = Path.Combine(appData, "ControllerAudioSwitcher");
            Directory.CreateDirectory(appDir);
            string logPath = Path.Combine(appDir, "crash.log");
            string text = $"[{DateTime.Now:O}] [{source}] {ex}\n";
            File.AppendAllText(logPath, text);
        }
        catch { }
    }
}
