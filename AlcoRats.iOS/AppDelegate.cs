using AlcoRats.Hosting;
using AlcoRats.Services.Speech;
using Foundation;
using Avalonia;
using Avalonia.iOS;
using Microsoft.Extensions.DependencyInjection;

namespace AlcoRats.iOS;

[Register("AppDelegate")]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        AppHost.Initialize(services =>
        {
            services.AddSingleton<ITtsEngine>(sp => sp.GetRequiredService<PiperTtsEngine>());
        });

        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
