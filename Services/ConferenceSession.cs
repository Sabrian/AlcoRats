using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AlcoRats.Services;

/// <summary>
/// Cross-platform conference control hub: state from Jitsi WebView + actions from UI/notification/hotkeys.
/// </summary>
public sealed class ConferenceSession(ILogger<ConferenceSession> logger) : IConferenceSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastMicTicks;

    private Func<Task> _toggleMicrophoneAsync;
    private Func<Task> _toggleCameraAsync;
    private Func<Task> _leaveAsync;
    private Func<Task> _openChatAsync;

    private ConferenceSessionState _state = ConferenceSessionState.Inactive;

    public event Action<ConferenceSessionState> StateChanged;

    public ConferenceSessionState State => _state;

    public void Register(
        Func<Task> toggleMicrophoneAsync,
        Func<Task> toggleCameraAsync,
        Func<Task> leaveAsync,
        Func<Task> openChatAsync)
    {
        _toggleMicrophoneAsync = toggleMicrophoneAsync;
        _toggleCameraAsync = toggleCameraAsync;
        _leaveAsync = leaveAsync;
        _openChatAsync = openChatAsync;
        logger.LogInformation("Conference session handlers registered");
    }

    public void Unregister(
        Func<Task> toggleMicrophoneAsync,
        Func<Task> toggleCameraAsync,
        Func<Task> leaveAsync,
        Func<Task> openChatAsync)
    {
        if (ReferenceEquals(_toggleMicrophoneAsync, toggleMicrophoneAsync))
            _toggleMicrophoneAsync = null;
        if (ReferenceEquals(_toggleCameraAsync, toggleCameraAsync))
            _toggleCameraAsync = null;
        if (ReferenceEquals(_leaveAsync, leaveAsync))
            _leaveAsync = null;
        if (ReferenceEquals(_openChatAsync, openChatAsync))
            _openChatAsync = null;

        Publish(ConferenceSessionState.Inactive);
        logger.LogInformation("Conference session handlers unregistered");
    }

    public void Publish(ConferenceSessionState state)
    {
        var previous = _state;
        _state = state;
        if (previous.IsActive != state.IsActive
            || previous.AudioMuted != state.AudioMuted
            || previous.VideoMuted != state.VideoMuted
            || previous.Participants.Count != state.Participants.Count
            || previous.UnreadChatCount != state.UnreadChatCount)
        {
            logger.LogDebug(
                "Conference state: Active={Active}, AudioMuted={AudioMuted}, VideoMuted={VideoMuted}, Participants={Count}, UnreadChat={Unread}",
                state.IsActive,
                state.AudioMuted,
                state.VideoMuted,
                state.Participants.Count,
                state.UnreadChatCount);
        }

        StateChanged?.Invoke(state);
    }

    public Task<bool> ToggleMicrophoneAsync(bool debounce = true)
        => InvokeAsync(_toggleMicrophoneAsync, nameof(ToggleMicrophoneAsync), debounce);

    public Task<bool> ToggleCameraAsync()
        => InvokeAsync(_toggleCameraAsync, nameof(ToggleCameraAsync), debounce: false);

    public Task<bool> LeaveAsync()
        => InvokeAsync(_leaveAsync, nameof(LeaveAsync), debounce: false);

    public Task<bool> OpenChatAsync()
        => InvokeAsync(_openChatAsync, nameof(OpenChatAsync), debounce: false);

    private async Task<bool> InvokeAsync(Func<Task> handler, string action, bool debounce)
    {
        if (handler is null)
        {
            logger.LogWarning("Conference action skipped — handler missing: {Action}", action);
            return false;
        }

        if (debounce)
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastMicTicks) < 350)
                return true;
            Interlocked.Exchange(ref _lastMicTicks, now);
        }

        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
            return true;

        try
        {
            logger.LogDebug("Conference action: {Action}", action);
            await handler().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Conference action failed: {Action}", action);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }
}
