#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AlcoRats.Hosting;
using AlcoRats.Services;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AlcoRats.Android;

/// <summary>
/// CallStyle allows only hangup + one custom action. This dialog packs mic toggle
/// and audio-route selection behind that single notification button.
/// </summary>
[Activity(
    Name = "com.alcorats.app.ConferenceCallActionsActivity",
    Theme = "@android:style/Theme.DeviceDefault.Dialog.Alert",
    ExcludeFromRecents = true,
    Exported = false,
    LaunchMode = LaunchMode.SingleTop,
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class ConferenceCallActionsActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetFlags(WindowManagerFlags.NotTouchModal, WindowManagerFlags.NotTouchModal);

        var conference = AppHost.IsInitialized
            ? AppHost.Services.GetService<IConferenceSession>()
            : null;
        var muted = conference?.State.AudioMuted ?? false;
        var routes = ConferenceAudio.ListRoutes(this);
        var currentAudio = ConferenceAudio.CurrentLabel(this);

        var labels = new List<string>
        {
            muted ? "Микрофон: выкл  →  включить" : "Микрофон: вкл  →  выключить"
        };
        labels.AddRange(routes.Select(r =>
            string.Equals(r.Label, currentAudio, StringComparison.OrdinalIgnoreCase)
                ? "✓ Звук: " + r.Label
                : "Звук: " + r.Label));

        if (routes.Count == 0)
            labels.Add("Звук: нет устройств");

        new AlertDialog.Builder(this)
            .SetTitle("Управление звонком")!
            .SetItems(labels.ToArray(), (_, args) =>
            {
                var index = args.Which;
                if (index == 0)
                {
                    ConferenceMediaSessionService.RequestToggleMicrophone(this);
                }
                else
                {
                    var routeIndex = index - 1;
                    if (routeIndex >= 0 && routeIndex < routes.Count)
                        ConferenceAudio.Select(this, routes[routeIndex]);
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
