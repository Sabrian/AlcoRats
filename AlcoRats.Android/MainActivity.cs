using System.Collections.Generic;
using System.Linq;
using AlcoRats.Hosting;
using AlcoRats.Services;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Provider;
using Android.Views;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using AndroidX.Core.View;
using Avalonia.Android;
using Microsoft.Extensions.DependencyInjection;

namespace AlcoRats.Android;

[Activity(
    Label = "AlcoRats",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode,
    LaunchMode = LaunchMode.SingleTop,
    // Let Avalonia TextBox receive IME (incl. Russian) instead of the embedded WebView.
    WindowSoftInputMode = SoftInput.AdjustResize)]
public class MainActivity : AvaloniaMainActivity
{
    private const int MediaPermissionRequestCode = 1001;
    private const int NotificationPermissionRequestCode = 1002;

    /// <summary>Camera + microphone — required for the Jitsi room.</summary>
    private static readonly string[] MediaPermissions =
    [
        Manifest.Permission.Camera,
        Manifest.Permission.RecordAudio
    ];

    private bool _permissionRequestInFlight;
    private bool _mediaSessionStarted;
    private IConferenceSession? _conference;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Opaque window avoids black/blank frames with Avalonia + native WebView.
        ApplyWindowBackground();
        FitDisplayToDevice();

        // One runtime permission dialog at a time — parallel RequestPermissions is unreliable
        // and can crash/cancel the first prompt on first launch (esp. Android 13+).
        RequestNextPermission();
        WireConferenceShadeLifecycle();
    }

    /// <summary>
    /// Edge-to-edge with padding for all system bars (status, nav, cutout).
    /// Avalonia chrome (bottom StatusPanel) must sit above the nav bar.
    /// Jitsi toolbox is pinned to the WebView viewport and its safe-area ::after is
    /// stripped in the bridge — so padding here does not create a double gap.
    /// </summary>
    private void FitDisplayToDevice()
    {
        if (Window is null)
            return;

        WindowCompat.SetDecorFitsSystemWindows(Window, false);

        if (global::System.OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            var layoutParams = Window.Attributes;
            if (layoutParams is not null)
            {
                layoutParams.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
                Window.Attributes = layoutParams;
            }
        }

        var content = FindViewById(global::Android.Resource.Id.Content);
        if (content is null)
            return;

        ViewCompat.SetOnApplyWindowInsetsListener(content, new SystemBarsPaddingInsets());
        ViewCompat.RequestApplyInsets(content);
    }

    private sealed class SystemBarsPaddingInsets : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(View? v, WindowInsetsCompat? insets)
        {
            if (v is null)
                return insets ?? WindowInsetsCompat.Consumed!;
            if (insets is null)
                return WindowInsetsCompat.Consumed!;

            var bars = insets.GetInsets(
                WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout())!;
            v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            return insets.Inset(bars.Left, bars.Top, bars.Right, bars.Bottom)!;
        }
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplyWindowBackground();
        FitDisplayToDevice();
    }

    protected override void OnResume()
    {
        base.OnResume();
        FitDisplayToDevice();

        if (!_permissionRequestInFlight)
            RequestNextPermission();

        WireConferenceShadeLifecycle();
        TryStartMediaSession();
    }

    private void ApplyWindowBackground()
    {
        var color = new Color(ContextCompat.GetColor(this, Resource.Color.app_surface));
        Window?.SetBackgroundDrawable(new ColorDrawable(color));
    }

    protected override void OnStart()
    {
        base.OnStart();
        WireConferenceShadeLifecycle();
        TryStartMediaSession();
    }

    protected override void OnDestroy()
    {
        if (_conference is not null)
        {
            _conference.StateChanged -= OnConferenceStateChangedForShade;
            _conference = null;
        }

        if (IsFinishing)
        {
            ConferenceMediaSessionService.Stop(this);
            _mediaSessionStarted = false;
        }

        base.OnDestroy();
    }

    private void WireConferenceShadeLifecycle()
    {
        if (!AppHost.IsInitialized)
            return;

        _conference = AppHost.Services.GetService<IConferenceSession>();
        if (_conference is null)
            return;

        _conference.StateChanged -= OnConferenceStateChangedForShade;
        _conference.StateChanged += OnConferenceStateChangedForShade;
        SyncMediaSessionWithConference(_conference.State);
    }

    private void OnConferenceStateChangedForShade(ConferenceSessionState state)
        => RunOnUiThread(() => SyncMediaSessionWithConference(state));

    private void SyncMediaSessionWithConference(ConferenceSessionState state)
    {
        if (state.IsActive)
            TryStartMediaSession();
        else if (_mediaSessionStarted)
        {
            ConferenceMediaSessionService.Stop(this);
            _mediaSessionStarted = false;
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        // Jitsi chat Files → Share uses <input type=file>; WebView needs this callback.
        if (WebViewMediaPermissionBridge.TryHandleFileChooserResult(requestCode, resultCode, data))
            return;

        base.OnActivityResult(requestCode, resultCode, data);
    }

    public override void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        _permissionRequestInFlight = false;

        if (requestCode == MediaPermissionRequestCode)
        {
            if (!HasAllPermissions(MediaPermissions))
            {
                var permanentlyDenied = MediaPermissions.Any(permission =>
                    ContextCompat.CheckSelfPermission(this, permission) != Permission.Granted
                    && !ActivityCompat.ShouldShowRequestPermissionRationale(this, permission));

                if (permanentlyDenied)
                    ShowOpenSettingsDialog();
                else
                    ShowPermissionRationaleDialog();

                return;
            }
        }

        // Continue the chain (notifications after media, etc.) then start FGS if allowed.
        RequestNextPermission();
        TryStartMediaSession();
    }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is not null
            && e.Action == KeyEventActions.Down
            && IsPlayPauseKey(e)
            && !e.IsCanceled)
        {
            ConferenceMediaSessionService.RequestToggleMicrophone(this);
            return true;
        }

        return base.DispatchKeyEvent(e);
    }

    /// <summary>
    /// Asks for the next missing permission group. Never overlaps two system dialogs.
    /// Order: camera+mic first (needed for call + mic FGS), then notifications (Android 13+).
    /// </summary>
    private void RequestNextPermission()
    {
        if (_permissionRequestInFlight)
            return;

        var missingMedia = GetMissingPermissions(MediaPermissions);
        if (missingMedia.Length > 0)
        {
            _permissionRequestInFlight = true;
            ActivityCompat.RequestPermissions(this, missingMedia, MediaPermissionRequestCode);
            return;
        }

        if (NeedsNotificationPermission())
        {
            _permissionRequestInFlight = true;
            ActivityCompat.RequestPermissions(
                this,
                [Manifest.Permission.PostNotifications],
                NotificationPermissionRequestCode);
        }
    }

    private bool NeedsNotificationPermission()
        => global::System.OperatingSystem.IsAndroidVersionAtLeast(33)
           && ContextCompat.CheckSelfPermission(this, Manifest.Permission.PostNotifications)
              != Permission.Granted;

    private void TryStartMediaSession()
    {
        // Shade / FGS only while actually in a live conference.
        if (_conference is null && AppHost.IsInitialized)
            _conference = AppHost.Services.GetService<IConferenceSession>();

        if (_conference is null || !_conference.State.IsActive)
        {
            if (_mediaSessionStarted)
            {
                ConferenceMediaSessionService.Stop(this);
                _mediaSessionStarted = false;
            }
            return;
        }

        // Android 14+ can kill/crash a microphone FGS if RECORD_AUDIO is not granted yet.
        if (ContextCompat.CheckSelfPermission(this, Manifest.Permission.RecordAudio) != Permission.Granted)
            return;

        if (_mediaSessionStarted)
        {
            // Refresh notification content while already running.
            ConferenceMediaSessionService.Start(this);
            return;
        }

        ConferenceMediaSessionService.Start(this);
        _mediaSessionStarted = true;
    }

    private void ShowPermissionRationaleDialog()
    {
        new AlertDialog.Builder(this)
            .SetTitle("Нужны камера и микрофон")!
            .SetMessage("Для видеозвонка в комнате Jitsi приложению нужны доступ к камере и микрофону.")!
            .SetCancelable(false)!
            .SetPositiveButton("Разрешить", (_, _) => RequestNextPermission())!
            .SetNegativeButton("Позже", (_, _) => { })!
            .Show();
    }

    private void ShowOpenSettingsDialog()
    {
        new AlertDialog.Builder(this)
            .SetTitle("Разрешения отключены")!
            .SetMessage("Камера или микрофон запрещены. Откройте настройки приложения и включите их вручную.")!
            .SetCancelable(false)!
            .SetPositiveButton("Настройки", (_, _) => OpenAppSettings())!
            .SetNegativeButton("Позже", (_, _) => { })!
            .Show();
    }

    private void OpenAppSettings()
    {
        var intent = new Intent(global::Android.Provider.Settings.ActionApplicationDetailsSettings);
        intent.SetData(global::Android.Net.Uri.FromParts("package", PackageName, null));
        intent.AddFlags(ActivityFlags.NewTask);
        StartActivity(intent);
    }

    private bool HasAllPermissions(IEnumerable<string> permissions)
        => permissions.All(permission =>
            ContextCompat.CheckSelfPermission(this, permission) == Permission.Granted);

    private string[] GetMissingPermissions(IEnumerable<string> permissions)
        => permissions
            .Where(permission =>
                ContextCompat.CheckSelfPermission(this, permission) != Permission.Granted)
            .ToArray();

    private static bool IsPlayPauseKey(KeyEvent e)
        => e.KeyCode is Keycode.MediaPlayPause
            or Keycode.Headsethook
            or Keycode.MediaPlay
            or Keycode.MediaPause;
}
