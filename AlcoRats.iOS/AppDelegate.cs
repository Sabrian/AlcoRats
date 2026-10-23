using AlcoRats.Services.Speech;
using Foundation;
using Avalonia;
using Avalonia.iOS;

namespace AlcoRats.iOS;

[Register("AppDelegate")]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        TtsService.Configure(new CompositeTtsEngine(new PiperTtsEngine()));
        ParticipantAnnouncer.Start();

        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
