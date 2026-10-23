using System;
using System.Threading.Tasks;
using AlcoRats.Services;
using AlcoRats.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AlcoRats.Views;

public partial class MainView : UserControl
{
    private readonly Func<Task> _toggleMicrophoneHandler;
    private bool _pageReady;
    private bool _navigationStarted;

    private const string ToggleMicScript = """
        (function () {
          try {
            if (typeof APP !== 'undefined' && APP.conference) {
              if (typeof APP.conference.toggleAudioMuted === 'function') {
                APP.conference.toggleAudioMuted();
                return 'app-toggle';
              }
              if (typeof APP.conference.muteAudio === 'function') {
                var muted = typeof APP.conference.isLocalAudioMuted === 'function'
                  && APP.conference.isLocalAudioMuted();
                APP.conference.muteAudio(!muted);
                return 'app-mute';
              }
            }
          } catch (e) {}

          try {
            var nodes = document.querySelectorAll('[aria-label], [data-testid]');
            for (var i = 0; i < nodes.length; i++) {
              var el = nodes[i];
              var label = ((el.getAttribute('aria-label') || '') + ' ' + (el.getAttribute('data-testid') || '')).toLowerCase();
              if (label.indexOf('microphone') >= 0 || label.indexOf('микрофон') >= 0 || label.indexOf('audio.btn') >= 0) {
                el.click();
                return 'dom';
              }
            }
          } catch (e) {}

          try {
            var target = document.activeElement || document.body || document.documentElement;
            target.dispatchEvent(new KeyboardEvent('keydown', {
              key: 'm', code: 'KeyM', keyCode: 77, which: 77, bubbles: true, cancelable: true
            }));
            return 'key';
          } catch (e) {}

          return 'fail';
        })()
        """;

    public MainView()
    {
        InitializeComponent();
        _toggleMicrophoneHandler = ToggleMicrophoneCoreAsync;
        RoomWebView.NavigationStarted += OnNavigationStarted;
        RoomWebView.NavigationCompleted += OnNavigationCompleted;
        RoomWebView.AdapterCreated += OnAdapterCreated;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        WebViewPlatformSetup.Apply(RoomWebView);
    }

    private void OnAdapterCreated(object? sender, EventArgs e)
        => WebViewPlatformSetup.Apply(RoomWebView);

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ConferenceHotkeys.Register(_toggleMicrophoneHandler);
        WebViewPlatformSetup.Apply(RoomWebView);
        StartNavigation();
        _ = WatchdogAsync();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        => ConferenceHotkeys.Unregister(_toggleMicrophoneHandler);

    private void StartNavigation()
    {
        if (_navigationStarted)
            return;

        var url = (DataContext as MainViewModel)?.RoomUrl ?? MainViewModel.DefaultRoomUri;
        _navigationStarted = true;
        SetStatus("Открытие: " + url.Host + "…");

        try
        {
            RoomWebView.Source = url;
            RoomWebView.Navigate(url);
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка WebView: " + ex.Message);
        }
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationEventArgs e)
        => SetStatus("Загрузка страницы…");

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        _pageReady = e.IsSuccess;
        SetStatus(e.IsSuccess
            ? "Комната загружена. Play/Pause на гарнитуре — mute микрофона."
            : "Не удалось загрузить комнату. Проверьте сеть / URL.");
    }

    private async Task WatchdogAsync()
    {
        await Task.Delay(15000);
        if (_pageReady)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_pageReady)
                SetStatus("Долгая загрузка или чёрный WebView. Проверьте интернет эмулятора и GPU (SwiftShader).");
        });
    }

    private void SetStatus(string text)
    {
        if (Dispatcher.UIThread.CheckAccess())
            StatusText.Text = text;
        else
            Dispatcher.UIThread.Post(() => StatusText.Text = text);
    }

    private async Task ToggleMicrophoneCoreAsync()
    {
        if (!_pageReady)
            return;

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                await RoomWebView.InvokeScript(ToggleMicScript);
            }
            catch
            {
                // WebView may not be ready yet (join screen / reload).
            }
        });
    }
}
