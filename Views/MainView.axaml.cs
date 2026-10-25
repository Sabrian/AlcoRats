using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
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
          if (window.__alcoRatsBridgeVersion === 26) {
            try {
              if (typeof window.__alcoRatsFillCredentials === 'function')
                window.__alcoRatsFillCredentials();
              if (typeof window.__alcoRatsDismissDeepLink === 'function')
                window.__alcoRatsDismissDeepLink();
              if (typeof window.__alcoRatsEnterHomeFromWelcome === 'function')
                setTimeout(window.__alcoRatsEnterHomeFromWelcome, 0);
            } catch (eEx) {}
            return 'exists';
          }
          window.__alcoRatsBridgeVersion = 26;
          if (window.__alcoRatsBridgeTimer) clearInterval(window.__alcoRatsBridgeTimer);
          if (window.__alcoRatsPasswordTimer) clearInterval(window.__alcoRatsPasswordTimer);
          if (window.__alcoRatsUiFixTimer) {
            clearInterval(window.__alcoRatsUiFixTimer);
            window.__alcoRatsUiFixTimer = null;
          }
          // Allow re-subscribe after WebView/bridge reinstall.
          window.__alcoRatsChatSubscribed = false;

          function forceRussianUi() {
            try {
              if (window.localStorage) {
                localStorage.setItem('language', 'ru');
                localStorage.setItem('i18nextLng', 'ru');
                for (var i = localStorage.length - 1; i >= 0; i--) {
                  var key = localStorage.key(i);
                  if (!key) continue;
                  var raw = localStorage.getItem(key);
                  if (!raw || raw.indexOf('userSelectedLanguage') < 0) continue;
                  try {
                    var parsed = JSON.parse(raw);
                    var changed = false;
                    function patch(node) {
                      if (!node || typeof node !== 'object') return;
                      if (Object.prototype.hasOwnProperty.call(node, 'userSelectedLanguage')
                          && node.userSelectedLanguage !== 'ru') {
                        node.userSelectedLanguage = 'ru';
                        changed = true;
                      }
                      Object.keys(node).forEach(function (k) { patch(node[k]); });
                    }
                    patch(parsed);
                    if (changed) localStorage.setItem(key, JSON.stringify(parsed));
                  } catch (e1) {}
                }
              }
            } catch (e) {}
            try {
              if (window.i18next && typeof i18next.changeLanguage === 'function'
                  && i18next.language !== 'ru')
                i18next.changeLanguage('ru');
            } catch (e2) {}
            try {
              if (typeof APP !== 'undefined' && APP.store && typeof APP.store.dispatch === 'function') {
                APP.store.dispatch({ type: 'SETTINGS_UPDATED', settings: { userSelectedLanguage: 'ru' } });
                APP.store.dispatch({ type: 'UPDATE_SETTINGS', settings: { userSelectedLanguage: 'ru' } });
              }
            } catch (e3) {}
          }

          function isChatOpen() {
            try {
              if (typeof APP === 'undefined' || !APP.store) return false;
              var chat = APP.store.getState()['features/chat'] || {};
              return !!chat.isOpen;
            } catch (e) {
              return false;
            }
          }

          // Working baseline (pre panel-inset experiments): slight extra height for tiles,
          // hide toolbox on prejoin, no getUserMedia monkey-patch, no resize storms.
          var TILE_SCROLL_EXTRA_PX = 96;

          function ensureToolboxCss() {
            var style = document.getElementById('alcoRatsToolboxFix');
            if (!style) {
              style = document.createElement('style');
              style.id = 'alcoRatsToolboxFix';
              (document.head || document.documentElement).appendChild(style);
            }
            style.textContent = [
              'html.alcoRats-prejoin .new-toolbox,',
              'html.alcoRats-prejoin #new-toolbox {',
              '  display: none !important; visibility: hidden !important;',
              '  pointer-events: none !important;',
              '}',
              '.new-toolbox { transition: none !important; transform: none !important; }',
              '.toolbox-content-wrapper::after {',
              '  content: none !important; display: none !important;',
              '  padding: 0 !important; height: 0 !important;',
              '}',
              '.toolbox-content, .toolbox-content-mobile { margin-bottom: 0 !important; }',
              '.tile-view .filmstrip #remoteVideos,',
              '.tile-view .filmstrip.collapse #remoteVideos {',
              '  height: calc(100% + ' + TILE_SCROLL_EXTRA_PX + 'px) !important;',
              '  margin-bottom: 0 !important;',
              '}',
              '.tile-view .remote-videos,',
              '.tile-view .filmstrip.collapse .remote-videos {',
              '  overflow: hidden !important;',
              '  max-height: none !important;',
              '}',
              '.tile-view .filmstrip__videos.has-scroll { padding-left: 0 !important; }'
            ].join('\\n');
          }

          function expandTileScrollArea() {
            try {
              ensureToolboxCss();
              var nodes = document.querySelectorAll(
                '.tile-view .remote-videos, .tile-view .remote-videos > div, #remoteVideos');
              for (var i = 0; i < nodes.length; i++) {
                var el = nodes[i];
                if (!el || !el.style) continue;
                el.style.setProperty('overflow', 'hidden', 'important');
                el.style.setProperty('overflow-y', 'hidden', 'important');
              }
            } catch (e) {}
          }

          function pinToolboxToBottom() {
            try {
              ensureToolboxCss();
              var root = document.querySelector('.new-toolbox') || document.querySelector('#new-toolbox');
              var prejoin = typeof isPrejoinUiVisible === 'function' && isPrejoinUiVisible();
              var active = !prejoin
                && typeof isConferenceActive === 'function'
                && isConferenceActive();
              try {
                document.documentElement.classList.toggle('alcoRats-prejoin', !active);
              } catch (e0) {}

              if (!root) return;

              if (!active) {
                root.style.setProperty('display', 'none', 'important');
                root.style.setProperty('visibility', 'hidden', 'important');
                root.style.removeProperty('transform');
                return;
              }

              root.style.removeProperty('display');
              root.style.removeProperty('visibility');
              root.style.removeProperty('transform');
              if (isChatOpen()) return;

              root.style.setProperty('position', 'fixed', 'important');
              root.style.setProperty('left', '0', 'important');
              root.style.setProperty('right', '0', 'important');
              root.style.setProperty('bottom', '0', 'important');
              root.style.setProperty('width', '100%', 'important');
            } catch (e) {}
          }

          forceRussianUi();
          pinToolboxToBottom();
          expandTileScrollArea();
          if (!window.__alcoRatsUiFixTimer) {
            window.__alcoRatsUiFixTimer = setInterval(function () {
              pinToolboxToBottom();
              expandTileScrollArea();
            }, 1500);
          }
          try {
            window.addEventListener('resize', function () {
              pinToolboxToBottom();
              expandTileScrollArea();
            });
          } catch (e) {}

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

          // Prejoin / device-setup UI (Redux flags are often missing on custom deploys).
          function isPrejoinUiVisible() {
            try {
              var text = ((document.body && (document.body.innerText || document.body.textContent)) || '')
                .toLowerCase();
              if (!text) return false;
              if (text.indexOf('настраиваются устройства') >= 0
                  || text.indexOf('configuring your devices') >= 0
                  || text.indexOf('configuring devices') >= 0
                  || text.indexOf('присоединиться к встрече') >= 0
                  || text.indexOf('join meeting') >= 0
                  || text.indexOf('join the meeting') >= 0
                  || text.indexOf('пожалуйста, введите свое имя') >= 0
                  || text.indexOf('enter your name') >= 0)
                return true;
            } catch (e) {}
            return false;
          }

          // True only while inside the live call — not prejoin, lobby, or joining.
          function isConferenceActive() {
            try {
              if (isPrejoinUiVisible())
                return false;
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

          function isVisibleElement(el) {
            if (!el || !el.getBoundingClientRect) return false;
            try {
              var r = el.getBoundingClientRect();
              if (r.width < 2 || r.height < 2) return false;
              var s = window.getComputedStyle(el);
              if (!s || s.display === 'none' || s.visibility === 'hidden' || s.opacity === '0')
                return false;
              return true;
            } catch (e) {
              return false;
            }
          }

          function setInputValue(input, value) {
            if (!input) return;
            try {
              var lastValue = input.value;
              var proto = window.HTMLInputElement && window.HTMLInputElement.prototype;
              var descriptor = proto && Object.getOwnPropertyDescriptor(proto, 'value');
              if (descriptor && descriptor.set)
                descriptor.set.call(input, value);
              else
                input.value = value;

              // React controlled inputs ignore plain .value unless the tracker is reset.
              var tracker = input._valueTracker;
              if (tracker && typeof tracker.setValue === 'function')
                tracker.setValue(lastValue);

              try { input.focus(); } catch (eF) {}
              input.dispatchEvent(new Event('input', { bubbles: true }));
              input.dispatchEvent(new Event('change', { bubbles: true }));
              try {
                input.dispatchEvent(new InputEvent('input', {
                  bubbles: true,
                  data: value,
                  inputType: 'insertReplacementText'
                }));
              } catch (eIn) {}
            } catch (e) {
              try { input.value = value; } catch (e2) {}
            }
          }

          function pageVisibleText() {
            try {
              return ((document.body && (document.body.innerText || document.body.textContent)) || '')
                .toLowerCase();
            } catch (e) {
              return '';
            }
          }

          function inputMeta(el) {
            return (
              (el.name || '') + ' ' +
              (el.id || '') + ' ' +
              (el.placeholder || '') + ' ' +
              (el.getAttribute('aria-label') || '') + ' ' +
              (el.getAttribute('autocomplete') || '') + ' ' +
              (el.getAttribute('data-testid') || '')
            ).toLowerCase();
          }

          // Registered-user JWT/login dialog («Требуется авторизация»), not room lock.
          function isUserLoginDialogVisible() {
            var text = pageVisibleText();
            if (text.indexOf('требуется авторизация') >= 0
                || text.indexOf('authentication required') >= 0
                || text.indexOf('login required') >= 0
                || text.indexOf('идентификатор пользователя') >= 0
                || text.indexOf('пароль пользователя') >= 0
                || text.indexOf('user identifier') >= 0
                || text.indexOf('user password') >= 0)
              return true;

            // Heuristic: visible username-like field + password field together.
            try {
              var user = null, pass = null;
              var inputs = document.querySelectorAll('input');
              for (var i = 0; i < inputs.length; i++) {
                var el = inputs[i];
                if (!isVisibleElement(el)) continue;
                var type = (el.type || 'text').toLowerCase();
                var meta = inputMeta(el);
                if (type === 'password')
                  pass = el;
                else if (type === 'text' || type === 'email' || type === 'username' || type === '') {
                  if (meta.indexOf('user') >= 0
                      || meta.indexOf('login') >= 0
                      || meta.indexOf('ident') >= 0
                      || meta.indexOf('пользовател') >= 0
                      || meta.indexOf('логин') >= 0)
                    user = el;
                }
              }
              return !!(user && pass);
            } catch (e) {
              return false;
            }
          }

          function findUserLoginFields() {
            var user = null;
            var pass = null;
            try {
              var inputs = document.querySelectorAll('input');
              for (var i = 0; i < inputs.length; i++) {
                var el = inputs[i];
                if (!isVisibleElement(el)) continue;
                var type = (el.type || 'text').toLowerCase();
                var meta = inputMeta(el);
                if (type === 'password') {
                  if (!pass) pass = el;
                  continue;
                }
                if (type !== 'text' && type !== 'email' && type !== 'username' && type !== '')
                  continue;
                if (meta.indexOf('user') >= 0
                    || meta.indexOf('login') >= 0
                    || meta.indexOf('ident') >= 0
                    || meta.indexOf('пользовател') >= 0
                    || meta.indexOf('логин') >= 0
                    || meta.indexOf('name') >= 0) {
                  user = el;
                }
              }
              // Login dialog always has a password; username may be unlabeled — take first text.
              if (pass && !user) {
                for (var j = 0; j < inputs.length; j++) {
                  var t = inputs[j];
                  if (!isVisibleElement(t) || t === pass) continue;
                  var tt = (t.type || 'text').toLowerCase();
                  if (tt === 'text' || tt === 'email' || tt === 'username' || tt === '') {
                    user = t;
                    break;
                  }
                }
              }
            } catch (e) {}
            return { user: user, password: pass };
          }

          function findRoomPasswordInput() {
            // Never treat the registered-user login password as a room lock field.
            if (isUserLoginDialogVisible())
              return null;

            var selectors = [
              'input[name="lockKey"]',
              '#passwordRequiredInput',
              'input#modal-dialog-pass',
              '[data-testid*="passwordRequired"] input',
              '[data-testid*="PasswordRequired"] input',
              '.password-section input[type="password"]'
            ];
            var i, el;
            for (i = 0; i < selectors.length; i++) {
              try {
                var list = document.querySelectorAll(selectors[i]);
                for (var j = 0; j < list.length; j++) {
                  if (isVisibleElement(list[j]))
                    return list[j];
                }
              } catch (eSel) {}
            }

            // Fallback: lone visible password field (room lock), not a login form.
            try {
              var passwords = [];
              var inputs = document.querySelectorAll('input');
              for (i = 0; i < inputs.length; i++) {
                el = inputs[i];
                if (!isVisibleElement(el)) continue;
                var meta = inputMeta(el);
                var type = (el.type || '').toLowerCase();
                if (type === 'password'
                    || meta.indexOf('lockkey') >= 0
                    || meta.indexOf('passcode') >= 0
                    || meta.indexOf('room password') >= 0
                    || meta.indexOf('пароль комнат') >= 0)
                  passwords.push(el);
              }
              if (passwords.length === 1)
                return passwords[0];
            } catch (eAll) {}
            return null;
          }

          function fillRoomPasswordIfNeeded() {
            try {
              var password = window.__alcoRatsRoomPassword;
              if (!password) return;
              var input = findRoomPasswordInput();
              if (!input) return;
              if (input.value === password) return;
              setInputValue(input, password);
              setTimeout(function () {
                try {
                  var again = findRoomPasswordInput();
                  if (again && again.value !== password)
                    setInputValue(again, password);
                } catch (e2) {}
              }, 50);
            } catch (e) {}
          }

          function fillUserLoginIfNeeded() {
            try {
              if (!isUserLoginDialogVisible())
                return;
              var user = window.__alcoRatsUser || '';
              var password = window.__alcoRatsUserPassword || '';
              if (!user && !password)
                return;

              var fields = findUserLoginFields();
              if (user && fields.user && fields.user.value !== user)
                setInputValue(fields.user, user);
              if (password && fields.password && fields.password.value !== password)
                setInputValue(fields.password, password);

              setTimeout(function () {
                try {
                  if (!isUserLoginDialogVisible()) return;
                  var again = findUserLoginFields();
                  if (user && again.user && again.user.value !== user)
                    setInputValue(again.user, user);
                  if (password && again.password && again.password.value !== password)
                    setInputValue(again.password, password);
                } catch (e2) {}
              }, 50);
            } catch (e) {}
          }

          function fillCredentialsIfNeeded() {
            fillUserLoginIfNeeded();
            fillRoomPasswordIfNeeded();
          }

          // Fallback when config.disableDeepLinking is ignored by the deployment:
          // auto-press “Join in browser” / «Присоединиться в браузере».
          function dismissDeepLinkingPrompt() {
            try {
              var nodes = document.querySelectorAll('a, button, [role="button"], div[class*="button"]');
              for (var i = 0; i < nodes.length; i++) {
                var el = nodes[i];
                if (!isVisibleElement(el)) continue;
                var label = (
                  (el.innerText || el.textContent || '') + ' ' +
                  (el.getAttribute('aria-label') || '') + ' ' +
                  (el.getAttribute('href') || '')
                ).toLowerCase().replace(/\s+/g, ' ').trim();
                if (!label) continue;
                if (label.indexOf('join in browser') >= 0
                    || label.indexOf('continue in browser') >= 0
                    || label.indexOf('launch in web') >= 0
                    || label.indexOf('присоединиться в браузер') >= 0
                    || label.indexOf('продолжить в браузер') >= 0
                    || label.indexOf('открыть в браузер') >= 0
                    || label.indexOf('войти через браузер') >= 0) {
                  el.click();
                  return true;
                }
              }
            } catch (e) {}
            return false;
          }

          function isJitsiWelcomePage() {
            try {
              if (typeof isConferenceActive === 'function' && isConferenceActive())
                return false;
              if (findRoomPasswordInput())
                return false;

              var text = ((document.body && (document.body.innerText || document.body.textContent)) || '')
                .toLowerCase();
              if (text.indexOf('создать конференцию') >= 0
                  || text.indexOf('create meeting') >= 0
                  || text.indexOf('start meeting') >= 0
                  || text.indexOf('go') >= 0 && text.indexOf('enter the name') >= 0)
                return true;

              // Root path with a visible room-name field = welcome screen.
              var path = (location.pathname || '/').replace(/\/+$/, '') || '/';
              if (path === '/') {
                var inputs = document.querySelectorAll('input');
                for (var i = 0; i < inputs.length; i++) {
                  if (!isVisibleElement(inputs[i])) continue;
                  var t = (inputs[i].type || 'text').toLowerCase();
                  if (t === 'text' || t === 'search' || t === '')
                    return true;
                }
              }
            } catch (e) {}
            return false;
          }

          function trySubmitWelcomeWithRoomName(roomName) {
            try {
              var inputs = document.querySelectorAll('input');
              var field = null;
              for (var i = 0; i < inputs.length; i++) {
                if (!isVisibleElement(inputs[i])) continue;
                var t = (inputs[i].type || 'text').toLowerCase();
                if (t === 'text' || t === 'search' || t === '') {
                  field = inputs[i];
                  break;
                }
              }
              if (!field) return false;
              setInputValue(field, roomName);

              var buttons = document.querySelectorAll('button, a, [role="button"]');
              for (var j = 0; j < buttons.length; j++) {
                var b = buttons[j];
                if (!isVisibleElement(b)) continue;
                var label = ((b.innerText || b.textContent || '') + ' ' + (b.getAttribute('aria-label') || ''))
                  .toLowerCase();
                if (label.indexOf('создать конференцию') >= 0
                    || label.indexOf('create meeting') >= 0
                    || label.indexOf('start meeting') >= 0
                    || label.indexOf('go') === 0
                    || label === 'go') {
                  b.click();
                  return true;
                }
              }
            } catch (e) {}
            return false;
          }

          function enterHomeRoomFromWelcome() {
            try {
              if (window.__alcoRatsRedirectingHome) return;
              if (!isJitsiWelcomePage()) return;

              var homeUrl = window.__alcoRatsHomeRoomUrl;
              var roomName = window.__alcoRatsHomeRoomName || 'AlcoRats';
              if (!homeUrl && !roomName) return;

              window.__alcoRatsRedirectingHome = true;
              if (homeUrl) {
                try {
                  location.replace(homeUrl);
                  return;
                } catch (eNav) {}
              }
              if (!trySubmitWelcomeWithRoomName(roomName))
                window.__alcoRatsRedirectingHome = false;
            } catch (e) {
              window.__alcoRatsRedirectingHome = false;
            }
          }

          window.__alcoRatsPublishConference = publish;
          window.__alcoRatsFillRoomPassword = fillRoomPasswordIfNeeded;
          window.__alcoRatsFillCredentials = fillCredentialsIfNeeded;
          window.__alcoRatsDismissDeepLink = dismissDeepLinkingPrompt;
          window.__alcoRatsEnterHomeFromWelcome = enterHomeRoomFromWelcome;

          // Debounced credential/welcome pass — raw MutationObserver + body.innerText
          // on every React paint freezes the prejoin WebView («Настраиваются устройства…»).
          function scheduleLightDomPass() {
            if (window.__alcoRatsDomPassScheduled) return;
            window.__alcoRatsDomPassScheduled = true;
            setTimeout(function () {
              window.__alcoRatsDomPassScheduled = false;
              try {
                fillCredentialsIfNeeded();
                dismissDeepLinkingPrompt();
                enterHomeRoomFromWelcome();
              } catch (ePass) {}
            }, 700);
          }

          window.__alcoRatsBridgeTimer = setInterval(publish, 2000);
          if (window.__alcoRatsPasswordTimer) clearInterval(window.__alcoRatsPasswordTimer);
          window.__alcoRatsPasswordTimer = setInterval(scheduleLightDomPass, 1200);
          setTimeout(publish, 400);
          setTimeout(scheduleLightDomPass, 300);
          setTimeout(scheduleLightDomPass, 1200);

          ensureChatSubscribe();

          try {
            if (window.__alcoRatsPasswordObserver)
              window.__alcoRatsPasswordObserver.disconnect();
            window.__alcoRatsPasswordObserver = new MutationObserver(function () {
              scheduleLightDomPass();
            });
            // childList only — attribute (class/style) storms during prejoin device setup.
            window.__alcoRatsPasswordObserver.observe(document.documentElement || document.body, {
              childList: true,
              subtree: true
            });
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
            // Collapsing the Auto row shrinks WebViewHost so NativeWebView cannot cover the bar
            // when it is shown again (prejoin / welcome).
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
            _settingsOpen = true;

            await SlideSettingsAsync(from: width, to: 0);
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
        // Settings → URI is thread-safe; assigning DataContext/RoomUrl must stay on UI.
        var url = ApplyRoomUrlConfig(MainViewModel.BuildRoomUri(_settings.RoomName));
        void AssignVm()
        {
            if (DataContext is MainViewModel vm)
                vm.RoomUrl = url;
        }

        if (Dispatcher.UIThread.CheckAccess())
            AssignVm();
        else
            Dispatcher.UIThread.Post(AssignVm);

        return url;
    }

    private Uri ApplyRoomUrlConfig(Uri url)
    {
        // lang=ru is the supported override; defaultLanguage alone is often ignored once
        // a language was persisted in WebView localStorage.
        var parameters = new List<(string Key, string Value)>
        {
            ("lang", "ru"),
            ("config.defaultLanguage", "\"ru\""),
            // Skip mobile “open in app / join in browser” interstitial inside our WebView.
            ("config.disableDeepLinking", "true"),
            ("config.deeplinking.disabled", "true")
        };

        // Display name only — do not force prejoin/GUM/mute flags (those broke WebView join).
        if (!string.IsNullOrWhiteSpace(_settings.User))
        {
            var safeName = _settings.User.Trim().Replace("\"", string.Empty, StringComparison.Ordinal);
            parameters.Add(("userInfo.displayName", "\"" + safeName + "\""));
        }

        if (_settings.ToolbarAlwaysVisible)
        {
            parameters.Add(("config.toolbarConfig.alwaysVisible", "true"));
            parameters.Add(("interfaceConfig.TOOLBAR_ALWAYS_VISIBLE", "true"));
        }

        return WithHashParams(url, parameters.ToArray());
    }

    private static Uri WithHashParams(Uri url, params (string Key, string Value)[] parameters)
    {
        // Build fragment manually — UriBuilder percent-encodes quotes and breaks Jitsi parsers.
        var absolute = url.GetLeftPart(UriPartial.Path);
        var query = url.Query; // includes leading '?' or empty
        var fragment = (url.Fragment ?? string.Empty).TrimStart('#');
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

        var hash = string.Join('&', parts);
        return new Uri(string.IsNullOrEmpty(hash) ? absolute + query : absolute + query + "#" + hash, UriKind.Absolute);
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
            SetStatus("Подключение...");
            _conference.Publish(ConferenceSessionState.Inactive);
            UpdateConferenceActionButton(false);
            try
            {
                // Stay off the UI sync-context while waiting on WebView JS — otherwise
                // InvokeScript completion (UI callback) + ConfigureAwait(true) deadlocks.
                await InstallBridgeAsync().ConfigureAwait(false);
                await PushBridgeContextAsync().ConfigureAwait(false);
                SetStatus("Ожидание подключения к конференции…");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bridge install / context push failed");
            }
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
            // Longer budget: first install is large; a timeout must NOT mark installed.
            if (await InvokeScriptBoundedAsync(BridgeScript, timeoutMs: 8000).ConfigureAwait(false))
                _bridgeInstalled = true;
        }
        catch
        {
            // Retry on next poll attempt via watchdog.
        }
    }

    private async Task PushBridgeContextAsync()
    {
        if (!_pageReady || RoomWebView is null)
            return;

        var homeUrl = ResolveRoomUrl().ToString();
        var roomName = string.IsNullOrWhiteSpace(_settings.RoomName)
            ? MainViewModel.DefaultRoomName
            : _settings.RoomName.Trim();

        // Enter-home is deferred with setTimeout so evaluateJavascript can finish;
        // a sync location.replace cancels the JS result callback and hangs await forever.
        var script =
            "(function(){" +
            $"window.__alcoRatsRoomPassword = {JsonSerializer.Serialize(_settings.RoomPassword ?? string.Empty)};" +
            $"window.__alcoRatsUser = {JsonSerializer.Serialize(_settings.User ?? string.Empty)};" +
            $"window.__alcoRatsUserPassword = {JsonSerializer.Serialize(_settings.Password ?? string.Empty)};" +
            $"window.__alcoRatsHomeRoomUrl = {JsonSerializer.Serialize(homeUrl)};" +
            $"window.__alcoRatsHomeRoomName = {JsonSerializer.Serialize(roomName)};" +
            "if (typeof window.__alcoRatsFillCredentials === 'function') window.__alcoRatsFillCredentials();" +
            "else if (typeof window.__alcoRatsFillRoomPassword === 'function') window.__alcoRatsFillRoomPassword();" +
            "if (typeof window.__alcoRatsEnterHomeFromWelcome === 'function')" +
            "  setTimeout(window.__alcoRatsEnterHomeFromWelcome, 0);" +
            "return 'ok';" +
            "})()";

        try
        {
            await InvokeScriptBoundedAsync(script).ConfigureAwait(false);
        }
        catch
        {
            // WebView may not be ready yet.
        }
    }

    /// <summary>
    /// Runs WebView JS with a real timeout that cannot deadlock the UI dispatcher.
    /// Avalonia Android completes InvokeScript via a UI-thread callback; awaiting it on
    /// that same thread with ConfigureAwait(true) freezes WhenAny (Delay never resumes).
    /// </summary>
    /// <returns>True if the script completed; false on timeout.</returns>
    private async Task<bool> InvokeScriptBoundedAsync(string script, int timeoutMs = 4000)
    {
        if (RoomWebView is null)
            return false;

        var webView = RoomWebView;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void StartInvoke()
        {
            try
            {
                var task = webView.InvokeScript(script);
                task.ContinueWith(
                    static (t, state) =>
                    {
                        var source = (TaskCompletionSource<bool>)state!;
                        if (t.IsFaulted)
                            source.TrySetException(t.Exception!.GetBaseException());
                        else if (t.IsCanceled)
                            source.TrySetCanceled();
                        else
                            source.TrySetResult(true);
                    },
                    tcs,
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        // Always schedule InvokeScript through the dispatcher queue so we never call it
        // while the UI thread is blocked inside an await on the same sync context.
        if (Dispatcher.UIThread.CheckAccess())
            Dispatcher.UIThread.Post(StartInvoke, DispatcherPriority.Background);
        else
            await Dispatcher.UIThread.InvokeAsync(StartInvoke);

        var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (finished != tcs.Task)
        {
            _logger.LogWarning("InvokeScript timed out after {Timeout}ms", timeoutMs);
            return false;
        }

        await tcs.Task.ConfigureAwait(false);
        return true;
    }

    private Task PushRoomPasswordAsync()
        => PushBridgeContextAsync();

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

        try
        {
            _ = await InvokeScriptBoundedAsync(script, timeoutMs: 3000).ConfigureAwait(false);
        }
        catch
        {
            // WebView may not be ready yet.
        }
    }
}
