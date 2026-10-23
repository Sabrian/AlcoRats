using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AlcoRats.Hosting;
using AlcoRats.Services;
using AlcoRats.Settings;
using AlcoRats.ViewModels;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Design = Avalonia.Controls.Design;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlcoRats.Views;

public partial class MainView : UserControl
{
    private readonly Func<Task> _toggleMicrophoneHandler;
    private readonly Func<Task> _toggleCameraHandler;
    private readonly Func<Task> _leaveHandler;
    private readonly Func<Task> _openChatHandler;
    private readonly ILogger<MainView> _logger;
    private readonly IConferenceSession _conference;
    private readonly AppSettings _settings;
    private bool _pageReady;
    private bool _navigationStarted;
    private bool _bridgeInstalled;
    private bool _conferenceActive;
    private bool _settingsOpen;
    private bool _settingsAnimating;
    private readonly TranslateTransform _settingsTransform = new();
    private NativeWebView RoomWebView = null!;

    private const string ToggleMicScript = """
        (function () {
          try {
            if (typeof APP !== 'undefined' && APP.conference) {
              if (typeof APP.conference.toggleAudioMuted === 'function') {
                APP.conference.toggleAudioMuted();
                if (typeof window.__alcoRatsPublishConference === 'function')
                  setTimeout(window.__alcoRatsPublishConference, 120);
                return 'ok';
              }
              if (typeof APP.conference.muteAudio === 'function') {
                var muted = typeof APP.conference.isLocalAudioMuted === 'function'
                  && APP.conference.isLocalAudioMuted();
                APP.conference.muteAudio(!muted);
                if (typeof window.__alcoRatsPublishConference === 'function')
                  setTimeout(window.__alcoRatsPublishConference, 120);
                return 'ok';
              }
            }
          } catch (e) {}
          return 'fail';
        })()
        """;

    private const string ToggleCamScript = """
        (function () {
          try {
            if (typeof APP !== 'undefined' && APP.conference) {
              if (typeof APP.conference.toggleVideoMuted === 'function') {
                APP.conference.toggleVideoMuted();
                if (typeof window.__alcoRatsPublishConference === 'function')
                  setTimeout(window.__alcoRatsPublishConference, 120);
                return 'ok';
              }
              if (typeof APP.conference.muteVideo === 'function') {
                var muted = typeof APP.conference.isLocalVideoMuted === 'function'
                  && APP.conference.isLocalVideoMuted();
                APP.conference.muteVideo(!muted);
                if (typeof window.__alcoRatsPublishConference === 'function')
                  setTimeout(window.__alcoRatsPublishConference, 120);
                return 'ok';
              }
            }
          } catch (e) {}
          return 'fail';
        })()
        """;

    private const string LeaveScript = """
        (function () {
          // Tell the app immediately — hangup alone often leaves a leftover post-meeting SPA screen.
          function publishInactive() {
            try {
              if (typeof invokeCSharpAction === 'function') {
                invokeCSharpAction(JSON.stringify({
                  active: false, audioMuted: false, videoMuted: false, unreadMessages: 0, participants: []
                }));
              }
            } catch (e) {}
          }
          publishInactive();
          try {
            if (typeof APP !== 'undefined' && APP.conference && typeof APP.conference.hangup === 'function') {
              // false = skip feedback prompt when supported (avoids sticky leftover UI).
              try { APP.conference.hangup(false); } catch (e1) { APP.conference.hangup(); }
              return 'ok';
            }
          } catch (e) {}
          try {
            var nodes = document.querySelectorAll('[aria-label], [data-testid]');
            for (var i = 0; i < nodes.length; i++) {
              var label = ((nodes[i].getAttribute('aria-label') || '') + ' ' + (nodes[i].getAttribute('data-testid') || '')).toLowerCase();
              if (label.indexOf('leave') >= 0 || label.indexOf('hangup') >= 0 || label.indexOf('покинуть') >= 0 || label.indexOf('заверш') >= 0) {
                nodes[i].click();
                return 'dom';
              }
            }
          } catch (e) {}
          return 'fail';
        })()
        """;

    private const string OpenChatScript = """
        (function () {
          try {
            if (typeof APP !== 'undefined' && APP.store && typeof APP.store.dispatch === 'function') {
              var chat = (APP.store.getState()['features/chat'] || {});
              if (chat.isOpen) {
                if (typeof window.__alcoRatsPublishConference === 'function')
                  setTimeout(window.__alcoRatsPublishConference, 80);
                return 'open';
              }
              try { APP.store.dispatch({ type: 'OPEN_CHAT' }); } catch (e1) {
                try { APP.store.dispatch({ type: 'TOGGLE_CHAT' }); } catch (e2) {}
              }
              if (typeof window.__alcoRatsPublishConference === 'function')
                setTimeout(window.__alcoRatsPublishConference, 120);
              return 'ok';
            }
          } catch (e) {}
          try {
            var nodes = document.querySelectorAll('[aria-label], [data-testid], button');
            for (var i = 0; i < nodes.length; i++) {
              var label = ((nodes[i].getAttribute('aria-label') || '') + ' ' + (nodes[i].getAttribute('data-testid') || '') + ' ' + (nodes[i].textContent || '')).toLowerCase();
              if (label.indexOf('chat') >= 0 || label.indexOf('чат') >= 0 || label.indexOf('message') >= 0 || label.indexOf('сообщ') >= 0) {
                nodes[i].click();
                if (typeof window.__alcoRatsPublishConference === 'function')
                  setTimeout(window.__alcoRatsPublishConference, 120);
                return 'dom';
              }
            }
          } catch (e) {}
          return 'fail';
        })()
        """;

    private const string BridgeScript = """
        (function () {
          // Bump version to reinstall after fixes.
          if (window.__alcoRatsBridgeVersion === 8) return 'exists';
          window.__alcoRatsBridgeVersion = 8;
          if (window.__alcoRatsBridgeTimer) clearInterval(window.__alcoRatsBridgeTimer);
          if (window.__alcoRatsPasswordTimer) clearInterval(window.__alcoRatsPasswordTimer);
          // Allow re-subscribe after WebView/bridge reinstall.
          window.__alcoRatsChatSubscribed = false;

          function addParticipant(byId, p, isLocal) {
            if (!p || typeof p !== 'object') return;
            var id = p.id != null ? String(p.id) : null;
            if (!id) return;
            byId[id] = {
              id: id,
              name: p.name || p.displayName || (isLocal || p.local ? 'Вы' : 'Участник'),
              avatar: p.avatarURL || p.avatarUrl || '',
              local: !!(isLocal || p.local)
            };
          }

          function eachMapLike(mapLike, fn) {
            if (!mapLike) return;
            if (typeof mapLike.forEach === 'function') {
              mapLike.forEach(function (value, key) { fn(value, key); });
              return;
            }
            if (typeof mapLike === 'object') {
              Object.keys(mapLike).forEach(function (key) { fn(mapLike[key], key); });
            }
          }

          // True only while inside the live call — not prejoin, lobby, or joining.
          function isConferenceActive() {
            try {
              if (typeof APP === 'undefined') return false;

              var joinedApi = false;
              var hasJoinedApi = false;
              if (APP.conference && typeof APP.conference.isJoined === 'function') {
                hasJoinedApi = true;
                joinedApi = !!APP.conference.isJoined();
              }

              if (APP.store && typeof APP.store.getState === 'function') {
                var state = APP.store.getState();
                var conf = state['features/base/conference'] || {};
                var prejoin = state['features/prejoin'] || state['features/base/prejoin'] || {};
                var lobby = state['features/lobby'] || {};

                if (prejoin.showPrejoin === true || prejoin.isPrejoinPageVisible === true)
                  return false;
                if (lobby.knocking === true || lobby.lobbyVisible === true)
                  return false;
                if (conf.joining || conf.leaving || conf.authRequired)
                  return false;
                // Post-meeting / feedback screens are not a live call.
                if (state['features/feedback'] && state['features/feedback'].visible)
                  return false;
                if (conf.conference === undefined && !joinedApi)
                  return false;

                // isJoined is authoritative once prejoin/lobby/joining are ruled out.
                if (hasJoinedApi)
                  return joinedApi;

                return !!(conf.conference && conf.room);
              }

              return hasJoinedApi ? joinedApi : false;
            } catch (e) {
              return false;
            }
          }

          function getUnreadChatCount(state) {
            try {
              var chat = state['features/chat'] || {};
              // Open chat = user already seeing messages — no unread / no tone.
              if (chat.isOpen)
                return 0;

              var fromMessages = 0;
              var messages = chat.messages || [];
              if (messages.length) {
                var lastRead = chat.lastReadMessage;
                var lastReadIndex = -1;
                if (lastRead) {
                  lastReadIndex = messages.lastIndexOf(lastRead);
                  if (lastReadIndex < 0) {
                    for (var i = messages.length - 1; i >= 0; i--) {
                      var m = messages[i];
                      if (!m) continue;
                      if ((lastRead.messageId && m.messageId === lastRead.messageId)
                        || (lastRead.id && m.id === lastRead.id)
                        || (lastRead.timestamp && m.timestamp === lastRead.timestamp && m.message === lastRead.message)) {
                        lastReadIndex = i;
                        break;
                      }
                    }
                  }
                }

                var reactionMessages = 0;
                for (var j = lastReadIndex + 1; j < messages.length; j++) {
                  if (messages[j] && messages[j].isReaction)
                    reactionMessages++;
                }
                fromMessages = Math.max(0, messages.length - (lastReadIndex + 1) - reactionMessages);
              }

              // nbUnreadMessages is often present but stale/zero — take the max with message-based count.
              var fromNb = typeof chat.nbUnreadMessages === 'number'
                ? Math.max(0, chat.nbUnreadMessages | 0)
                : 0;
              return Math.max(fromMessages, fromNb);
            } catch (e) {
              return 0;
            }
          }

          function ensureChatSubscribe() {
            try {
              if (window.__alcoRatsChatSubscribed) return;
              if (typeof APP === 'undefined' || !APP.store || typeof APP.store.subscribe !== 'function')
                return;
              window.__alcoRatsChatSubscribed = true;
              var lastUnread = -1;
              APP.store.subscribe(function () {
                try {
                  var next = collect();
                  if (next.unreadMessages !== lastUnread) {
                    lastUnread = next.unreadMessages;
                    publish();
                  }
                } catch (e) {}
              });
            } catch (e) {}
          }

          function collect() {
            var out = { active: false, audioMuted: false, videoMuted: false, unreadMessages: 0, participants: [] };
            try {
              if (typeof APP === 'undefined') return out;

              out.active = isConferenceActive();

              if (!out.active)
                return out;

              if (APP.conference) {
                if (typeof APP.conference.isLocalAudioMuted === 'function')
                  out.audioMuted = !!APP.conference.isLocalAudioMuted();
                if (typeof APP.conference.isLocalVideoMuted === 'function')
                  out.videoMuted = !!APP.conference.isLocalVideoMuted();
              }

              var byId = {};
              if (APP.store && typeof APP.store.getState === 'function') {
                var state = APP.store.getState();
                out.unreadMessages = getUnreadChatCount(state);
                var pstate = state['features/base/participants'];
                if (pstate && typeof pstate === 'object') {
                  addParticipant(byId, pstate.local, true);
                  eachMapLike(pstate.remote, function (p) { addParticipant(byId, p, false); });

                  Object.keys(pstate).forEach(function (key) {
                    if (key === 'local' || key === 'localScreenShare' || key === 'remote'
                      || key === 'sortedRemoteParticipants' || key === 'sortedRemoteVirtualScreenshareParticipants'
                      || key === 'fakeParticipants' || key === 'overlaying' || key === 'raisedHandsQueue'
                      || key === 'risingHandQueue' || key === 'activeSpeakers' || key === 'dominantSpeaker'
                      || key === 'pinnedParticipant' || key === 'overwrittenNameList'
                      || key === 'numberOfNonModeratorParticipants' || key === 'numberOfParticipantsDisabledE2EE'
                      || key === 'numberOfParticipantsNotSupportingE2EE' || key === 'remoteVideoSources')
                      return;
                    addParticipant(byId, pstate[key], false);
                  });
                }
              }

              if (Object.keys(byId).length === 0 && APP.conference && typeof APP.conference.listMembers === 'function') {
                try {
                  APP.conference.listMembers().forEach(function (m) {
                    if (!m) return;
                    var id = String(m.id || (m.getId && m.getId()) || Math.random());
                    byId[id] = {
                      id: id,
                      name: (m.displayName || m.name || (m.getDisplayName && m.getDisplayName()) || 'Участник'),
                      avatar: (m.avatar || m.avatarUrl || ''),
                      local: !!(m.local || (APP.conference.getMyUserId && m.id === APP.conference.getMyUserId()))
                    };
                  });
                } catch (e) {}
              }

              if (Object.keys(byId).length <= 1 && APP.conference && APP.conference._room) {
                try {
                  var room = APP.conference._room;
                  if (typeof room.myUserId === 'function') {
                    var myId = String(room.myUserId());
                    if (!byId[myId]) {
                      byId[myId] = { id: myId, name: 'Вы', avatar: '', local: true };
                    }
                  }
                  var parts = room.participants || {};
                  Object.keys(parts).forEach(function (id) {
                    var p = parts[id];
                    if (!p) return;
                    byId[String(id)] = {
                      id: String(id),
                      name: (p._displayName || p.getDisplayName && p.getDisplayName() || 'Участник'),
                      avatar: '',
                      local: false
                    };
                  });
                } catch (e) {}
              }

              out.participants = Object.keys(byId).map(function (k) { return byId[k]; });
            } catch (e) {}
            return out;
          }

          function publish() {
            try {
              ensureChatSubscribe();
              if (typeof invokeCSharpAction === 'function')
                invokeCSharpAction(JSON.stringify(collect()));
            } catch (e) {}
          }

          function setInputValue(input, value) {
            if (!input) return;
            try {
              var proto = window.HTMLInputElement && window.HTMLInputElement.prototype;
              var descriptor = proto && Object.getOwnPropertyDescriptor(proto, 'value');
              if (descriptor && descriptor.set)
                descriptor.set.call(input, value);
              else
                input.value = value;
              input.dispatchEvent(new Event('input', { bubbles: true }));
              input.dispatchEvent(new Event('change', { bubbles: true }));
            } catch (e) {
              try { input.value = value; } catch (e2) {}
            }
          }

          function findRoomPasswordInput() {
            return document.querySelector('input[name="lockKey"]')
              || document.querySelector('#passwordRequiredInput')
              || document.querySelector('input[type="password"]');
          }

          function fillRoomPasswordIfNeeded() {
            try {
              var password = window.__alcoRatsRoomPassword;
              if (!password) return;
              var input = findRoomPasswordInput();
              if (!input) return;
              // Only fill while the dialog field is empty / not yet matching saved password.
              if (input.value === password) return;
              setInputValue(input, password);
            } catch (e) {}
          }

          window.__alcoRatsPublishConference = publish;
          window.__alcoRatsFillRoomPassword = fillRoomPasswordIfNeeded;
          window.__alcoRatsBridgeTimer = setInterval(publish, 1500);
          window.__alcoRatsPasswordTimer = setInterval(fillRoomPasswordIfNeeded, 400);
          setTimeout(publish, 300);
          setTimeout(fillRoomPasswordIfNeeded, 200);

          ensureChatSubscribe();

          try {
            if (!window.__alcoRatsPasswordObserver) {
              window.__alcoRatsPasswordObserver = new MutationObserver(function () {
                fillRoomPasswordIfNeeded();
              });
              window.__alcoRatsPasswordObserver.observe(document.documentElement || document.body, {
                childList: true,
                subtree: true
              });
            }
          } catch (e) {}

          return 'ok';
        })()
        """;

    public MainView(IConferenceSession conference, AppSettings settings, ILogger<MainView> logger): this()
    {
        _conference = conference;
        _settings = settings;
        _logger = logger;

        _toggleMicrophoneHandler = () => RunScriptAsync(ToggleMicScript);
        _toggleCameraHandler = () => RunScriptAsync(ToggleCamScript);
        _leaveHandler = LeaveAndResetAsync;
        _openChatHandler = () => RunScriptAsync(OpenChatScript);

        WireRoomWebView();

        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        _logger.LogDebug("MainView created");
    }

    public MainView()
    {
        InitializeComponent();
        SettingsOverlay.RenderTransform = _settingsTransform;
        EnsureRoomWebView();
    }

    private void EnsureRoomWebView()
    {
        if (Design.IsDesignMode)
            return;

        CreateRoomWebViewInstance();
    }

    private void CreateRoomWebViewInstance()
    {
        RoomWebView = new NativeWebView
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        WebViewHost.Children.Clear();
        WebViewHost.Children.Add(RoomWebView);
    }

    private void WireRoomWebView()
    {
        if (RoomWebView is null)
            return;

        RoomWebView.NavigationStarted += OnNavigationStarted;
        RoomWebView.NavigationCompleted += OnNavigationCompleted;
        RoomWebView.AdapterCreated += OnAdapterCreated;
        RoomWebView.WebMessageReceived += OnWebMessageReceived;
        WebViewPlatformSetup.Apply(RoomWebView);
    }

    private void UnwireRoomWebView()
    {
        if (RoomWebView is null)
            return;

        RoomWebView.NavigationStarted -= OnNavigationStarted;
        RoomWebView.NavigationCompleted -= OnNavigationCompleted;
        RoomWebView.AdapterCreated -= OnAdapterCreated;
        RoomWebView.WebMessageReceived -= OnWebMessageReceived;
    }

    /// <summary>
    /// Drops the native WebView so Jitsi SPA / media from a previous session cannot linger.
    /// Same-URL Navigate alone often leaves post-hangup UI and skips a real reconnect.
    /// </summary>
    private void RecreateRoomWebViewAndNavigateHome()
    {
        if (Design.IsDesignMode)
            return;

        UnwireRoomWebView();

        try
        {
            if (RoomWebView is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Previous WebView dispose failed");
        }

        WebViewHost.Children.Clear();
        _bridgeInstalled = false;
        _pageReady = false;

        CreateRoomWebViewInstance();
        WireRoomWebView();

        var url = WithHashParams(ResolveRoomUrl(), ("alcoRatsReset", Guid.NewGuid().ToString("N")[..8]));
        SetStatus("Открытие страницы подключения…");
        _logger.LogInformation("Recreated WebView and navigating to {Url}", url);

        try
        {
            RoomWebView.Source = url;
            RoomWebView.Navigate(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Navigate after WebView recreate failed");
            SetStatus("Ошибка: " + ex.Message);
        }
    }

    private async Task LeaveAndResetAsync()
    {
        try
        {
            await RunScriptAsync(LeaveScript);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Leave script failed");
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _conference.Publish(ConferenceSessionState.Inactive);
            UpdateConferenceActionButton(false);
            RecreateRoomWebViewAndNavigateHome();
        });
    }

    private void OnAdapterCreated(object? sender, EventArgs e)
        => WebViewPlatformSetup.Apply(RoomWebView);

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _conference.Register(
            _toggleMicrophoneHandler,
            _toggleCameraHandler,
            _leaveHandler,
            _openChatHandler);
        _conference.StateChanged += OnConferenceSessionStateChanged;
        UpdateConferenceActionButton(_conference.State.IsActive);
        WebViewPlatformSetup.Apply(RoomWebView);
        StartNavigation();
        _ = WatchdogAsync();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _conference.StateChanged -= OnConferenceSessionStateChanged;
        _conference.Unregister(
            _toggleMicrophoneHandler,
            _toggleCameraHandler,
            _leaveHandler,
            _openChatHandler);
        _bridgeInstalled = false;
    }

    private void OnConferenceSessionStateChanged(ConferenceSessionState state)
        => UpdateConferenceActionButton(state.IsActive);

    private void UpdateConferenceActionButton(bool active)
    {
        void Apply()
        {
            _conferenceActive = active;
            // Hang up is done from Jitsi toolbar; hide the whole status bar while in call.
            StatusPanel.IsVisible = !active;
            ConferenceActionButton.IsVisible = !active;
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private void OnConferenceActionClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_conferenceActive)
                return;

            NavigateToRoomHome();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Conference action button failed");
            SetStatus("Ошибка: " + ex.Message);
        }
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await OpenSettingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open settings failed");
            SetStatus("Ошибка: " + ex.Message);
        }
    }

    private async void OnSettingsBackRequested(object? sender, EventArgs e)
    {
        try
        {
            await CloseSettingsAsync(save: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Close settings failed");
            SetStatus("Ошибка: " + ex.Message);
        }
    }

    private async Task OpenSettingsAsync()
    {
        if (_settingsOpen || _settingsAnimating)
            return;

        _settingsAnimating = true;
        try
        {
            SettingsPage.DataContext = new SettingsViewModel(_settings);

            // Native WebView sits above Avalonia and can steal IME focus on Android.
            SetWebViewActive(false);

            var width = Bounds.Width;
            if (width <= 0)
                width = 400;

            _settingsTransform.X = width;
            SettingsOverlay.IsVisible = true;
            SettingsOverlay.IsHitTestVisible = true;

            await SlideSettingsAsync(from: width, to: 0);
            _settingsOpen = true;
            SettingsPage.Focus();
        }
        finally
        {
            _settingsAnimating = false;
        }
    }

    private async Task CloseSettingsAsync(bool save)
    {
        if (!_settingsOpen || _settingsAnimating)
            return;

        _settingsAnimating = true;
        try
        {
            if (SettingsPage.DataContext is SettingsViewModel vm)
            {
                vm.Settings.Save();
                _logger.LogInformation("Settings saved to {Path}", AppSettings.SettingsFilePath);
            }

            var width = Bounds.Width;
            if (width <= 0)
                width = 400;

            await SlideSettingsAsync(from: 0, to: width);

            SettingsOverlay.IsHitTestVisible = false;
            SettingsOverlay.IsVisible = false;
            _settingsTransform.X = 0;
            SetWebViewActive(true);
            _settingsOpen = false;
            await PushRoomPasswordAsync();
        }
        finally
        {
            _settingsAnimating = false;
        }
    }

    private void SetWebViewActive(bool active)
    {
        RoomWebView.IsVisible = active;
        RoomWebView.IsEnabled = active;
        RoomWebView.IsHitTestVisible = active;
        RoomWebView.IsTabStop = active;
        if (!active)
            RoomWebView.Focusable = false;
        else
            RoomWebView.Focusable = true;
    }

    private async Task SlideSettingsAsync(double from, double to)
    {
        // Animation.RunAsync on TranslateTransform hits TransformAnimator and throws;
        // DoubleTransition on X is the supported path for transform offsets.
        _settingsTransform.Transitions = null;
        _settingsTransform.X = from;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

        _settingsTransform.Transitions =
        [
            new DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = TimeSpan.FromMilliseconds(280),
                Easing = new CubicEaseOut()
            }
        ];

        _settingsTransform.X = to;
        await Task.Delay(280);
    }

    private void NavigateToRoomHome()
        => RecreateRoomWebViewAndNavigateHome();

    private void StartNavigation()
    {
        if (_navigationStarted)
            return;

        var url = ResolveRoomUrl();
        _navigationStarted = true;
        SetStatus("Открытие: " + url.Host + "…");
        _logger.LogInformation("Initial navigation to {Url}", url);

        try
        {
            RoomWebView.Source = url;
            RoomWebView.Navigate(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initial navigation failed");
            SetStatus("Ошибка: " + ex.Message);
        }
    }

    private Uri ResolveRoomUrl()
    {
        var url = (DataContext as MainViewModel)?.RoomUrl ?? MainViewModel.DefaultRoomUri;
        return ApplyRoomUrlConfig(url);
    }

    private Uri ApplyRoomUrlConfig(Uri url)
    {
        // Jitsi reads hash overrides: defaultLanguage forces RU UI regardless of browser locale.
        var parameters = new List<(string Key, string Value)>
        {
            ("config.defaultLanguage", "\"ru\"")
        };

        if (_settings.ToolbarAlwaysVisible)
        {
            parameters.Add(("config.toolbarConfig.alwaysVisible", "true"));
            parameters.Add(("interfaceConfig.TOOLBAR_ALWAYS_VISIBLE", "true"));
        }

        return WithHashParams(url, parameters.ToArray());
    }

    private static Uri WithHashParams(Uri url, params (string Key, string Value)[] parameters)
    {
        var builder = new UriBuilder(url);
        var fragment = (builder.Fragment ?? string.Empty).TrimStart('#');
        var parts = string.IsNullOrEmpty(fragment)
            ? new List<string>()
            : new List<string>(fragment.Split('&', StringSplitOptions.RemoveEmptyEntries));

        foreach (var (key, value) in parameters)
        {
            var assigned = false;
            for (var i = 0; i < parts.Count; i++)
            {
                var eq = parts[i].IndexOf('=');
                var name = eq >= 0 ? parts[i][..eq] : parts[i];
                if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                    continue;

                parts[i] = $"{key}={value}";
                assigned = true;
                break;
            }

            if (!assigned)
                parts.Add($"{key}={value}");
        }

        builder.Fragment = string.Join('&', parts);
        return builder.Uri;
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationEventArgs e)
    {
        _bridgeInstalled = false;
        SetStatus("Загрузка страницы…");
    }

    private async void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        _pageReady = e.IsSuccess;
        if (e.IsSuccess)
        {
            SetStatus("Ожидание подключения к конференции…");
            _conference.Publish(ConferenceSessionState.Inactive);
            UpdateConferenceActionButton(false);
            await InstallBridgeAsync();
            await PushRoomPasswordAsync();
        }
        else
        {
            SetStatus("Не удалось загрузить комнату.");
            _conference.Publish(ConferenceSessionState.Inactive);
            UpdateConferenceActionButton(false);
        }
    }

    private async Task InstallBridgeAsync()
    {
        if (!_pageReady)
            return;

        try
        {
            // Script is versioned/idempotent; safe to re-run after updates.
            await RoomWebView.InvokeScript(BridgeScript);
            _bridgeInstalled = true;
        }
        catch
        {
            // Retry on next poll attempt via watchdog.
        }
    }

    private async Task PushRoomPasswordAsync()
    {
        if (!_pageReady || RoomWebView is null)
            return;

        var password = _settings.RoomPassword ?? string.Empty;
        var escaped = JsonSerializer.Serialize(password);
        var script =
            $"(function(){{ window.__alcoRatsRoomPassword = {escaped}; " +
            "if (typeof window.__alcoRatsFillRoomPassword === 'function') window.__alcoRatsFillRoomPassword(); " +
            "return 'ok'; }})()";

        try
        {
            await RoomWebView.InvokeScript(script);
        }
        catch
        {
            // WebView may not be ready yet.
        }
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Body))
            return;

        try
        {
            using var doc = JsonDocument.Parse(e.Body);
            var root = doc.RootElement;
            var active = root.TryGetProperty("active", out var a) && a.GetBoolean();
            var audioMuted = root.TryGetProperty("audioMuted", out var am) && am.GetBoolean();
            var videoMuted = root.TryGetProperty("videoMuted", out var vm) && vm.GetBoolean();
            var unreadMessages = root.TryGetProperty("unreadMessages", out var um) && um.ValueKind == JsonValueKind.Number
                ? um.GetInt32()
                : 0;
            var participants = new List<ConferenceParticipant>();

            if (active
                && root.TryGetProperty("participants", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                    var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "Участник" : "Участник";
                    var avatar = item.TryGetProperty("avatar", out var avEl) ? avEl.GetString() : null;
                    var local = item.TryGetProperty("local", out var locEl) && locEl.GetBoolean();
                    participants.Add(new ConferenceParticipant(id, name, string.IsNullOrWhiteSpace(avatar) ? null : avatar, local));
                }
            }

            // Status + notification panel only while actually in the live call.
            if (!active)
            {
                _conference.Publish(ConferenceSessionState.Inactive);
                UpdateConferenceActionButton(false);
                SetStatus("Ожидание подключения к конференции…");
                return;
            }

            _conference.Publish(new ConferenceSessionState(true, audioMuted, videoMuted, participants, Math.Max(0, unreadMessages)));
            UpdateConferenceActionButton(true);
            SetStatus($"Участников: {participants.Count} · сообщений: {unreadMessages}");
        }
        catch
        {
            // Ignore malformed payloads from the page.
        }
    }

    private async Task WatchdogAsync()
    {
        await Task.Delay(15000);
        if (_pageReady)
        {
            if (!_bridgeInstalled)
            {
                await InstallBridgeAsync();
                await PushRoomPasswordAsync();
            }
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_pageReady)
                SetStatus("Ожидание...");
        });
    }

    private void SetStatus(string text)
    {
        if (Dispatcher.UIThread.CheckAccess())
            StatusText.Text = text;
        else
            Dispatcher.UIThread.Post(() => StatusText.Text = text);
    }

    private async Task RunScriptAsync(string script)
    {
        if (!_pageReady)
            return;

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                await RoomWebView.InvokeScript(script);
            }
            catch
            {
                // WebView may not be ready yet.
            }
        });
    }
}
