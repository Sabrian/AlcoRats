using AlcoRats.Services.Speech;
using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace AlcoRats.Android;

[Application]
public class Application : AvaloniaAndroidApplication<App>
{
    protected Application(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        WebViewMediaPermissionBridge.Register();
        ConfigureSpeech();

        // Emulators often fail EGL and stay black; Software is a reliable fallback.
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .With(new AndroidPlatformOptions
            {
                RenderingMode =
                [
                    AndroidRenderingMode.Egl,
                    AndroidRenderingMode.Software
                ]
            });
    }

    private static void ConfigureSpeech()
    {
        PlatformAudioPlayer.PlayHandler = AndroidWavPlayer.PlayAsync;
        // Piper is self-contained; Android TTS is only a fallback if model download fails.
        TtsService.Configure(new CompositeTtsEngine(
            new PiperTtsEngine(),
            new AndroidTextToSpeechEngine()));
        ParticipantAnnouncer.Start();
    }
}
