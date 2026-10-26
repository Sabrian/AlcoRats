using System;
using System.Linq;
using AlcoRats.Services;
using Android.App;
using Android.Content;
using Android.Runtime;
using Android.Webkit;
using Avalonia.Controls;
using Avalonia.Platform;

namespace AlcoRats.Android;

/// <summary>
/// Bridges Activity grants into the embedded WebView:
/// camera/mic for getUserMedia, and the system file picker for Jitsi chat «Share» (Files).
/// </summary>
public static class WebViewMediaPermissionBridge
{
    public const int FileChooserRequestCode = 1003;

    private static IValueCallback? _pendingFilePathCallback;

    public static void Register()
        => WebViewPlatformSetup.Configure = Attach;

    public static void Attach(NativeWebView webView)
    {
        webView.AdapterCreated -= OnAdapterCreated;
        webView.AdapterCreated += OnAdapterCreated;
        TryInstall(webView);
    }

    /// <summary>
    /// Completes a WebView file-chooser started from <see cref="GrantMediaWebChromeClient.OnShowFileChooser"/>.
    /// Call from <see cref="MainActivity.OnActivityResult"/>.
    /// </summary>
    public static bool TryHandleFileChooserResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode != FileChooserRequestCode)
            return false;

        var callback = _pendingFilePathCallback;
        _pendingFilePathCallback = null;
        if (callback is null)
            return true;

        try
        {
            global::Android.Net.Uri[]? uris = null;
            if (resultCode == Result.Ok)
                uris = WebChromeClient.FileChooserParams.ParseResult((int)resultCode, data);

            callback.OnReceiveValue(uris);
        }
        catch
        {
            try { callback.OnReceiveValue(null); } catch { /* ignore */ }
        }

        return true;
    }

    private static void CancelPendingFileChooser()
    {
        var callback = _pendingFilePathCallback;
        _pendingFilePathCallback = null;
        try { callback?.OnReceiveValue(null); } catch { /* ignore */ }
    }

    private static void OnAdapterCreated(object? sender, EventArgs e)
    {
        if (sender is NativeWebView webView)
            TryInstall(webView);
    }

    private static void TryInstall(NativeWebView webView)
    {
        if (webView.TryGetPlatformHandle() is not IAndroidWebViewPlatformHandle handle)
            return;

        if (handle.WebKitWebView == IntPtr.Zero)
            return;

        var native = Java.Lang.Object.GetObject<global::Android.Webkit.WebView>(
            handle.WebKitWebView,
            JniHandleOwnership.DoNotTransfer);

        if (native is null)
            return;

        var settings = native.Settings;
        if (settings is not null)
        {
            settings.JavaScriptEnabled = true;
            settings.DomStorageEnabled = true;
            settings.MediaPlaybackRequiresUserGesture = false;
            settings.AllowContentAccess = true;
            settings.AllowFileAccess = true;
        }

        // Keep Avalonia's chrome client (if any) and only override media + file chooser.
        WebChromeClient? existing = null;
        if (global::System.OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            existing = native.WebChromeClient;
            if (existing is GrantMediaWebChromeClient)
                return;
        }

        native.SetWebChromeClient(new GrantMediaWebChromeClient(existing));
    }

    private static Activity? FindActivity(Context? context)
    {
        while (context is not null)
        {
            if (context is Activity activity)
                return activity;
            if (context is ContextWrapper wrapper)
            {
                context = wrapper.BaseContext;
                continue;
            }

            break;
        }

        return null;
    }

    private sealed class GrantMediaWebChromeClient(WebChromeClient? inner) : WebChromeClient
    {
        public override void OnPermissionRequest(PermissionRequest? request)
        {
            if (request is null)
            {
                inner?.OnPermissionRequest(null);
                return;
            }

            var resources = request.GetResources() ?? [];
            var allowed = resources
                .Where(static r =>
                    r == PermissionRequest.ResourceVideoCapture
                    || r == PermissionRequest.ResourceAudioCapture)
                .ToArray();

            if (allowed.Length > 0)
                request.Grant(allowed);
            else
                request.Deny();
        }

        public override void OnPermissionRequestCanceled(PermissionRequest? request)
            => inner?.OnPermissionRequestCanceled(request);

        public override void OnProgressChanged(global::Android.Webkit.WebView? view, int newProgress)
            => inner?.OnProgressChanged(view, newProgress);

        public override void OnReceivedTitle(global::Android.Webkit.WebView? view, string? title)
            => inner?.OnReceivedTitle(view, title);

        public override bool OnShowFileChooser(
            global::Android.Webkit.WebView? webView,
            IValueCallback? filePathCallback,
            WebChromeClient.FileChooserParams? fileChooserParams)
        {
            // Do not defer to Avalonia's default — it typically no-ops and Share appears broken.
            CancelPendingFileChooser();
            _pendingFilePathCallback = filePathCallback;

            try
            {
                var activity = FindActivity(webView?.Context);
                if (activity is null)
                {
                    CancelPendingFileChooser();
                    return false;
                }

                Intent intent;
                try
                {
                    intent = fileChooserParams?.CreateIntent()
                             ?? CreateFallbackPickerIntent(fileChooserParams);
                }
                catch
                {
                    intent = CreateFallbackPickerIntent(fileChooserParams);
                }

                var title = fileChooserParams?.Title;
                var chooser = Intent.CreateChooser(
                    intent,
                    string.IsNullOrWhiteSpace(title) ? "Share file" : title);
                activity.StartActivityForResult(chooser, FileChooserRequestCode);
                return true;
            }
            catch
            {
                CancelPendingFileChooser();
                return false;
            }
        }

        private static Intent CreateFallbackPickerIntent(WebChromeClient.FileChooserParams? fileChooserParams)
        {
            var intent = new Intent(Intent.ActionGetContent);
            intent.AddCategory(Intent.CategoryOpenable);

            var accept = fileChooserParams?.GetAcceptTypes()
                ?.Where(static t => !string.IsNullOrWhiteSpace(t))
                .ToArray() ?? [];

            if (accept.Length == 1)
                intent.SetType(accept[0]);
            else
            {
                intent.SetType("*/*");
                if (accept.Length > 1)
                    intent.PutExtra(Intent.ExtraMimeTypes, accept);
            }

            // ChromeFileChooserMode: Open=0, OpenMultiple=1, Save=2 (API 21).
            if (fileChooserParams is not null
                && ((int)fileChooserParams.Mode & 1) != 0)
                intent.PutExtra(Intent.ExtraAllowMultiple, true);

            return intent;
        }

        public override void OnGeolocationPermissionsShowPrompt(
            string? origin,
            GeolocationPermissions.ICallback? callback)
        {
            if (inner is not null)
                inner.OnGeolocationPermissionsShowPrompt(origin, callback);
            else
                base.OnGeolocationPermissionsShowPrompt(origin, callback);
        }
    }
}
