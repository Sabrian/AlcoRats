#nullable enable
using System;
using System.Threading;
using AlcoRats.Hosting;
using Android.App;
using Android.OS;
using Android.Runtime;
using Android.Text.Method;
using Android.Util;
using Android.Views;
using Android.Widget;
using Java.Lang;

namespace AlcoRats.Android;

/// <summary>Android AlertDialog + JNI unhandled hook for fatal exception text.</summary>
internal static class CrashUi
{
    public static void Register()
    {
        GlobalExceptionHandler.FatalErrorPresenter = Present;
        AndroidEnvironment.UnhandledExceptionRaiser += OnAndroidUnhandled;
    }

    private static void OnAndroidUnhandled(object? sender, RaiseThrowableEventArgs e)
    {
        // Prevent the runtime from killing us before the user can read the dialog.
        e.Handled = true;
        var message = e.Exception?.ToString() ?? e.Exception?.Message ?? "(null exception)";

        try
        {
            PresentThenExit(message);
        }
        catch
        {
            try { JavaSystem.Exit(1); }
            catch { /* ignored */ }
        }
    }

    private static void PresentThenExit(string message)
    {
        var activity = MainActivity.Current;
        if (activity is null || activity.IsFinishing || activity.IsDestroyed)
        {
            JavaSystem.Exit(1);
            return;
        }

        var onMain = Looper.MyLooper() == Looper.MainLooper;
        var done = new ManualResetEventSlim(false);

        void Show()
        {
            try
            {
                ShowDialog(activity, message, () =>
                {
                    done.Set();
                    try { JavaSystem.Exit(1); }
                    catch { /* ignored */ }
                });
            }
            catch (System.Exception)
            {
                done.Set();
                try { JavaSystem.Exit(1); }
                catch { /* ignored */ }
            }
        }

        if (onMain)
        {
            // Dialog OK exits — do not Wait on the main thread.
            Show();
            return;
        }

        activity.RunOnUiThread(Show);
        done.Wait(TimeSpan.FromMinutes(2));
        try { JavaSystem.Exit(1); }
        catch { /* ignored */ }
    }

    private static void Present(string message, ManualResetEventSlim done)
    {
        var activity = MainActivity.Current;
        if (activity is null || activity.IsFinishing || activity.IsDestroyed)
        {
            done.Set();
            return;
        }

        void Show()
        {
            try
            {
                ShowDialog(activity, message, done.Set);
            }
            catch (System.Exception)
            {
                done.Set();
            }
        }

        if (Looper.MyLooper() == Looper.MainLooper)
            Show();
        else
            activity.RunOnUiThread(Show);
    }

    private static void ShowDialog(Activity activity, string message, Action onOk)
    {
        var density = activity.Resources?.DisplayMetrics?.Density ?? 1f;
        var pad = (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, 16f, activity.Resources!.DisplayMetrics);

        var text = new TextView(activity);
        text.Text = message;
        text.SetTextIsSelectable(true);
        text.MovementMethod = new ScrollingMovementMethod();
        text.SetTextSize(ComplexUnitType.Sp, 13f);

        var scroll = new ScrollView(activity);
        scroll.AddView(text, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));
        scroll.SetPadding(pad, pad, pad, pad);
        scroll.SetMinimumHeight((int)(density * 180));

        var dialog = new AlertDialog.Builder(activity)
            .SetTitle("Критическая ошибка")!
            .SetView(scroll)!
            .SetCancelable(false)!
            .SetPositiveButton("OK", (_, _) => onOk())!
            .Create()!;

        dialog.SetCanceledOnTouchOutside(false);
        dialog.Show();
    }
}
