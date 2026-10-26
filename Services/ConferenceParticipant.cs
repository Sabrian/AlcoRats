namespace AlcoRats.Services;

public sealed record ConferenceParticipant(
    string Id,
    string Name,
    string AvatarUrl,
    bool IsLocal);
