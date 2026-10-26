using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AlcoRats.Hosting;
using AlcoRats.Services;
using Android.App;
using Microsoft.Extensions.DependencyInjection;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.Media;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Core.App;
using AndroidX.Media.Session;
using MediaMetadataCompat = Android.Support.V4.Media.MediaMetadataCompat;
using MediaSessionCompat = Android.Support.V4.Media.Session.MediaSessionCompat;
using PlaybackStateCompat = Android.Support.V4.Media.Session.PlaybackStateCompat;

namespace AlcoRats.Android;

[Service(
    Name = "com.alcorats.app.ConferenceMediaSessionService",
    Exported = true,
    ForegroundServiceType = ForegroundService.TypeMediaPlayback | ForegroundService.TypeMicrophone)]
[IntentFilter([Intent.ActionMediaButton, ActionToggleMic, ActionToggleCam, ActionOpenChat, ActionLeave, ActionRefresh])]
public sealed class ConferenceMediaSessionService : Service
{
    public const string ChannelId = "alcorats_conference_hotkeys";
    public const int NotificationId = 4201;

    public const string ActionToggleMic = "com.alcorats.app.ACTION_TOGGLE_MIC";
    public const string ActionToggleCam = "com.alcorats.app.ACTION_TOGGLE_CAM";
    public const string ActionOpenChat = "com.alcorats.app.ACTION_OPEN_CHAT";
    public const string ActionLeave = "com.alcorats.app.ACTION_LEAVE";
    public const string ActionRefresh = "com.alcorats.app.ACTION_REFRESH";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly ConcurrentDictionary<string, Bitmap?> AvatarCache = new();

    private const int PendingTimeoutMs = 3000;

    private MediaSessionCompat? _session;
    private MediaSessionCallback? _callback;
    private IConferenceSession? _conference;
    private ConferenceSessionState _lastState = ConferenceSessionState.Inactive;
    private Handler? _mainHandler;
    private bool _micPending;
    private bool _camPending;
    private bool _audioMutedWhenMicPending;
    private bool _videoMutedWhenCamPending;
    /// <summary>Unread already present while history is still catching up after join — no tones yet.</summary>
    private int _unreadChatJoinFloor;
    private bool _chatToneBaselineReady;
    private static readonly object ToneLock = new();
    private Java.Lang.IRunnable? _clearMicPendingRunnable;
    private Java.Lang.IRunnable? _clearCamPendingRunnable;
    private const int CollapsedParticipantSlots = 4;
    private const int ExpandedParticipantSlots = 8;

    private readonly int[] _participantContainerIds =
    [
        Resource.Id.participant1,
        Resource.Id.participant2,
        Resource.Id.participant3,
        Resource.Id.participant4,
        Resource.Id.participant5,
        Resource.Id.participant6,
        Resource.Id.participant7,
        Resource.Id.participant8
    ];

    private readonly int[] _avatarViewIds =
    [
        Resource.Id.avatar1,
        Resource.Id.avatar2,
        Resource.Id.avatar3,
        Resource.Id.avatar4,
        Resource.Id.avatar5,
        Resource.Id.avatar6,
        Resource.Id.avatar7,
        Resource.Id.avatar8
    ];

    private readonly int[] _nameViewIds =
    [
        Resource.Id.name1,
        Resource.Id.name2,
        Resource.Id.name3,
        Resource.Id.name4,
        Resource.Id.name5,
        Resource.Id.name6,
        Resource.Id.name7,
        Resource.Id.name8
    ];


    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        EnsureNotificationChannel();
        CreateMediaSession();
        _conference = ResolveConferenceSession();
        if (_conference is not null)
        {
            _lastState = _conference.State;
            _conference.StateChanged += OnConferenceStateChanged;
        }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        MediaButtonReceiver.HandleIntent(_session, intent);
        _conference ??= ResolveConferenceSession();

        switch (intent?.Action)
        {
            case ActionToggleMic:
                TryToggleMicrophone();
                break;
            case ActionToggleCam:
                TryToggleCamera();
                break;
            case ActionOpenChat:
                TryOpenChat();
                break;
            case ActionLeave:
                ClearPending(mic: true, cam: true);
                _ = _conference?.LeaveAsync();
                break;
        }

        var state = _conference?.State ?? _lastState;
        _lastState = state;

        if (!state.IsActive)
        {
            // StartForegroundService requires a brief StartForeground — then tear down.
            EnsureForegroundStartedForCompliance();
            DismissShade();
            StopSelf(startId);
            return StartCommandResult.NotSticky;
        }

        PublishForeground(state);
        if (_session is not null)
            _session.Active = true;
        UpdatePlaybackState(PlaybackStateCompat.StatePlaying);
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        if (_conference is not null)
            _conference.StateChanged -= OnConferenceStateChanged;
        ClearPending(mic: true, cam: true);

        if (_session is not null)
        {
            _session.Active = false;
            _session.SetCallback(null);
            _session.Release();
            _session = null;
        }

        _callback = null;
        base.OnDestroy();
    }

    private void OnConferenceStateChanged(ConferenceSessionState state)
    {
        var micChanged = state.IsActive
            && _lastState.IsActive
            && state.AudioMuted != _lastState.AudioMuted;

        UpdateChatJoinBaseline(state);
        var newChatMessage = ShouldPlayIncomingMessageTone(state);

        ResolvePendingFromState(state);
        _lastState = state;
        _ = PrefetchAvatarsAndRefreshAsync(state);

        if (micChanged)
            PlayMicStatusTone(microphoneOn: !state.AudioMuted);

        if (newChatMessage)
            PlayIncomingMessageTone();
    }

    /// <summary>
    /// While joining, unread often climbs 0→1→2… as history loads. Raise a floor until the
    /// count stops growing — no timer, only “still catching up” vs “stable”.
    /// </summary>
    private void UpdateChatJoinBaseline(ConferenceSessionState state)
    {
        if (!state.IsActive)
        {
            _unreadChatJoinFloor = 0;
            _chatToneBaselineReady = false;
            return;
        }

        if (!_lastState.IsActive)
        {
            _unreadChatJoinFloor = state.UnreadChatCount;
            _chatToneBaselineReady = false;
            return;
        }

        if (_chatToneBaselineReady)
        {
            if (state.UnreadChatCount < _lastState.UnreadChatCount)
                _unreadChatJoinFloor = 0;
            return;
        }

        if (state.UnreadChatCount > _unreadChatJoinFloor)
            _unreadChatJoinFloor = state.UnreadChatCount;
        else
            _chatToneBaselineReady = true;
    }

    private bool ShouldPlayIncomingMessageTone(ConferenceSessionState state)
    {
        if (!state.IsActive || !_lastState.IsActive || !_chatToneBaselineReady)
            return false;

        return state.UnreadChatCount > _lastState.UnreadChatCount;
    }

    private static IConferenceSession? ResolveConferenceSession()
        => AppHost.IsInitialized
            ? AppHost.Services.GetRequiredService<IConferenceSession>()
            : null;

    /// <summary>Shared path for notification button and media hotkeys.</summary>
    private bool TryToggleMicrophone()
    {
        if (_micPending || _conference is null)
            return false;

        BeginMicPending();
        _ = _conference.ToggleMicrophoneAsync();
        return true;
    }

    private bool TryToggleCamera()
    {
        if (_camPending || _conference is null)
            return false;

        BeginCamPending();
        _ = _conference.ToggleCameraAsync();
        return true;
    }

    private bool TryOpenChat()
    {
        if (_conference is null || !_lastState.IsActive)
            return false;

        BringAppToForeground();
        _ = _conference.OpenChatAsync();
        return true;
    }

    private void BringAppToForeground()
    {
        var launchIntent = new Intent(this, typeof(MainActivity));
        launchIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop | ActivityFlags.NewTask);
        StartActivity(launchIntent);
    }

    private Handler MainHandler => _mainHandler ??= new Handler(Looper.MainLooper!);

    private void BeginMicPending()
    {
        _micPending = true;
        _audioMutedWhenMicPending = _lastState.AudioMuted;
        SchedulePendingTimeout(ref _clearMicPendingRunnable, () =>
        {
            if (!_micPending)
                return;
            _micPending = false;
            PublishForeground(_lastState);
        });
    }

    private void BeginCamPending()
    {
        _camPending = true;
        _videoMutedWhenCamPending = _lastState.VideoMuted;
        SchedulePendingTimeout(ref _clearCamPendingRunnable, () =>
        {
            if (!_camPending)
                return;
            _camPending = false;
            PublishForeground(_lastState);
        });
    }

    private void SchedulePendingTimeout(ref Java.Lang.IRunnable? runnable, Action action)
    {
        if (runnable is not null)
            MainHandler.RemoveCallbacks(runnable);

        runnable = new Java.Lang.Runnable(action);
        MainHandler.PostDelayed(runnable, PendingTimeoutMs);
    }

    private void ResolvePendingFromState(ConferenceSessionState state)
    {
        if (_micPending && state.AudioMuted != _audioMutedWhenMicPending)
        {
            _micPending = false;
            if (_clearMicPendingRunnable is not null)
                MainHandler.RemoveCallbacks(_clearMicPendingRunnable);
        }

        if (_camPending && state.VideoMuted != _videoMutedWhenCamPending)
        {
            _camPending = false;
            if (_clearCamPendingRunnable is not null)
                MainHandler.RemoveCallbacks(_clearCamPendingRunnable);
        }

        if (!state.IsActive)
            ClearPending(mic: true, cam: true);
    }

    private void ClearPending(bool mic, bool cam)
    {
        if (mic)
        {
            _micPending = false;
            if (_clearMicPendingRunnable is not null)
                MainHandler.RemoveCallbacks(_clearMicPendingRunnable);
        }

        if (cam)
        {
            _camPending = false;
            if (_clearCamPendingRunnable is not null)
                MainHandler.RemoveCallbacks(_clearCamPendingRunnable);
        }
    }

    private async Task PrefetchAvatarsAndRefreshAsync(ConferenceSessionState state)
    {
        if (state.IsActive)
        {
            foreach (var participant in state.Participants.Take(ExpandedParticipantSlots))
            {
                if (string.IsNullOrWhiteSpace(participant.AvatarUrl))
                    continue;
                if (AvatarCache.ContainsKey(participant.AvatarUrl))
                    continue;

                try
                {
                    await using var stream = await Http.GetStreamAsync(participant.AvatarUrl).ConfigureAwait(false);
                    var bitmap = await BitmapFactory.DecodeStreamAsync(stream).ConfigureAwait(false);
                    if (bitmap is not null)
                        AvatarCache[participant.AvatarUrl] = CreateCircularBitmap(bitmap);
                }
                catch
                {
                    AvatarCache.TryAdd(participant.AvatarUrl, null);
                }
            }
        }

        var handler = new Handler(Looper.MainLooper!);
        handler.Post(() => PublishForeground(state));
    }

    private void PublishForeground(ConferenceSessionState state)
    {
        if (!state.IsActive)
        {
            DismissShade();
            return;
        }

        var notification = BuildNotification(state);
        StartForegroundWith(notification);
        NotificationManagerCompat.From(this)?.Notify(NotificationId, notification);
    }

    /// <summary>Satisfy StartForegroundService contract when we are about to stop without a call.</summary>
    private void EnsureForegroundStartedForCompliance()
    {
        var icon = ApplicationInfo?.Icon ?? global::Android.Resource.Drawable.IcMediaPlay;
        var placeholder = new NotificationCompat.Builder(this, ChannelId)!
            .SetSmallIcon(icon)!
            .SetContentTitle("AlcoRats")!
            .SetContentText("")!
            .SetOngoing(false)!
            .SetOnlyAlertOnce(true)!
            .Build()!;
        StartForegroundWith(placeholder);
    }

    private void StartForegroundWith(Notification notification)
    {
        if (global::System.OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            StartForeground(
                NotificationId,
                notification,
                ForegroundService.TypeMediaPlayback | ForegroundService.TypeMicrophone);
        }
        else if (global::System.OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }
    }

    private void DismissShade()
    {
        try
        {
            if (global::System.OperatingSystem.IsAndroidVersionAtLeast(24))
                StopForeground(StopForegroundFlags.Remove);
            else
#pragma warning disable CS0618
                StopForeground(removeNotification: true);
#pragma warning restore CS0618
        }
        catch
        {
            // Service may not have been in foreground yet.
        }

        NotificationManagerCompat.From(this)?.Cancel(NotificationId);

        if (_session is not null)
            _session.Active = false;
    }

    private void CreateMediaSession()
    {
        var mediaButtonIntent = new Intent(Intent.ActionMediaButton);
        mediaButtonIntent.SetClass(this, Java.Lang.Class.FromType(typeof(MediaButtonReceiver)));
        var mediaButtonPendingIntent = PendingIntent.GetBroadcast(
            this,
            0,
            mediaButtonIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;

        _session = new MediaSessionCompat(this, "AlcoRatsConference");
        _callback = new MediaSessionCallback(this);
        _session.SetCallback(_callback);
        _session.SetMediaButtonReceiver(mediaButtonPendingIntent);
#pragma warning disable CS0618
        _session.SetFlags(
            MediaSessionCompat.FlagHandlesMediaButtons |
            MediaSessionCompat.FlagHandlesTransportControls);
#pragma warning restore CS0618

        var metadata = new MediaMetadataCompat.Builder()
            .PutString(MediaMetadataCompat.MetadataKeyTitle, "AlcoRats")!
            .PutString(MediaMetadataCompat.MetadataKeyArtist, "Конференция")!
            .Build();
        _session.SetMetadata(metadata);
        UpdatePlaybackState(PlaybackStateCompat.StatePlaying);
        _session.Active = true;
    }

    private void UpdatePlaybackState(int state)
    {
        var playbackState = new PlaybackStateCompat.Builder()!
            .SetActions(
                PlaybackStateCompat.ActionPlay |
                PlaybackStateCompat.ActionPause |
                PlaybackStateCompat.ActionPlayPause |
                PlaybackStateCompat.ActionStop)!
            .SetState(state, PlaybackStateCompat.PlaybackPositionUnknown, 1f)!
            .Build();
        _session?.SetPlaybackState(playbackState);
    }

    private void EnsureNotificationChannel()
    {
        if (!global::System.OperatingSystem.IsAndroidVersionAtLeast(26))
            return;

        var manager = (NotificationManager?)GetSystemService(NotificationService);
        var existing = manager?.GetNotificationChannel(ChannelId);
        if (existing is not null)
            return;

        var channel = new NotificationChannel(
            ChannelId,
            "Конференция",
            NotificationImportance.Default)
        {
            Description = "Панель конференции: участники, микрофон, камера, чат, выход. Play/Pause — mute."
        };
        manager?.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification(ConferenceSessionState state)
    {
        var launchIntent = new Intent(this, typeof(MainActivity));
        launchIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var contentIntent = PendingIntent.GetActivity(
            this,
            0,
            launchIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var icon = ApplicationInfo?.Icon ?? global::Android.Resource.Drawable.IcMediaPlay;
        var builder = new NotificationCompat.Builder(this, ChannelId)!
            .SetSmallIcon(icon)!
            .SetContentIntent(contentIntent)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetCategory(NotificationCompat.CategoryCall)!
            .SetVisibility(NotificationCompat.VisibilityPublic)!
            .SetPriority((int)NotificationPriority.Default)!;

        var names = string.Join(", ", state.Participants.Take(CollapsedParticipantSlots).Select(p => p.Name));
        if (string.IsNullOrWhiteSpace(names))
            names = "Вы в конференции";

        var collapsed = new RemoteViews(PackageName, Resource.Layout.notification_conference);
        BindConferenceRemoteViews(collapsed, state, expanded: false);

        // ≤4 people: same compact strip when expanded (no empty 4×2 grid).
        // >4: big layout with two rows (up to 8).
        RemoteViews big;
        if (state.Participants.Count <= CollapsedParticipantSlots)
        {
            big = new RemoteViews(PackageName, Resource.Layout.notification_conference);
            BindConferenceRemoteViews(big, state, expanded: false);
        }
        else
        {
            big = new RemoteViews(PackageName, Resource.Layout.notification_conference_expanded);
            BindConferenceRemoteViews(big, state, expanded: true);
        }

        return builder
            .SetContentTitle("Конференция")!
            .SetContentText(names)!
            .SetCustomContentView(collapsed)!
            .SetCustomBigContentView(big)!
            .SetStyle(new NotificationCompat.DecoratedCustomViewStyle())!
            .Build()!;
    }

    private void BindConferenceRemoteViews(RemoteViews views, ConferenceSessionState state, bool expanded)
    {
        var slotCount = expanded ? ExpandedParticipantSlots : CollapsedParticipantSlots;
        var participants = state.Participants.Take(slotCount).ToList();

        for (var i = 0; i < slotCount; i++)
        {
            var containerId = _participantContainerIds[i];
            var avatarId = _avatarViewIds[i];
            var nameId = _nameViewIds[i];

            if (i >= participants.Count)
            {
                views.SetViewVisibility(containerId, ViewStates.Gone);
                continue;
            }

            var participant = participants[i];
            views.SetViewVisibility(containerId, ViewStates.Visible);
            views.SetTextViewText(nameId, TruncateName(participant.Name));
            views.SetTextColor(nameId, ResolveNameTextColor());

            if (!string.IsNullOrWhiteSpace(participant.AvatarUrl)
                && AvatarCache.TryGetValue(participant.AvatarUrl, out var bitmap)
                && bitmap is not null)
            {
                views.SetImageViewBitmap(avatarId, bitmap);
            }
            else
            {
                views.SetImageViewResource(avatarId, Resource.Drawable.ic_avatar_placeholder);
            }
        }

        // Hide unused expanded-only slots when binding collapsed layout (ids absent there).
        if (!expanded)
        {
            var extra = Math.Max(0, state.Participants.Count - CollapsedParticipantSlots);
            if (extra > 0)
            {
                views.SetViewVisibility(Resource.Id.participant_count, ViewStates.Visible);
                views.SetTextViewText(Resource.Id.participant_count, extra > 9 ? "9+" : $"+{extra}");
            }
            else
            {
                views.SetViewVisibility(Resource.Id.participant_count, ViewStates.Gone);
                views.SetTextViewText(Resource.Id.participant_count, string.Empty);
            }
        }
        else
        {
            // Second row: hide whole row when ≤4 people so expanded stays compact.
            var row2Visible = state.Participants.Count > CollapsedParticipantSlots;
            views.SetViewVisibility(
                Resource.Id.avatar_row2,
                row2Visible ? ViewStates.Visible : ViewStates.Gone);
        }

        BindConferenceControls(views, state);
    }

    private void BindConferenceControls(RemoteViews views, ConferenceSessionState state)
    {
        views.SetImageViewResource(
            Resource.Id.btn_mic,
            _micPending
                ? Resource.Drawable.ic_mic_pending
                : state.AudioMuted
                    ? Resource.Drawable.ic_mic_off
                    : Resource.Drawable.ic_mic);
        views.SetImageViewResource(
            Resource.Id.btn_cam,
            _camPending
                ? Resource.Drawable.ic_cam_pending
                : state.VideoMuted
                    ? Resource.Drawable.ic_cam_off
                    : Resource.Drawable.ic_cam);
        views.SetImageViewResource(Resource.Id.btn_chat, Resource.Drawable.ic_chat);

        if (state.UnreadChatCount > 0)
        {
            views.SetViewVisibility(Resource.Id.chat_badge, ViewStates.Visible);
            views.SetTextViewText(
                Resource.Id.chat_badge,
                state.UnreadChatCount > 9 ? "9+" : state.UnreadChatCount.ToString());
        }
        else
        {
            views.SetViewVisibility(Resource.Id.chat_badge, ViewStates.Gone);
            views.SetTextViewText(Resource.Id.chat_badge, string.Empty);
        }

        views.SetOnClickPendingIntent(Resource.Id.btn_mic, CreateActionPendingIntent(ActionToggleMic, 11));
        views.SetOnClickPendingIntent(Resource.Id.btn_cam, CreateActionPendingIntent(ActionToggleCam, 12));
        views.SetOnClickPendingIntent(Resource.Id.btn_chat, CreateActionPendingIntent(ActionOpenChat, 14));
        views.SetOnClickPendingIntent(Resource.Id.btn_leave, CreateActionPendingIntent(ActionLeave, 13));
    }

    private static string TruncateName(string? name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "Участник" : name.Trim();
        // Narrow 40dp cells in the shade — keep labels short.
        return value.Length <= 7 ? value : value[..7] + "…";
    }

    private Color ResolveNameTextColor()
    {
        var night = (Resources!.Configuration!.UiMode & UiMode.NightMask) == UiMode.NightYes;
        return new Color(Color.ParseColor(night ? "#E8E8ED" : "#1C1B1F"));
    }

    private static void PlayMicStatusTone(bool microphoneOn)
    {
        _ = Task.Run(() =>
        {
            try
            {
                // Ascending when enabling mic, descending when muting.
                var firstHz = microphoneOn ? 740.0 : 987.0;
                var secondHz = microphoneOn ? 987.0 : 740.0;
                PlayTone(firstHz, 70);
                Thread.Sleep(40);
                PlayTone(secondHz, 95);
            }
            catch
            {
                // Sonification is best-effort.
            }
        });
    }

    private static void PlayIncomingMessageTone()
    {
        _ = Task.Run(() =>
        {
            try
            {
                // ~250 ms major arpeggio (E4–G#4–B4–E5) with soft harmonics.
                PlayPcm(BuildIncomingMessageMelody(durationMs: 250));
            }
            catch
            {
                // Sonification is best-effort.
            }
        });
    }

    private static short[] BuildIncomingMessageMelody(int durationMs)
    {
        const int sampleRate = 22050;
        var sampleCount = Math.Max(1, sampleRate * durationMs / 1000);
        var samples = new short[sampleCount];

        // E major arpeggio — consonant and “notification-like” (loud gains).
        (double Hz, double Start, double Length, double Gain)[] voices =
        [
            (329.63, 0.00, 0.55, 0.55), // E4
            (415.30, 0.12, 0.55, 0.50), // G#4
            (493.88, 0.24, 0.55, 0.45), // B4
            (659.25, 0.38, 0.62, 0.60), // E5
            (830.61, 0.52, 0.48, 0.28), // G#5 sparkle
        ];

        for (var i = 0; i < sampleCount; i++)
        {
            var t = (double)i / sampleCount;
            var mix = 0.0;

            foreach (var (hz, start, length, gain) in voices)
            {
                if (t < start || t > start + length)
                    continue;

                var local = (t - start) / length;
                // Bell-ish envelope: fast attack, smooth decay.
                var envelope = Math.Sin(Math.PI * Math.Clamp(local, 0, 1));
                envelope *= envelope;
                var phase = 2 * Math.PI * hz * i / sampleRate;
                // Fundamental + quiet octave harmonic for a more melodic timbre.
                mix += (Math.Sin(phase) + 0.28 * Math.Sin(2 * phase)) * gain * envelope;
            }

            // Soft overall fade so the whole phrase fits a quarter second cleanly.
            var master = 1.0;
            var attack = 0.04;
            var release = 0.22;
            if (t < attack)
                master = t / attack;
            else if (t > 1.0 - release)
                master = (1.0 - t) / release;

            var sample = mix * master * short.MaxValue;
            samples[i] = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
        }

        return samples;
    }

    private static void PlayTone(double frequencyHz, int durationMs)
    {
        const int sampleRate = 22050;
        var sampleCount = Math.Max(1, sampleRate * durationMs / 1000);
        var samples = new short[sampleCount];
        var attack = Math.Max(1, sampleRate / 100);
        var release = Math.Max(1, sampleRate / 50);

        for (var i = 0; i < sampleCount; i++)
        {
            var envelope = 1.0;
            if (i < attack)
                envelope = (double)i / attack;
            else if (i > sampleCount - release)
                envelope = (double)(sampleCount - i) / release;

            var sample = Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate) * short.MaxValue * 1.0 * envelope;
            samples[i] = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
        }

        PlayPcm(samples);
    }

    private static void PlayPcm(short[] samples)
    {
        // Serialize tones — overlapping Static AudioTracks are often dropped mid-call.
        lock (ToneLock)
        {
            const int sampleRate = 22050;
            var durationMs = Math.Max(1, samples.Length * 1000 / sampleRate);

            using var track = new AudioTrack.Builder()
                .SetAudioAttributes(new AudioAttributes.Builder()!
                    // In-call signalling survives WebRTC audio focus better than Notification*.
                    .SetUsage(AudioUsageKind.VoiceCommunicationSignalling)!
                    .SetContentType(AudioContentType.Sonification)!
                    .Build()!)!
                .SetAudioFormat(new AudioFormat.Builder()!
                    .SetEncoding(Encoding.Pcm16bit)!
                    .SetSampleRate(sampleRate)!
                    .SetChannelMask(ChannelOut.Mono)!
                    .Build()!)!
                .SetBufferSizeInBytes(samples.Length * sizeof(short))!
                .SetTransferMode(AudioTrackMode.Static)!
                .Build()!;

            track.SetVolume(1f);
            track.Write(samples, 0, samples.Length);
            track.Play();
            Thread.Sleep(durationMs + 20);
            track.Stop();
        }
    }

    private PendingIntent CreateActionPendingIntent(string action, int requestCode)
    {
        var intent = new Intent(this, typeof(ConferenceMediaSessionService));
        intent.SetAction(action);
        return PendingIntent.GetService(
            this,
            requestCode,
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    private static Bitmap CreateCircularBitmap(Bitmap source)
    {
        var size = Math.Min(source.Width, source.Height);
        var output = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(output);
        using var paint = new Paint { AntiAlias = true };
        var rect = new Rect(0, 0, size, size);
        canvas.DrawARGB(0, 0, 0, 0);
        canvas.DrawCircle(size / 2f, size / 2f, size / 2f, paint);
        paint.SetXfermode(new PorterDuffXfermode(PorterDuff.Mode.SrcIn!));
        canvas.DrawBitmap(source, rect, rect, paint);
        if (!ReferenceEquals(source, output))
            source.Recycle();
        return output;
    }

    public static void Start(Context context)
    {
        var intent = new Intent(context, typeof(ConferenceMediaSessionService));
        if (global::System.OperatingSystem.IsAndroidVersionAtLeast(26))
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }

    public static void RequestToggleMicrophone(Context context)
    {
        var intent = new Intent(context, typeof(ConferenceMediaSessionService));
        intent.SetAction(ActionToggleMic);
        if (global::System.OperatingSystem.IsAndroidVersionAtLeast(26))
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }

    public static void Stop(Context context)
    {
        context.StopService(new Intent(context, typeof(ConferenceMediaSessionService)));
    }

    private sealed class MediaSessionCallback(ConferenceMediaSessionService service) : MediaSessionCompat.Callback
    {
        public override void OnPlay() => Toggle();

        public override void OnPause() => Toggle();

        public override void OnStop() => Toggle();

        public override bool OnMediaButtonEvent(Intent? mediaButtonEvent)
        {
            var keyEvent = ExtractKeyEvent(mediaButtonEvent);
            if (keyEvent is null)
                return base.OnMediaButtonEvent(mediaButtonEvent);

            if (keyEvent.Action == KeyEventActions.Down
                && keyEvent.KeyCode is Keycode.MediaPlayPause
                    or Keycode.Headsethook
                    or Keycode.MediaPlay
                    or Keycode.MediaPause)
            {
                Toggle();
                return true;
            }

            return base.OnMediaButtonEvent(mediaButtonEvent);
        }

        private void Toggle()
        {
            service.MainHandler.Post(() =>
            {
                if (service.TryToggleMicrophone())
                    service.PublishForeground(service._lastState);
            });
        }

        private static KeyEvent? ExtractKeyEvent(Intent? mediaButtonEvent)
        {
            if (mediaButtonEvent is null)
                return null;

            if (global::System.OperatingSystem.IsAndroidVersionAtLeast(33))
                return mediaButtonEvent.GetParcelableExtra(Intent.ExtraKeyEvent, Java.Lang.Class.FromType(typeof(KeyEvent))) as KeyEvent;

#pragma warning disable CA1422
            return mediaButtonEvent.GetParcelableExtra(Intent.ExtraKeyEvent) as KeyEvent;
#pragma warning restore CA1422
        }
    }
}
