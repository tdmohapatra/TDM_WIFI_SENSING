using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace StarSensing.Dashboard;

public partial class App : Application
{
    private static DateTime _lastDialogUtc = DateTime.MinValue;
    private static string? _lastDialogMessage;
    private static readonly TimeSpan DialogCooldown = TimeSpan.FromSeconds(8);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            LogException("UI thread", args.Exception);
            MaybeShowErrorDialog(args.Exception.Message);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LogException("AppDomain", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogException("Task", args.Exception);
            args.SetObserved();
        };
    }

    private static void LogException(string source, Exception ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StarSensing", "logs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "dashboard-errors.log");
            File.AppendAllText(path,
                $"[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort logging only.
        }
    }

    private static void MaybeShowErrorDialog(string message)
    {
        var now = DateTime.UtcNow;
        if (string.Equals(_lastDialogMessage, message, StringComparison.Ordinal) &&
            now - _lastDialogUtc < DialogCooldown)
            return;

        _lastDialogMessage = message;
        _lastDialogUtc = now;
        MessageBox.Show(
            $"STAR_SENSING hit an error but will keep running.\n\n{message}",
            "STAR_SENSING",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
