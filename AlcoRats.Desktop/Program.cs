using System;
using AlcoRats.Hosting;
using AlcoRats.Services.Speech;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;

namespace AlcoRats.Desktop;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            AppHost.Shutdown();
        }
    }

    // Called by runtime and by the Avalonia XAML previewer (which skips Main).
    public static AppBuilder BuildAvaloniaApp()
    {
        EnsureAppHost();
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }

    private static void EnsureAppHost()
    {
        if (AppHost.IsInitialized)
            return;

        AppHost.Initialize(services =>
        {
            services.AddSingleton<WindowsSpeechTtsEngine>();
            services.AddSingleton<ITtsEngine>(sp => new CompositeTtsEngine(
                sp.GetRequiredService<PiperTtsEngine>(),
                sp.GetRequiredService<WindowsSpeechTtsEngine>()));
        });
    }
}
