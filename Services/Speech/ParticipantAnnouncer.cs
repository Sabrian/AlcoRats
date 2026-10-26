using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AlcoRats.Settings;
using Microsoft.Extensions.Logging;

namespace AlcoRats.Services.Speech;

/// <summary>
/// Announces remote participant join/leave with Piper (or fallback TTS).
/// Leaves are confirmed across consecutive snapshots to avoid false “left”
/// when the WebView briefly publishes a partial/empty participant list.
/// </summary>
public sealed class ParticipantAnnouncer(
    IConferenceSession conference,
    SpeechQueueService speech,
    AppSettings settings,
    ILogger<ParticipantAnnouncer> logger)
{
    /// <summary>Misses required before speaking leave (normal shrink).</summary>
    private const int LeaveConfirmHits = 2;

    /// <summary>Misses when the remote list goes fully empty while still in-call (Redux glitch).</summary>
    private const int LeaveConfirmHitsOnEmptyList = 3;
    private readonly Lock _gate = new();
    private Dictionary<string, string> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pendingLeave = new(StringComparer.Ordinal);
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
            _pendingLeave.Clear();
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
                if (_known.Count > 0 || _baselineReady)
                    logger.LogDebug("Conference inactive — clearing participant baseline");
                _known.Clear();
                _pendingLeave.Clear();
                _baselineReady = false;
                return;
            }

            var current = state.Participants
                .Where(p => !p.IsLocal && !string.IsNullOrWhiteSpace(p.Id))
                .GroupBy(p => p.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Name ?? "Участник", StringComparer.Ordinal);

            if (!_baselineReady)
            {
                _known = new Dictionary<string, string>(current, StringComparer.Ordinal);
                _pendingLeave.Clear();
                _baselineReady = true;
                logger.LogInformation("Participant baseline ready ({Count} remote)", current.Count);
                return;
            }

            // Returned after a flicker — cancel pending leave, do not re-announce join.
            foreach (var id in _pendingLeave.Keys.ToList())
            {
                if (current.ContainsKey(id))
                {
                    logger.LogDebug("Participant leave cancelled (returned): {Id}", id);
                    _pendingLeave.Remove(id);
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
                AnnounceJoin(name);
            }

            var vanished = _known.Keys.Where(id => !current.ContainsKey(id)).ToList();
            var suspiciousEmpty = current.Count == 0 && _known.Count > 0;
            var needHits = suspiciousEmpty ? LeaveConfirmHitsOnEmptyList : LeaveConfirmHits;

            if (suspiciousEmpty && vanished.Count > 0)
            {
                logger.LogDebug(
                    "Ignoring empty remote list for leave ({Pending} known) — need {Hits} confirms",
                    _known.Count,
                    needHits);
            }

            foreach (var id in vanished)
            {
                _pendingLeave.TryGetValue(id, out var hits);
                hits++;
                if (hits < needHits)
                {
                    _pendingLeave[id] = hits;
                    logger.LogDebug(
                        "Participant leave pending {Hits}/{Need}: {Id} ({Name})",
                        hits,
                        needHits,
                        id,
                        _known[id]);
                    continue;
                }

                var name = _known[id];
                logger.LogInformation("Participant left: {Id} ({Name})", id, name);
                _known.Remove(id);
                _pendingLeave.Remove(id);
                AnnounceLeave(name);
            }
        }
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
