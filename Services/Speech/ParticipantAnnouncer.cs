using System;
using System.Collections.Generic;
using System.Linq;
using AlcoRats.Settings;
using Microsoft.Extensions.Logging;

namespace AlcoRats.Services.Speech;

/// <summary>
/// Announces remote participant join/leave with Piper (or fallback TTS).
/// </summary>
public sealed class ParticipantAnnouncer
{
    private readonly IConferenceSession _conference;
    private readonly SpeechQueueService _speech;
    private readonly AppSettings _settings;
    private readonly ILogger<ParticipantAnnouncer> _logger;
    private readonly object _gate = new();
    private Dictionary<string, string> _known = new(StringComparer.Ordinal);
    private bool _baselineReady;
    private bool _started;

    public ParticipantAnnouncer(
        IConferenceSession conference,
        SpeechQueueService speech,
        AppSettings settings,
        ILogger<ParticipantAnnouncer> logger)
    {
        _conference = conference;
        _speech = speech;
        _settings = settings;
        _logger = logger;
    }

    public void Start()
    {
        if (_started)
            return;

        _started = true;
        _conference.StateChanged += OnStateChanged;
        OnStateChanged(_conference.State);
        _logger.LogInformation("Participant announcer subscribed to conference state");
    }

    public void Stop()
    {
        if (!_started)
            return;

        _started = false;
        _conference.StateChanged -= OnStateChanged;
        lock (_gate)
        {
            _known.Clear();
            _baselineReady = false;
        }

        _logger.LogInformation("Participant announcer stopped");
    }

    private void OnStateChanged(ConferenceSessionState state)
    {
        lock (_gate)
        {
            if (!state.IsActive)
            {
                if (_known.Count > 0 || _baselineReady)
                    _logger.LogDebug("Conference inactive — clearing participant baseline");
                _known.Clear();
                _baselineReady = false;
                return;
            }

            var current = state.Participants
                .Where(p => !p.IsLocal && !string.IsNullOrWhiteSpace(p.Id))
                .GroupBy(p => p.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Name ?? "Участник", StringComparer.Ordinal);

            if (!_baselineReady)
            {
                _known = current;
                _baselineReady = true;
                _logger.LogInformation("Participant baseline ready ({Count} remote)", current.Count);
                return;
            }

            foreach (var (id, name) in current)
            {
                if (_known.ContainsKey(id))
                    continue;
                _logger.LogInformation("Participant joined: {Id} ({Name})", id, name);
                AnnounceJoin(name);
            }

            foreach (var (id, name) in _known)
            {
                if (current.ContainsKey(id))
                    continue;
                _logger.LogInformation("Participant left: {Id} ({Name})", id, name);
                AnnounceLeave(name);
            }

            _known = current;
        }
    }

    private void AnnounceJoin(string name)
        => Announce(_settings.UserEnter, name);

    private void AnnounceLeave(string name)
        => Announce(_settings.UserLeave, name);

    private void Announce(string template, string name)
    {
        var shortName = NameSyllableHelper.TruncateToSyllables(name, 10);
        var text = string.IsNullOrWhiteSpace(template)
            ? shortName
            : template.Replace("{User}", shortName, StringComparison.Ordinal);
        _speech.Speak(text);
    }
}
