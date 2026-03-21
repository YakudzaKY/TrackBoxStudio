using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace TrackBoxStudio;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
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
        var logPath = WriteFatalErrorLog(exception);

        MessageBox.Show(
            "TrackBoxStudio failed to start or encountered a fatal error.\n\n" +
            $"Details were written to:\n{logPath}\n\n" +
            exception.Message,
            "TrackBoxStudio Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static string WriteFatalErrorLog(Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
            Directory.CreateDirectory(logDirectory);

            var logPath = Path.Combine(logDirectory, "trackboxstudio-error.log");
            var content = new StringBuilder()
                .AppendLine($"Timestamp: {DateTimeOffset.Now:O}")
                .AppendLine("Unhandled exception:")
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
