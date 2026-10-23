using System.Collections.Generic;

namespace AlcoRats.Services;

public sealed record ConferenceSessionState(
    bool IsActive,
    bool AudioMuted,
    bool VideoMuted,
    IReadOnlyList<ConferenceParticipant> Participants)
{
    public static ConferenceSessionState Inactive { get; } =
        new(false, false, false, []);
}
