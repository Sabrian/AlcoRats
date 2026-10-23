using System;
using System.Threading;
using System.Threading.Tasks;

namespace AlcoRats.Services;

/// <summary>
/// Cross-platform conference control hub: state from Jitsi WebView + actions from UI/notification/hotkeys.
/// </summary>
public static class ConferenceSession
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static long _lastMicTicks;

    private static Func<Task>? _toggleMicrophoneAsync;
    private static Func<Task>? _toggleCameraAsync;
    private static Func<Task>? _leaveAsync;

    private static ConferenceSessionState _state = ConferenceSessionState.Inactive;

    public static event Action<ConferenceSessionState>? StateChanged;

    public static ConferenceSessionState State => _state;

    public static void Register(
        Func<Task> toggleMicrophoneAsync,
        Func<Task> toggleCameraAsync,
        Func<Task> leaveAsync)
    {
        _toggleMicrophoneAsync = toggleMicrophoneAsync;
        _toggleCameraAsync = toggleCameraAsync;
        _leaveAsync = leaveAsync;
    }

    public static void Unregister(
        Func<Task> toggleMicrophoneAsync,
        Func<Task> toggleCameraAsync,
        Func<Task> leaveAsync)
    {
        if (ReferenceEquals(_toggleMicrophoneAsync, toggleMicrophoneAsync))
            _toggleMicrophoneAsync = null;
        if (ReferenceEquals(_toggleCameraAsync, toggleCameraAsync))
            _toggleCameraAsync = null;
        if (ReferenceEquals(_leaveAsync, leaveAsync))
            _leaveAsync = null;

        Publish(ConferenceSessionState.Inactive);
    }

    public static void Publish(ConferenceSessionState state)
    {
        _state = state;
        StateChanged?.Invoke(state);
    }

    public static Task<bool> ToggleMicrophoneAsync(bool debounce = true)
        => InvokeAsync(_toggleMicrophoneAsync, debounce);

    public static Task<bool> ToggleCameraAsync()
        => InvokeAsync(_toggleCameraAsync, debounce: false);

    public static Task<bool> LeaveAsync()
        => InvokeAsync(_leaveAsync, debounce: false);

    private static async Task<bool> InvokeAsync(Func<Task>? handler, bool debounce)
    {
        if (handler is null)
            return false;

        if (debounce)
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastMicTicks) < 350)
                return true;
            Interlocked.Exchange(ref _lastMicTicks, now);
        }

        if (!await Gate.WaitAsync(0).ConfigureAwait(false))
            return true;

        try
        {
            await handler().ConfigureAwait(false);
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
