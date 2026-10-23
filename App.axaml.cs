using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AlcoRats.Hosting;
using AlcoRats.ViewModels;
using AlcoRats.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlcoRats;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (!AppHost.IsInitialized)
            throw new InvalidOperationException("AppHost must be initialized before Avalonia UI starts.");

        var services = AppHost.Services;
        var logger = services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("Avalonia framework initialization completed");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = services.GetRequiredService<MainViewModel>()
            };
            desktop.ShutdownRequested += (_, _) => AppHost.Shutdown();
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
        {
            activityLifetime.MainViewFactory = () => new MainView
            {
                DataContext = services.GetRequiredService<MainViewModel>()
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = services.GetRequiredService<MainViewModel>()
            };
        }

        // After UI thread exists: exception hooks + TTS announcer (no Piper download during splash).
        AppHost.StartRuntime();

        base.OnFrameworkInitializationCompleted();
    }
}
