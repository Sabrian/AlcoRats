using System;
using Avalonia.Controls;

namespace AlcoRats.Services;

/// <summary>
/// Platform hooks for NativeWebView (e.g. Android camera/mic permission bridging).
/// </summary>
public static class WebViewPlatformSetup
{
    public static Action<NativeWebView> Configure { get; set; }

    /// <summary>
    /// Android call-audio picker → WebView (setSinkId / Jitsi). Argument: earpiece|speaker|wired|bluetooth|…
    /// </summary>
    public static Action<string> RequestAudioOutput { get; set; }

    public static void Apply(NativeWebView webView)
        => Configure?.Invoke(webView);
}
