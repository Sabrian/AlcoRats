#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.OS;
using Android.Widget;
using AlcoRats.Hosting;
using AlcoRats.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AlcoRats.Android;

/// <summary>
/// Headset / play-pause routing: one on-screen toast per press (no adb).
/// With CallStyle+Telecom the button often arrives as Telecom disconnect/mute.
/// </summary>
internal static class HotkeyDiag
{
    public const string Tag = "AlcoRats.Hotkey";

    /// <summary>On-screen toast for sideload diagnosis. Turn off when done.</summary>
    public static bool ShowToast { get; set; } = true;

    private static long _lastToggleTicks;
    private static long _suppressMuteUntilTicks;

    /// <summary>
    /// Ignore Telecom mute callbacks for a short window (disconnect/rearm churn).
    /// </summary>
    public static void SuppressMuteEvents(int ms = 2000)
    {
        var until = System.Environment.TickCount64 + ms;
        Interlocked.Exchange(ref _suppressMuteUntilTicks, until);
    }

    public static bool MuteEventsSuppressed
        => System.Environment.TickCount64 < Interlocked.Read(ref _suppressMuteUntilTicks);

    public static void Note(string source)
        => global::Android.Util.Log.Info(Tag, source);

    /// <summary>Toggle mic once; show a single toast with the outcome.</summary>
    public static void ToggleMicFrom(string source, Context? context = null)
    {
        var now = System.Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastToggleTicks) < 600)
        {
            Note(source + " [debounced]");
            return;
        }

        Interlocked.Exchange(ref _lastToggleTicks, now);
        Note(source + " → ToggleMicrophone");

        _ = ToggleAndAnnounceAsync(source, context);
    }

    private static async Task ToggleAndAnnounceAsync(string source, Context? context)
    {
        string result;
        try
        {
            if (!AppHost.IsInitialized)
            {
                result = "нет AppHost";
            }
            else
            {
                var conference = AppHost.Services.GetService<IConferenceSession>();
                if (conference is null)
                {
                    result = "нет сессии";
                }
                else if (!conference.State.IsActive)
                {
                    result = "звонок не активен";
                }
                else
                {
                    var before = conference.State.AudioMuted;
                    await conference.ToggleMicrophoneAsync(debounce: false).ConfigureAwait(false);
                    // Poll briefly — bridge updates state asynchronously.
                    var muted = before;
                    for (var i = 0; i < 8; i++)
                    {
                        await Task.Delay(50).ConfigureAwait(false);
                        muted = conference.State.AudioMuted;
                        if (muted != before)
                            break;
                    }

                    if (muted == before)
                    {
                        result = "mic НЕ сменился (был " + (before ? "выкл" : "вкл") + ")";
                    }
                    else
                    {
                        result = muted ? "микрофон ВЫКЛ" : "микрофон ВКЛ";
                        // Explicit tone — AssistanceSonification was silent on BT SCO;
                        // service StateChanged may also fire (PlayMicStatusTone is cheap).
                        ConferenceMediaSessionService.PlayMicStatusTone(microphoneOn: !muted);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result = "ошибка " + ex.GetType().Name;
            Note(source + " EX: " + ex.Message);
        }

        var path = source.Contains("OnDisconnect", StringComparison.Ordinal) ? "Telecom hangup"
            : source.Contains("OnMute", StringComparison.Ordinal) ? "Telecom mute"
            : source.Contains("CallAudioState", StringComparison.Ordinal) ? "Telecom muted"
            : source.StartsWith("MediaSession", StringComparison.Ordinal) ? "MediaSession"
            : source.StartsWith("MainActivity", StringComparison.Ordinal) ? "Activity"
            : "хоткей";

        ShowOneToast(path + ": " + result, context);
    }

    private static void ShowOneToast(string text, Context? toastContext)
    {
        global::Android.Util.Log.Info(Tag, text);
        if (!ShowToast)
            return;

        try
        {
            var ctx = toastContext ?? global::Android.App.Application.Context;
            if (ctx is null)
                return;

            var handler = new Handler(Looper.MainLooper!);
            handler.Post(() =>
            {
                try { Toast.MakeText(ctx, text, ToastLength.Long)?.Show(); }
                catch { /* ignore */ }
            });
        }
        catch
        {
            // ignore
        }
    }
}
