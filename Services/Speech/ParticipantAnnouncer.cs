using System;
using System.Collections.Generic;
using System.Linq;

namespace AlcoRats.Services.Speech;

/// <summary>
/// Announces remote participant join/leave with Piper (or fallback TTS).
/// </summary>
public static class ParticipantAnnouncer
{
    private static readonly object Gate = new();
    private static Dictionary<string, string> _known = new(StringComparer.Ordinal);
    private static bool _baselineReady;
    private static bool _started;

    public static void Start()
    {
        if (_started)
            return;
        _started = true;
        ConferenceSession.StateChanged += OnStateChanged;
        OnStateChanged(ConferenceSession.State);
    }

    public static void Stop()
    {
        if (!_started)
            return;
        _started = false;
        ConferenceSession.StateChanged -= OnStateChanged;
        lock (Gate)
        {
            _known.Clear();
            _baselineReady = false;
        }
    }

    private static void OnStateChanged(ConferenceSessionState state)
    {
        lock (Gate)
        {
            if (!state.IsActive)
            {
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
                return;
            }

            foreach (var (id, name) in current)
            {
                if (_known.ContainsKey(id))
                    continue;
                AnnounceJoin(name);
            }

            foreach (var (id, name) in _known)
            {
                if (current.ContainsKey(id))
                    continue;
                AnnounceLeave(name);
            }

            _known = current;
        }
    }

    private static void AnnounceJoin(string name)
        => TtsService.Speak("Входит " + NameSyllableHelper.TruncateToSyllables(name, 10));

    private static void AnnounceLeave(string name)
        => TtsService.Speak("Выходит " + NameSyllableHelper.TruncateToSyllables(name, 10));
}
