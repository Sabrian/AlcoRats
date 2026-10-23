using System;
using System.Threading.Tasks;

namespace AlcoRats.Services;

public interface IConferenceSession
{
    ConferenceSessionState State { get; }

    event Action<ConferenceSessionState>? StateChanged;

    void Register(Func<Task> toggleMicrophoneAsync, Func<Task> toggleCameraAsync, Func<Task> leaveAsync);

    void Unregister(Func<Task> toggleMicrophoneAsync, Func<Task> toggleCameraAsync, Func<Task> leaveAsync);

    void Publish(ConferenceSessionState state);

    Task<bool> ToggleMicrophoneAsync(bool debounce = true);

    Task<bool> ToggleCameraAsync();

    Task<bool> LeaveAsync();
}
