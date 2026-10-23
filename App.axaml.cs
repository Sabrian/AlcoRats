using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AlcoRats.Hosting;
using AlcoRats.ViewModels;
using AlcoRats.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AlcoRats.Services;
using AlcoRats.Settings;

namespace AlcoRats;

public partial class App : Application
{
    public override void Initialize()
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.DefaultThreadCurrentCulture = ru;
        CultureInfo.DefaultThreadCurrentUICulture = ru;
        CultureInfo.CurrentCulture = ru;
        CultureInfo.CurrentUICulture = ru;

        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    private MainView CreateMainView()
    {
        var services = AppHost.Services;
        var model = AppHost.Services.GetRequiredService<MainViewModel>();
        var mainView = new MainView
        (
            services.GetRequiredService<IConferenceSession>(),
            services.GetRequiredService<AppSettings>(),
            services.GetRequiredService<ILogger<MainView>>()
        )
        {
            DataContext = model
        };

        return mainView;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // XAML previewer may reach here without Program.Main — allow a bare host.
        if (!AppHost.IsInitialized)
            AppHost.Initialize();

        var services = AppHost.Services;
        var logger = services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("Avalonia framework initialization completed");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow
            {
                DataContext = services.GetRequiredService<MainViewModel>()
            };
            mainWindow.SetMainView(CreateMainView());
            desktop.MainWindow = mainWindow;
            desktop.ShutdownRequested += (_, _) => AppHost.Shutdown();
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
        {
            activityLifetime.MainViewFactory = () => CreateMainView();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = CreateMainView();
        }

        // Skip TTS/background work in the designer preview process.
        if (!Design.IsDesignMode)
            AppHost.StartRuntime();

        base.OnFrameworkInitializationCompleted();
    }
}
