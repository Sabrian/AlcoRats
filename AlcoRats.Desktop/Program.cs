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
        AppHost.Initialize(services =>
        {
            services.AddSingleton<WindowsSpeechTtsEngine>();
            services.AddSingleton<ITtsEngine>(sp => new CompositeTtsEngine(
                sp.GetRequiredService<PiperTtsEngine>(),
                sp.GetRequiredService<WindowsSpeechTtsEngine>()));
        });

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            AppHost.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
