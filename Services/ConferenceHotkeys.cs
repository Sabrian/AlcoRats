using System;
using System.Threading;
using System.Threading.Tasks;

namespace AlcoRats.Services;

/// <summary>
/// Bridge between platform media keys (Android Play/Pause) and the active conference view.
/// </summary>
public static class ConferenceHotkeys
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Func<Task>? _toggleMicrophoneAsync;
    private static long _lastToggleTicks;

    public static void Register(Func<Task> toggleMicrophoneAsync)
        => _toggleMicrophoneAsync = toggleMicrophoneAsync;

    public static void Unregister(Func<Task> toggleMicrophoneAsync)
    {
        if (ReferenceEquals(_toggleMicrophoneAsync, toggleMicrophoneAsync))
            _toggleMicrophoneAsync = null;
    }

    public static async Task<bool> ToggleMicrophoneAsync()
    {
        var handler = _toggleMicrophoneAsync;
        if (handler is null)
            return false;

        // Headset buttons often fire twice; ignore a rapid second press.
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastToggleTicks) < 350)
            return true;

        if (!await Gate.WaitAsync(0).ConfigureAwait(false))
            return true;

        try
        {
            Interlocked.Exchange(ref _lastToggleTicks, now);
            await handler().ConfigureAwait(false);
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
