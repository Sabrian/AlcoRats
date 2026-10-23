using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace AlcoRats.Hosting;

/// <summary>
/// Captures unhandled exceptions from UI, AppDomain and unobserved tasks.
/// </summary>
public sealed class GlobalExceptionHandler
{
    private static GlobalExceptionHandler? _instance;
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private bool _uiHooked;

    private GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public static void Install(ILogger<GlobalExceptionHandler> logger)
    {
        if (_instance is not null)
            return;

        _instance = new GlobalExceptionHandler(logger);
        _instance.AttachProcessHandlers();
    }

    /// <summary>
    /// Must be called after Avalonia UI thread exists (e.g. OnFrameworkInitializationCompleted).
    /// </summary>
    public static void AttachUiDispatcher()
    {
        _instance?.AttachUiHandler();
    }

    private void AttachProcessHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        _logger.LogInformation("Process-level exception handlers installed");
    }

    private void AttachUiHandler()
    {
        if (_uiHooked)
            return;

        try
        {
            Dispatcher.UIThread.UnhandledException += OnUiUnhandledException;
            _uiHooked = true;
            _logger.LogInformation("UI dispatcher exception handler installed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UI dispatcher exception hook is unavailable");
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            _logger.LogCritical(ex, "Unhandled AppDomain exception (IsTerminating={IsTerminating})", e.IsTerminating);
        else
            _logger.LogCritical("Unhandled AppDomain exception object: {ExceptionObject}", e.ExceptionObject);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    private void OnUiUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Unhandled UI exception");
        e.Handled = true;
    }
}
