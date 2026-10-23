using AlcoRats.Hosting;
using AlcoRats.Services.Speech;
using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Microsoft.Extensions.DependencyInjection;

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

        AppHost.Initialize(services =>
        {
            services.AddSingleton<IAudioPlayer, AndroidAudioPlayer>();
            services.AddSingleton<AndroidTextToSpeechEngine>();
            services.AddSingleton<ITtsEngine>(sp => new CompositeTtsEngine(
                sp.GetRequiredService<PiperTtsEngine>(),
                sp.GetRequiredService<AndroidTextToSpeechEngine>()));
        });

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
}
