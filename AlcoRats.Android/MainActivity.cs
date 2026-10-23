using System.Collections.Generic;
using System.Linq;
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

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Opaque window avoids black/blank frames with Avalonia + native WebView.
        ApplyWindowBackground();
        FitDisplayToDevice();

        // One runtime permission dialog at a time — parallel RequestPermissions is unreliable
        // and can crash/cancel the first prompt on first launch (esp. Android 13+).
        RequestNextPermission();
    }

    /// <summary>
    /// Keep Avalonia/WebView in the usable area above system bars.
    /// Edge-to-edge (DecorFitsSystemWindows=false) leaves a nav-bar gap under Jitsi's
    /// floating toolbar while the toolbar still overlays the video — avoid that.
    /// </summary>
    private void FitDisplayToDevice()
    {
        if (Window is null)
            return;

        // Let the framework inset content for status + navigation bars.
        WindowCompat.SetDecorFitsSystemWindows(Window, true);

        if (global::System.OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            var layoutParams = Window.Attributes;
            if (layoutParams is not null)
            {
                layoutParams.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
                Window.Attributes = layoutParams;
            }
        }
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplyWindowBackground();
    }

    protected override void OnResume()
    {
        base.OnResume();

        if (!_permissionRequestInFlight)
            RequestNextPermission();

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
        TryStartMediaSession();
    }

    protected override void OnDestroy()
    {
        if (IsFinishing)
        {
            ConferenceMediaSessionService.Stop(this);
            _mediaSessionStarted = false;
        }

        base.OnDestroy();
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
        if (_mediaSessionStarted)
            return;

        // Android 14+ can kill/crash a microphone FGS if RECORD_AUDIO is not granted yet.
        if (ContextCompat.CheckSelfPermission(this, Manifest.Permission.RecordAudio) != Permission.Granted)
            return;

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
