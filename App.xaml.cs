using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using TrackBoxStudio.Services;

namespace TrackBoxStudio;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private readonly InpaintCoverageSettingsService _coverageSettingsService = new();

    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            _coverageSettingsService.EnsureSettingsFileExists();
        }
        catch (Exception ex)
        {
            ShowFatalError(ex);
            Shutdown(-1);
            return;
        }

        base.OnStartup(e);
    }

    public static void LogError(Exception ex, string category = "Error")
    {
        WriteErrorLog(ex, category);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatalError(e.Exception);
        e.Handled = true;
        Shutdown(-1);
    }

    private void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            ShowFatalError(exception);
            return;
        }

        var message = e.ExceptionObject?.ToString() ?? "Unknown unhandled exception.";
        ShowFatalError(new InvalidOperationException(message));
    }

    private static void ShowFatalError(Exception exception)
    {
        var logPath = WriteErrorLog(exception, "Fatal Error");

        MessageBox.Show(
            "TrackBoxStudio failed to start or encountered a fatal error.\n\n" +
            $"Details were written to:\n{logPath}\n\n" +
            exception.Message,
            "TrackBoxStudio Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static string WriteErrorLog(Exception exception, string category)
    {
        try
        {
            var logDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
            Directory.CreateDirectory(logDirectory);

            var logPath = Path.Combine(logDirectory, "trackboxstudio-error.log");
            var content = new StringBuilder()
                .AppendLine($"Timestamp: {DateTimeOffset.Now:O}")
                .AppendLine($"{category}:")
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();

            File.AppendAllText(logPath, content, Encoding.UTF8);
            return logPath;
        }
        catch
        {
            return Path.Combine(AppContext.BaseDirectory, "Data", "trackboxstudio-error.log");
        }
    }
}
