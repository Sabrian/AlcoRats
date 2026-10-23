using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AlcoRats.Hosting;
using AlcoRats.Services;
using AlcoRats.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlcoRats.Views;

public partial class MainView : UserControl
{
    private readonly Func<Task> _toggleMicrophoneHandler;
    private readonly Func<Task> _toggleCameraHandler;
    private readonly Func<Task> _leaveHandler;
    private readonly ILogger<MainView> _logger;
    private readonly IConferenceSession _conference;
    private bool _pageReady;
    private bool _navigationStarted;
    private bool _bridgeInstalled;
    private bool _conferenceActive;

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
          try {
            if (typeof APP !== 'undefined' && APP.conference && typeof APP.conference.hangup === 'function') {
              APP.conference.hangup();
              if (typeof window.__alcoRatsPublishConference === 'function')
                setTimeout(window.__alcoRatsPublishConference, 150);
              return 'ok';
            }
          } catch (e) {}
          try {
            var nodes = document.querySelectorAll('[aria-label], [data-testid]');
            for (var i = 0; i < nodes.length; i++) {
              var label = ((nodes[i].getAttribute('aria-label') || '') + ' ' + (nodes[i].getAttribute('data-testid') || '')).toLowerCase();
              if (label.indexOf('leave') >= 0 || label.indexOf('hangup') >= 0 || label.indexOf('покинуть') >= 0 || label.indexOf('заверш') >= 0) {
                nodes[i].click();
                if (typeof window.__alcoRatsPublishConference === 'function')
                  setTimeout(window.__alcoRatsPublishConference, 150);
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
          if (window.__alcoRatsBridgeVersion === 4) return 'exists';
          window.__alcoRatsBridgeVersion = 4;
          if (window.__alcoRatsBridgeTimer) clearInterval(window.__alcoRatsBridgeTimer);

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

          function collect() {
            var out = { active: false, audioMuted: false, videoMuted: false, participants: [] };
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
              if (typeof invokeCSharpAction === 'function')
                invokeCSharpAction(JSON.stringify(collect()));
            } catch (e) {}
          }

          window.__alcoRatsPublishConference = publish;
          window.__alcoRatsBridgeTimer = setInterval(publish, 1500);
          setTimeout(publish, 300);
          return 'ok';
        })()
        """;

    public MainView()
    {
        InitializeComponent();
        if (AppHost.IsInitialized)
        {
            _logger = AppHost.Services.GetRequiredService<ILogger<MainView>>();
            _conference = AppHost.Services.GetRequiredService<IConferenceSession>();
        }
        else
        {
            _logger = NullLogger<MainView>.Instance;
            _conference = null!;
        }

        _toggleMicrophoneHandler = () => RunScriptAsync(ToggleMicScript);
        _toggleCameraHandler = () => RunScriptAsync(ToggleCamScript);
        _leaveHandler = () => RunScriptAsync(LeaveScript);

        RoomWebView.NavigationStarted += OnNavigationStarted;
        RoomWebView.NavigationCompleted += OnNavigationCompleted;
        RoomWebView.AdapterCreated += OnAdapterCreated;
        RoomWebView.WebMessageReceived += OnWebMessageReceived;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        WebViewPlatformSetup.Apply(RoomWebView);
        _logger.LogDebug("MainView created");
    }

    private void OnAdapterCreated(object? sender, EventArgs e)
        => WebViewPlatformSetup.Apply(RoomWebView);

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _conference.Register(_toggleMicrophoneHandler, _toggleCameraHandler, _leaveHandler);
        _conference.StateChanged += OnConferenceSessionStateChanged;
        UpdateConferenceActionButton(_conference.State.IsActive);
        WebViewPlatformSetup.Apply(RoomWebView);
        StartNavigation();
        _ = WatchdogAsync();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _conference.StateChanged -= OnConferenceSessionStateChanged;
        _conference.Unregister(_toggleMicrophoneHandler, _toggleCameraHandler, _leaveHandler);
        _bridgeInstalled = false;
    }

    private void OnConferenceSessionStateChanged(ConferenceSessionState state)
        => UpdateConferenceActionButton(state.IsActive);

    private void UpdateConferenceActionButton(bool active)
    {
        void Apply()
        {
            _conferenceActive = active;
            HomeIcon.IsVisible = !active;
            HangupIcon.IsVisible = active;
            ConferenceActionButton.SetValue(
                ToolTip.TipProperty,
                active ? "Покинуть конференцию" : "На страницу подключения");
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private async void OnConferenceActionClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_conferenceActive)
            {
                // Optimistic UI — bridge will confirm after hangup.
                _logger.LogInformation("Leave conference requested from UI");
                UpdateConferenceActionButton(false);
                _conference.Publish(ConferenceSessionState.Inactive);
                SetStatus("Выход из конференции…");
                await _conference.LeaveAsync();
                return;
            }

            NavigateToRoomHome();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Conference action button failed");
            SetStatus("Ошибка: " + ex.Message);
        }
    }

    private void NavigateToRoomHome()
    {
        var url = (DataContext as MainViewModel)?.RoomUrl ?? MainViewModel.DefaultRoomUri;
        _bridgeInstalled = false;
        _pageReady = false;
        SetStatus("Открытие страницы подключения…");
        _logger.LogInformation("Navigating to room home: {Url}", url);

        try
        {
            RoomWebView.Navigate(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Navigate to room home failed");
            SetStatus("Ошибка: " + ex.Message);
        }
    }

    private void StartNavigation()
    {
        if (_navigationStarted)
            return;

        var url = (DataContext as MainViewModel)?.RoomUrl ?? MainViewModel.DefaultRoomUri;
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

            _conference.Publish(new ConferenceSessionState(true, audioMuted, videoMuted, participants));
            UpdateConferenceActionButton(true);
            SetStatus($"В конференции: {participants.Count}");
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
                await InstallBridgeAsync();
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
