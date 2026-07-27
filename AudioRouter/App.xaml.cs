using System.IO;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace AudioRouter;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "AudioRouter",
        "Logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(LogDirectory);

#if DEBUG
        const LogEventLevel minimumLevel = LogEventLevel.Debug;
#else
        const LogEventLevel minimumLevel = LogEventLevel.Information;
#endif

        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override(
                "Microsoft",
                LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty(
                "Application",
                "AudioRouter")
            .Enrich.WithProperty(
                "ProcessId",
                Environment.ProcessId)
            .WriteTo.File(
                Path.Combine(
                    LogDirectory,
                    "audio-router-.log"),
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                retainedFileCountLimit: 14,
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} " +
                "[{Level:u3}] {Message:lj} {Properties:j}" +
                "{NewLine}{Exception}");

#if DEBUG
        loggerConfiguration.WriteTo.Console(
            theme: ConsoleTheme.None,
            outputTemplate:
            "{Timestamp:HH:mm:ss.fff} [{Level:u3}] " +
            "{Message:lj}{NewLine}{Exception}");
#endif

        Log.Logger = loggerConfiguration.CreateLogger();

        DispatcherUnhandledException +=
            OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException +=
            OnUnhandledException;
        TaskScheduler.UnobservedTaskException +=
            OnUnobservedTaskException;

        try
        {
            Log.Information(
                "应用启动，日志目录：{LogDirectory}",
                LogDirectory);
            base.OnStartup(e);
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "应用启动失败");
            Log.CloseAndFlush();
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information(
            "应用退出，退出码：{ExitCode}",
            e.ApplicationExitCode);

        DispatcherUnhandledException -=
            OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -=
            OnUnhandledException;
        TaskScheduler.UnobservedTaskException -=
            OnUnobservedTaskException;

        try
        {
            base.OnExit(e);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "UI 线程发生未处理异常");
    }

    private static void OnUnhandledException(
        object? sender,
        UnhandledExceptionEventArgs e)
    {
        Log.Fatal(
            e.ExceptionObject as Exception,
            "进程发生未处理异常，正在终止：{IsTerminating}",
            e.IsTerminating);
    }

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(
            e.Exception,
            "后台任务发生未观察异常");
    }
}
