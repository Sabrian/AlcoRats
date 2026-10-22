using System.Linq;
using Android;
using Android.App;
using Android.Content.PM;
using Android.OS;
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
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private const int MediaPermissionRequestCode = 1001;

    private static readonly string[] RequiredPermissions =
    [
        Manifest.Permission.Camera,
        Manifest.Permission.RecordAudio,
        Manifest.Permission.ModifyAudioSettings
    ];

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RequestMediaPermissionsIfNeeded();
    }

    private void RequestMediaPermissionsIfNeeded()
    {
        var missing = RequiredPermissions
            .Where(permission =>
                ContextCompat.CheckSelfPermission(this, permission) != Permission.Granted)
            .ToArray();

        if (missing.Length > 0)
            ActivityCompat.RequestPermissions(this, missing, MediaPermissionRequestCode);
    }
}
