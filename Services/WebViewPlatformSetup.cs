using System;
using Avalonia.Controls;

namespace AlcoRats.Services;

/// <summary>
/// Platform hooks for NativeWebView (e.g. Android camera/mic permission bridging).
/// </summary>
public static class WebViewPlatformSetup
{
    public static Action<NativeWebView>? Configure { get; set; }

    public static void Apply(NativeWebView webView)
        => Configure?.Invoke(webView);
}
