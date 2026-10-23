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
