using System;
using System.Linq;
using AlcoRats.Services;
using Android.Runtime;
using Android.Webkit;
using Avalonia.Controls;
using Avalonia.Platform;

namespace AlcoRats.Android;

/// <summary>
/// Forwards Activity CAMERA/RECORD_AUDIO grants into the embedded WebView
/// when the page calls getUserMedia (Jitsi).
/// </summary>
public static class WebViewMediaPermissionBridge
{
    public static void Register()
        => WebViewPlatformSetup.Configure = Attach;

    public static void Attach(NativeWebView webView)
    {
        webView.AdapterCreated -= OnAdapterCreated;
        webView.AdapterCreated += OnAdapterCreated;
        TryInstall(webView);
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

        // Keep Avalonia's chrome client (if any) and only override media permissions.
        WebChromeClient? existing = null;
        if (global::System.OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            existing = native.WebChromeClient;
            if (existing is GrantMediaWebChromeClient)
                return;
        }

        native.SetWebChromeClient(new GrantMediaWebChromeClient(existing));
    }

    private sealed class GrantMediaWebChromeClient : WebChromeClient
    {
        private readonly WebChromeClient? _inner;

        public GrantMediaWebChromeClient(WebChromeClient? inner)
            => _inner = inner;

        public override void OnPermissionRequest(PermissionRequest? request)
        {
            if (request is null)
            {
                _inner?.OnPermissionRequest(null);
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
            => _inner?.OnPermissionRequestCanceled(request);

        public override void OnProgressChanged(global::Android.Webkit.WebView? view, int newProgress)
            => _inner?.OnProgressChanged(view, newProgress);

        public override void OnReceivedTitle(global::Android.Webkit.WebView? view, string? title)
            => _inner?.OnReceivedTitle(view, title);

        public override bool OnShowFileChooser(
            global::Android.Webkit.WebView? webView,
            IValueCallback? filePathCallback,
            FileChooserParams? fileChooserParams)
        {
            if (_inner is not null)
                return _inner.OnShowFileChooser(webView, filePathCallback, fileChooserParams);

            return base.OnShowFileChooser(webView, filePathCallback, fileChooserParams);
        }

        public override void OnGeolocationPermissionsShowPrompt(
            string? origin,
            GeolocationPermissions.ICallback? callback)
        {
            if (_inner is not null)
                _inner.OnGeolocationPermissionsShowPrompt(origin, callback);
            else
                base.OnGeolocationPermissionsShowPrompt(origin, callback);
        }
    }
}
