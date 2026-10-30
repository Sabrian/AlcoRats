#nullable enable
using System;
using System.Linq;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace AlcoRats.Android;

/// <summary>
/// Dialog-style picker launched from the call notification to choose speaker / headset / etc.
/// </summary>
[Activity(
    Name = "com.alcorats.app.AudioRoutePickerActivity",
    Theme = "@android:style/Theme.DeviceDefault.Dialog.Alert",
    ExcludeFromRecents = true,
    Exported = false,
    LaunchMode = LaunchMode.SingleTop,
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class AudioRoutePickerActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetFlags(WindowManagerFlags.NotTouchModal, WindowManagerFlags.NotTouchModal);

        var routes = ConferenceAudio.ListRoutes(this);
        if (routes.Count == 0)
        {
            new AlertDialog.Builder(this)
                .SetTitle("Устройство звука")!
                .SetMessage("Нет доступных устройств вывода.")!
                .SetPositiveButton("OK", (_, _) => Finish())!
                .SetOnCancelListener(new CancelListener(Finish))!
                .Show();
            return;
        }

        var current = ConferenceAudio.CurrentLabel(this);
        var labels = routes
            .Select(r => string.Equals(r.Label, current, StringComparison.OrdinalIgnoreCase)
                ? "✓ " + r.Label
                : r.Label)
            .ToArray();

        new AlertDialog.Builder(this)
            .SetTitle("Устройство звука")!
            .SetItems(labels, (_, args) =>
            {
                var index = args.Which;
                if (index >= 0 && index < routes.Count)
                {
                    ConferenceAudio.Select(this, routes[index]);
                    ConferenceMediaSessionService.RequestRefresh(this);
                }
                Finish();
            })!
            .SetNegativeButton("Отмена", (_, _) => Finish())!
            .SetOnCancelListener(new CancelListener(Finish))!
            .Show();
    }

    private sealed class CancelListener(Action onCancel) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => onCancel();
    }
}
