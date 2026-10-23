using System.Threading.Tasks;

namespace AlcoRats.Services;

/// <summary>
/// Compatibility facade for media Play/Pause → microphone toggle.
/// </summary>
public static class ConferenceHotkeys
{
    public static Task<bool> ToggleMicrophoneAsync()
        => ConferenceSession.ToggleMicrophoneAsync(debounce: true);
}
