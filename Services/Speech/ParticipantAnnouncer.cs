using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AlcoRats.Settings;
using Microsoft.Extensions.Logging;

namespace AlcoRats.Services.Speech;

/// <summary>
/// Announces remote participant join/leave with Piper (or fallback TTS).
/// Leaves require sustained absence (wall-clock), not just two rapid Redux
/// snapshots — store.subscribe can publish a partial roster several times
/// while someone is still joining.
/// </summary>
public sealed class ParticipantAnnouncer(
    IConferenceSession conference,
    SpeechQueueService speech,
    AppSettings settings,
    ILogger<ParticipantAnnouncer> logger)
{
    /// <summary>How long a remote must be missing before leave TTS.</summary>
    private static readonly TimeSpan LeaveConfirm = TimeSpan.FromSeconds(3);

    /// <summary>
    /// If they were only visible this briefly, treat vanish as roster flicker —
    /// drop silently (no «выкатился»).
    /// </summary>
    private static readonly TimeSpan MinPresenceForLeaveAnnounce = TimeSpan.FromSeconds(2.5);

    /// <summary>Extra leave wait right after we first saw them (join is still settling).</summary>
    private static readonly TimeSpan RecentJoinLeaveConfirm = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private Dictionary<string, string> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _seenSinceUtc = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _missingSinceUtc = new(StringComparer.Ordinal);
    private bool _baselineReady;
    private bool _started;

    public void Start()
    {
        if (_started)
            return;

        _started = true;
        conference.StateChanged += OnStateChanged;
        OnStateChanged(conference.State);
        logger.LogInformation("Participant announcer subscribed to conference state");
    }

    public void Stop()
    {
        if (!_started)
            return;

        _started = false;
        conference.StateChanged -= OnStateChanged;
        lock (_gate)
        {
            _known.Clear();
            _seenSinceUtc.Clear();
            _missingSinceUtc.Clear();
            _baselineReady = false;
        }

        logger.LogInformation("Participant announcer stopped");
    }

    private void OnStateChanged(ConferenceSessionState state)
    {
        lock (_gate)
        {
            if (!state.IsActive)
            {
                // Local user left / home screen — never announce others as "left".
                if (_known.Count > 0 || _baselineReady)
                    logger.LogDebug("Conference inactive — clearing participant baseline (no leave TTS)");
                _known.Clear();
                _seenSinceUtc.Clear();
                _missingSinceUtc.Clear();
                _baselineReady = false;
                return;
            }

            var now = DateTime.UtcNow;
            var current = state.Participants
                .Where(p => !p.IsLocal && !string.IsNullOrWhiteSpace(p.Id))
                .GroupBy(p => p.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Name ?? "Участник", StringComparer.Ordinal);

            if (!_baselineReady)
            {
                _known = new Dictionary<string, string>(current, StringComparer.Ordinal);
                _seenSinceUtc.Clear();
                foreach (var id in current.Keys)
                    _seenSinceUtc[id] = now;
                _missingSinceUtc.Clear();
                _baselineReady = true;
                logger.LogInformation("Participant baseline ready ({Count} remote)", current.Count);
                return;
            }

            // Returned after a flicker — cancel pending leave, do not re-announce join.
            foreach (var id in _missingSinceUtc.Keys.ToList())
            {
                if (current.ContainsKey(id))
                {
                    logger.LogDebug("Participant leave cancelled (returned): {Id}", id);
                    _missingSinceUtc.Remove(id);
                }
            }

            foreach (var (id, name) in current)
            {
                if (_known.ContainsKey(id))
                {
                    _known[id] = name;
                    continue;
                }

                logger.LogInformation("Participant joined: {Id} ({Name})", id, name);
                _known[id] = name;
                _seenSinceUtc[id] = now;
                _missingSinceUtc.Remove(id);
                AnnounceJoin(name);
            }

            var vanished = _known.Keys.Where(id => !current.ContainsKey(id)).ToList();
            if (vanished.Count == 0)
                return;

            // Whole roster (or all remotes) vanished while still "active" → local hangup /
            // teardown, not everyone leaving. Clear silently, never TTS leave.
            var massDisconnect = current.Count == 0
                                 || vanished.Count >= _known.Count
                                 || (vanished.Count >= 2 && current.Count == 0);
            if (massDisconnect)
            {
                logger.LogDebug(
                    "Suppressing leave TTS — mass disconnect ({Vanished}/{Known} remotes gone)",
                    vanished.Count,
                    _known.Count);
                foreach (var id in vanished)
                    Forget(id);

                return;
            }

            foreach (var id in vanished)
            {
                if (!_missingSinceUtc.TryGetValue(id, out var missingSince))
                {
                    _missingSinceUtc[id] = now;
                    logger.LogDebug(
                        "Participant leave pending (first miss): {Id} ({Name})",
                        id,
                        _known[id]);
                    continue;
                }

                var seenSince = _seenSinceUtc.TryGetValue(id, out var s) ? s : missingSince;
                var presentFor = missingSince - seenSince;
                var requiredAbsent = presentFor < MinPresenceForLeaveAnnounce
                    ? RecentJoinLeaveConfirm
                    : LeaveConfirm;

                var absentFor = now - missingSince;
                if (absentFor < requiredAbsent)
                {
                    logger.LogDebug(
                        "Participant leave pending {Absent:0.0}s/{Need:0.0}s: {Id} ({Name})",
                        absentFor.TotalSeconds,
                        requiredAbsent.TotalSeconds,
                        id,
                        _known[id]);
                    continue;
                }

                // Still too new overall — ghost/join flicker, drop without TTS.
                if (presentFor < MinPresenceForLeaveAnnounce)
                {
                    logger.LogDebug(
                        "Suppressing leave TTS — brief presence {Present:0.0}s: {Id} ({Name})",
                        presentFor.TotalSeconds,
                        id,
                        _known[id]);
                    Forget(id);
                    continue;
                }

                var name = _known[id];
                logger.LogInformation(
                    "Participant left (absent {Absent:0.0}s): {Id} ({Name})",
                    absentFor.TotalSeconds,
                    id,
                    name);
                Forget(id);
                AnnounceLeave(name);
            }
        }
    }

    private void Forget(string id)
    {
        _known.Remove(id);
        _seenSinceUtc.Remove(id);
        _missingSinceUtc.Remove(id);
    }

    private void AnnounceJoin(string name)
        => Announce(settings.UserEnter, name);

    private void AnnounceLeave(string name)
        => Announce(settings.UserLeave, name);

    private void Announce(string template, string name)
    {
        var shortName = NameSyllableHelper.TruncateToSyllables(name, 10);
        var text = string.IsNullOrWhiteSpace(template)
            ? shortName
            : template.Replace("{User}", shortName, StringComparison.Ordinal);
        speech.Speak(text);
    }
}
