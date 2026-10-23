using System;
using AlcoRats.Services.Speech;
using Avalonia;

namespace AlcoRats.Desktop;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ConfigureSpeech();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void ConfigureSpeech()
    {
        TtsService.Configure(new CompositeTtsEngine(
            new PiperTtsEngine(),
            new WindowsSpeechTtsEngine()));
        ParticipantAnnouncer.Start();
    }
}
