using System.Threading.Tasks;
using AlcoRats.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AlcoRats.Services;

/// <summary>
/// Compatibility facade for media Play/Pause → microphone toggle.
/// </summary>
public static class ConferenceHotkeys
{
    public static Task<bool> ToggleMicrophoneAsync()
    {
        if (!AppHost.IsInitialized)
            return Task.FromResult(false);

        return AppHost.Services
            .GetRequiredService<IConferenceSession>()
            .ToggleMicrophoneAsync(debounce: true);
    }
}
