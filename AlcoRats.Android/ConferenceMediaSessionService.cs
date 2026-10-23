using AlcoRats.Services;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
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
[IntentFilter([Intent.ActionMediaButton])]
public sealed class ConferenceMediaSessionService : Service
{
    public const string ChannelId = "alcorats_conference_hotkeys";
    public const int NotificationId = 4201;

    private MediaSessionCompat? _session;
    private MediaSessionCallback? _callback;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        EnsureNotificationChannel();
        CreateMediaSession();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        MediaButtonReceiver.HandleIntent(_session, intent);

        var notification = BuildNotification();
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

        if (_session is not null)
            _session.Active = true;

        UpdatePlaybackState(PlaybackStateCompat.StatePlaying);
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
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
        _callback = new MediaSessionCallback();
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
        if (manager?.GetNotificationChannel(ChannelId) is not null)
            return;

        var channel = new NotificationChannel(
            ChannelId,
            "Хоткей микрофона",
            NotificationImportance.Low)
        {
            Description = "Нужен, чтобы Play/Pause глушил микрофон при выключенном экране."
        };
        manager?.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification()
    {
        var launchIntent = new Intent(this, typeof(MainActivity));
        launchIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var contentIntent = PendingIntent.GetActivity(
            this,
            0,
            launchIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var icon = ApplicationInfo?.Icon ?? global::Android.Resource.Drawable.IcMediaPlay;

        return new NotificationCompat.Builder(this, ChannelId)!
            .SetContentTitle("AlcoRats")!
            .SetContentText("Play/Pause на гарнитуре — mute/unmute микрофона")!
            .SetSmallIcon(icon)!
            .SetContentIntent(contentIntent)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetCategory(NotificationCompat.CategoryService)!
            .SetVisibility(NotificationCompat.VisibilityPublic)!
            .SetPriority((int)NotificationPriority.Low)!
            .Build()!;
    }

    public static void Start(Context context)
    {
        var intent = new Intent(context, typeof(ConferenceMediaSessionService));
        if (global::System.OperatingSystem.IsAndroidVersionAtLeast(26))
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }

    public static void Stop(Context context)
    {
        context.StopService(new Intent(context, typeof(ConferenceMediaSessionService)));
    }

    private sealed class MediaSessionCallback : MediaSessionCompat.Callback
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

        private static void Toggle()
            => _ = ConferenceHotkeys.ToggleMicrophoneAsync();

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
