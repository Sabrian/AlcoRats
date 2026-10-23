using System.Collections.Generic;

namespace AlcoRats.Services;

public sealed record ConferenceSessionState(
    bool IsActive,
    bool AudioMuted,
    bool VideoMuted,
    IReadOnlyList<ConferenceParticipant> Participants,
    int UnreadChatCount = 0)
{
    public static ConferenceSessionState Inactive { get; } =
        new(false, false, false, [], 0);
}
