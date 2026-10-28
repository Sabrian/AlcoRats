using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace AlcoRats.Hosting;

/// <summary>
/// Captures unhandled exceptions from UI, AppDomain and unobserved tasks.
/// Fatal crashes show the exception text on screen before the process exits.
/// </summary>
public sealed class GlobalExceptionHandler
{
    private static GlobalExceptionHandler _instance;
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private bool _uiHooked;
    private int _fatalUiShown;

    /// <summary>
    /// Platform hook (e.g. Android AlertDialog). Must marshal to the UI thread and
    /// call <paramref name="done"/>.Set() when the user dismisses.
    /// </summary>
    public static Action<string, ManualResetEventSlim> FatalErrorPresenter { get; set; }

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

    /// <summary>Show exception text and block until dismissed (or timeout).</summary>
    public static void ShowFatal(Exception exception)
        => _instance?.PresentFatal(exception?.ToString() ?? "(null exception)", block: true);

    public static void ShowFatal(string message)
        => _instance?.PresentFatal(message, block: true);

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
        string text;
        if (e.ExceptionObject is Exception ex)
        {
            _logger.LogCritical(ex, "Unhandled AppDomain exception (IsTerminating={IsTerminating})", e.IsTerminating);
            text = ex.ToString();
        }
        else
        {
            text = e.ExceptionObject?.ToString() ?? "(null)";
            _logger.LogCritical("Unhandled AppDomain exception object: {ExceptionObject}", text);
        }

        if (e.IsTerminating)
            PresentFatal(text, block: true);
    }

    private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    private void OnUiUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        PresentFatal(e.Exception.ToString(), block: false);
    }

    private void PresentFatal(string message, bool block)
    {
        if (string.IsNullOrWhiteSpace(message))
            message = "(empty exception)";

        if (Interlocked.Exchange(ref _fatalUiShown, 1) == 1)
            return;

        // Waiting on the UI thread while the dialog needs that thread deadlocks.
        // Offload the wait to the pool; the presenter marshals Show() back to UI.
        if (block && IsAvaloniaUiThread())
        {
            PresentFatalBody(message, block: false);
            return;
        }

        try
        {
            PresentFatalBody(message, block);
        }
        finally
        {
            if (block)
                Interlocked.Exchange(ref _fatalUiShown, 0);
        }
    }

    private void PresentFatalBody(string message, bool block)
    {
        var done = new ManualResetEventSlim(false);
        var presented = false;

        var presenter = FatalErrorPresenter;
        if (presenter is not null)
        {
            try
            {
                presenter(message, done);
                presented = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Platform fatal-error presenter failed");
            }
        }

        if (!presented)
        {
            try
            {
                presented = TryShowAvaloniaFatal(message, done);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Avalonia fatal-error dialog failed");
            }
        }

        if (!presented)
        {
            if (block)
                Thread.Sleep(2500);
            return;
        }

        if (block)
        {
            done.Wait(TimeSpan.FromMinutes(2));
            return;
        }

        _ = Task.Run(() =>
        {
            done.Wait(TimeSpan.FromMinutes(2));
            Interlocked.Exchange(ref _fatalUiShown, 0);
        });
    }

    private static bool IsAvaloniaUiThread()
    {
        try
        {
            return Dispatcher.UIThread.CheckAccess();
        }
        catch
        {
            return false;
        }
    }

    private static bool TryShowAvaloniaFatal(string message, ManualResetEventSlim done)
    {
        if (Application.Current is null)
            return false;

        void Show()
        {
            var text = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16)
            };
            var scroll = new ScrollViewer
            {
                Content = text,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
            var ok = new Button
            {
                Content = "OK",
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 8, 16, 16),
                MinWidth = 88
            };
            var root = new DockPanel();
            DockPanel.SetDock(ok, Dock.Bottom);
            root.Children.Add(ok);
            root.Children.Add(scroll);

            var window = new Window
            {
                Title = "Критическая ошибка",
                Width = 520,
                Height = 420,
                CanResize = true,
                Content = root,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };

            ok.Click += (_, _) => window.Close();
            window.Closed += (_, _) => done.Set();

            if (Application.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow is { } owner)
                _ = window.ShowDialog(owner);
            else
                window.Show();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Show();
            return true;
        }

        Dispatcher.UIThread.Post(Show);
        return true;
    }
}
