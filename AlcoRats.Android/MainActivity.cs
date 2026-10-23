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
using Avalonia.Android;

namespace AlcoRats.Android;

[Activity(
    Label = "AlcoRats",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode,
    LaunchMode = LaunchMode.SingleTop)]
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

    private bool _mediaPermissionPromptShown;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Opaque window avoids black/blank frames with Avalonia + native WebView.
        ApplyWindowBackground();

        EnsureMediaPermissions();
        EnsureNotificationPermission();
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplyWindowBackground();
    }

    protected override void OnResume()
    {
        base.OnResume();

        // User may return from Settings after denying — ask again if still missing.
        if (!HasAllPermissions(MediaPermissions))
            EnsureMediaPermissions();
    }

    private void ApplyWindowBackground()
    {
        var color = new Color(ContextCompat.GetColor(this, Resource.Color.app_surface));
        Window?.SetBackgroundDrawable(new ColorDrawable(color));
    }

    protected override void OnStart()
    {
        base.OnStart();
        ConferenceMediaSessionService.Start(this);
    }

    protected override void OnDestroy()
    {
        if (IsFinishing)
            ConferenceMediaSessionService.Stop(this);

        base.OnDestroy();
    }

    public override void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode != MediaPermissionRequestCode)
            return;

        _mediaPermissionPromptShown = false;

        if (HasAllPermissions(MediaPermissions))
            return;

        var permanentlyDenied = MediaPermissions.Any(permission =>
            ContextCompat.CheckSelfPermission(this, permission) != Permission.Granted
            && !ActivityCompat.ShouldShowRequestPermissionRationale(this, permission));

        if (permanentlyDenied)
            ShowOpenSettingsDialog();
        else
            ShowPermissionRationaleDialog();
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

    private void EnsureMediaPermissions()
    {
        var missing = GetMissingPermissions(MediaPermissions);
        if (missing.Length == 0)
            return;

        if (_mediaPermissionPromptShown)
            return;

        _mediaPermissionPromptShown = true;
        ActivityCompat.RequestPermissions(this, missing, MediaPermissionRequestCode);
    }

    private void EnsureNotificationPermission()
    {
        if (!global::System.OperatingSystem.IsAndroidVersionAtLeast(33))
            return;

        if (ContextCompat.CheckSelfPermission(this, Manifest.Permission.PostNotifications) == Permission.Granted)
            return;

        ActivityCompat.RequestPermissions(
            this,
            [Manifest.Permission.PostNotifications],
            NotificationPermissionRequestCode);
    }

    private void ShowPermissionRationaleDialog()
    {
        new AlertDialog.Builder(this)
            .SetTitle("Нужны камера и микрофон")!
            .SetMessage("Для видеозвонка в комнате Jitsi приложению нужны доступ к камере и микрофону.")!
            .SetCancelable(false)!
            .SetPositiveButton("Разрешить", (_, _) =>
            {
                _mediaPermissionPromptShown = false;
                EnsureMediaPermissions();
            })!
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
