using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlcoRats.Bot.Models;
using AlcoRats.Bot.Speech;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

namespace AlcoRats.Bot.Jitsi;

/// <summary>
/// Jitsi Meet bot via Chromium (port of <c>Jitsi/Js/bot.js</c>).
/// Prefers attaching to an already running browser on the configured debugging port.
/// Always signs in with Secure Domain credentials (not anonymous).
/// </summary>
sealed class JitsiBotService : IJitsiBotService, IAsyncDisposable
{
    private static readonly string[] DefaultChromiumPaths =
    [
        "/usr/bin/chromium-browser",
        "/usr/bin/chromium",
        "/usr/bin/google-chrome",
        "/usr/bin/google-chrome-stable",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Configuration _configuration;
    private readonly ILogger<JitsiBotService> _logger;
    private readonly PiperTtsSynthesizer _tts;
    private readonly IDomSnapshotStore _domSnapshots;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _speakGate = new(1, 1);

    private IBrowser _browser;
    private IPage _page;
    private DateTime? _joinedAt;
    private bool _ownsBrowser;
    private bool _starting;
    private bool _disposed;

    private CancellationTokenSource _chatCts;
    private Task _chatMonitorTask;
    private readonly HashSet<string> _seenChatMessageIds = new(StringComparer.Ordinal);
    private bool _chatBaselineReady;

    // id -> display name (real users only; screen-share stubs never enter this map)
    private readonly Dictionary<string, string> _knownParticipants = new(StringComparer.Ordinal);
    private bool _participantsBaselineReady;

    public JitsiBotService(
        Configuration configuration,
        ILogger<JitsiBotService> logger,
        PiperTtsSynthesizer tts,
        IDomSnapshotStore domSnapshots)
    {
        _configuration = configuration;
        _logger = logger;
        _tts = tts;
        _domSnapshots = domSnapshots;

        ThreadPool.QueueUserWorkItem(async (state) =>
        {
            while(!_disposed)
            {
                Thread.Sleep(15000);
                if (!_starting)
                {
                    await EnsureStartedAsync();
                }
            }
        });
    }

    public bool IsRunning => _starting || _browser is { IsClosed: false };

    public event EventHandler<ChatMessage> ChatMessageReceived;

    public event EventHandler<RoomParticipant> UserConnected;

    public event EventHandler<RoomParticipant> UserDisconnected;

    public Task EnsureStartedAsync(CancellationToken cancellationToken = default) => StartAsync(cancellationToken);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureCredentialsConfigured();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_starting)
            {
                return;
            }

            if (_browser is { IsClosed: false } && _page != null)
            {
                if (await IsJoinedAsync(_page))
                {
                    _joinedAt ??= DateTime.UtcNow;
                    return;
                }

                _starting = true;
                try
                {
                    await JoinConferenceAsync(_page, cancellationToken);
                }
                finally
                {
                    _starting = false;
                }

                return;
            }

            _starting = true;

            var host = NormalizeHost(_configuration.JitsiHost);
            var room = (_configuration.JitsiRoom ?? string.Empty).Trim().Trim('/');
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(room))
                throw new InvalidOperationException("JitsiHost and JitsiRoom must be configured.");

            _logger.LogInformation("Starting Jitsi bot...");

            if (!await TryAttachExistingBrowserAsync(host, cancellationToken))
                await LaunchBrowserAsync(cancellationToken);

            _page = await ResolvePageAsync(host);
            await PrepareProfileAsync(_page, host, cancellationToken);

            if (await IsJoinedAsync(_page))
            {
                // DOM dump showed attach lands already in-room: localStorage alone does not
                // refresh avatar/email — push profile into the live conference.
                await ApplyLiveProfileAsync(_page, cancellationToken);
                _joinedAt ??= DateTime.UtcNow;
                _logger.LogInformation("Attached to existing joined Jitsi session");
                return;
            }

            await JoinConferenceAsync(_page, cancellationToken);
        }
        catch
        {
            await DisposeBrowserAsync();
            throw;
        }
        finally
        {
            _starting = false;
            _gate.Release();

            // Must run here: early `return` inside try skips code after try/finally.
            if (!_disposed && _browser is { IsClosed: false } && _page != null)
                EnsureChatMonitor();
        }
    }

    public async Task<Room> GetRoom()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var roomName = (_configuration.JitsiRoom ?? string.Empty).Trim();

        if (_page == null || _browser is not { IsClosed: false })
        {
            return new Room
            {
                Name = roomName,
                Online = TimeSpan.Zero,
                Members = Array.Empty<Member>(),
            };
        }

        await _gate.WaitAsync();
        try
        {
            if (_page == null || _browser is not { IsClosed: false })
            {
                return new Room
                {
                    Name = roomName,
                    Online = TimeSpan.Zero,
                    Members = Array.Empty<Member>(),
                };
            }

            var snapshot = await CollectRoomSnapshotAsync(_page);
            var members = snapshot?.Participants?
                .Where(p => !string.IsNullOrWhiteSpace(p?.Name))
                .Select(p => new Member { Name = p.Name.Trim() })
                .ToArray()
                ?? Array.Empty<Member>();

            var online = ResolveRoomOnline(snapshot);
            if (online == TimeSpan.Zero)
            {
                _logger.LogDebug(
                    "Room online is 0 (appPresent={AppPresent}, conferenceTimestampMs={Ts}, onlineMs={OnlineMs}, confKeys={Keys})",
                    snapshot?.AppPresent,
                    snapshot?.ConferenceTimestampMs,
                    snapshot?.OnlineMs,
                    snapshot?.ConfKeys is { Length: > 0 } ? string.Join(",", snapshot.ConfKeys) : "-");
            }

            return new Room
            {
                Name = roomName,
                Online = online,
                Members = members,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Jitsi room participants");
            return new Room
            {
                Name = roomName,
                Online = TimeSpan.Zero,
                Members = Array.Empty<Member>(),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RoomSnapshot> CollectRoomSnapshotAsync(IPage page)
    {
        var json = await EvaluateInMainWorldAsync(page, CollectParticipantsScript);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        return JsonSerializer.Deserialize<RoomSnapshot>(json, JsonOptions);
    }

    /// <summary>
    /// Runs a <c>function() { ... }</c> in the page main world (sees <c>window.APP</c>)
    /// via a temporary script tag; result is passed back through a DOM attribute.
    /// </summary>
    private static async Task<string> EvaluateInMainWorldAsync(IPage page, string functionSource)
    {
        return await page.EvaluateFunctionAsync<string>(
            """
            (fnSource) => {
                const attr = 'data-ar-eval-result';
                document.documentElement.removeAttribute(attr);
                const script = document.createElement('script');
                script.textContent =
                    '(function(){try{var __arFn=(' + fnSource + ');var __arResult=__arFn();' +
                    'document.documentElement.setAttribute("' + attr + '",' +
                    '(typeof __arResult===\'string\')?__arResult:JSON.stringify(__arResult));' +
                    '}catch(e){document.documentElement.setAttribute("' + attr + '",' +
                    'JSON.stringify({error:String(e&&e.message||e),messages:[],participants:[],appPresent:false}));}})();';
                document.documentElement.appendChild(script);
                script.remove();
                return document.documentElement.getAttribute(attr) || '';
            }
            """,
            functionSource);
    }

    /// <summary>
    /// Runs an <c>async function(arg) { ... }</c> in the page main world and waits for the result attribute.
    /// </summary>
    private static async Task<string> EvaluateInMainWorldAsyncAsync(IPage page, string asyncFunctionSource, object arg)
    {
        return await page.EvaluateFunctionAsync<string>(
            """
            async (fnSource, arg) => {
                const attr = 'data-ar-eval-async-result';
                document.documentElement.removeAttribute(attr);
                const script = document.createElement('script');
                const argJson = JSON.stringify(arg);
                script.textContent =
                    '(async function(){try{var __arFn=(' + fnSource + ');var __arArg=' + argJson + ';' +
                    'var __arResult=await __arFn(__arArg);' +
                    'document.documentElement.setAttribute("' + attr + '",' +
                    '(typeof __arResult===\'string\')?__arResult:JSON.stringify(__arResult));' +
                    '}catch(e){document.documentElement.setAttribute("' + attr + '",' +
                    'JSON.stringify({ok:false,error:String(e&&e.message||e)}));}})();';
                document.documentElement.appendChild(script);
                script.remove();
                const deadline = Date.now() + 180000;
                while (Date.now() < deadline) {
                    const v = document.documentElement.getAttribute(attr);
                    if (v != null) return v;
                    await new Promise(r => setTimeout(r, 50));
                }
                return JSON.stringify({ ok: false, error: 'timeout' });
            }
            """,
            asyncFunctionSource,
            arg);
    }

    private static TimeSpan ResolveRoomOnline(RoomSnapshot snapshot)
    {
        // Prefer explicit duration already computed in-page (DOM timer / Redux).
        if (snapshot?.OnlineMs is > 0)
            return TimeSpan.FromMilliseconds(snapshot.OnlineMs.Value);

        if (snapshot?.ConferenceTimestampMs is > 0)
        {
            var started = DateTimeOffset.FromUnixTimeMilliseconds(snapshot.ConferenceTimestampMs.Value);
            var online = DateTimeOffset.UtcNow - started;
            return online > TimeSpan.Zero ? online : TimeSpan.Zero;
        }

        return TimeSpan.Zero;
    }

    public async Task SendMessage(string message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Message must not be empty.", nameof(message));

        await EnsureStartedAsync();

        await _gate.WaitAsync();
        try
        {
            if (_page == null || _browser is not { IsClosed: false })
                throw new InvalidOperationException("Jitsi browser is not available.");

            if (!await IsJoinedAsync(_page))
                throw new InvalidOperationException("Jitsi bot is not in the conference.");

            var textJson = JsonSerializer.Serialize(message);
            var script =
                $$"""
                function() {
                    var text = {{textJson}};
                    if (!text || !String(text).trim())
                        return JSON.stringify({ ok: false, error: 'empty' });

                    try {
                        var app = window.APP;
                        if (!app)
                            return JSON.stringify({ ok: false, error: 'no-app' });

                        // Prefer lib-jitsi-meet conference from Redux.
                        var conf = null;
                        try {
                            if (app.store && typeof app.store.getState === 'function') {
                                var state = app.store.getState();
                                var base = state['features/base/conference'] || {};
                                conf = base.conference || null;
                            }
                        } catch (eState) {}

                        if (!conf && app.conference) {
                            if (typeof app.conference.sendTextMessage === 'function')
                                conf = app.conference;
                            else if (app.conference._room)
                                conf = app.conference._room;
                        }

                        if (!conf)
                            return JSON.stringify({ ok: false, error: 'no-conference' });

                        if (typeof conf.sendTextMessage === 'function') {
                            conf.sendTextMessage(String(text));
                            return JSON.stringify({ ok: true, via: 'sendTextMessage' });
                        }

                        if (typeof conf.sendMessage === 'function') {
                            conf.sendMessage(String(text));
                            return JSON.stringify({ ok: true, via: 'sendMessage' });
                        }

                        return JSON.stringify({ ok: false, error: 'no-send-api' });
                    } catch (e) {
                        return JSON.stringify({ ok: false, error: String(e && e.message || e) });
                    }
                }
                """;

            var json = await EvaluateInMainWorldAsync(_page, script);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
            if (!ok)
            {
                var error = root.TryGetProperty("error", out var errProp)
                    ? errProp.GetString()
                    : "unknown";
                throw new InvalidOperationException($"Failed to send Jitsi chat message: {error}");
            }

            _logger.LogInformation("Sent Jitsi chat message ({Length} chars)", message.Length);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text must not be empty.", nameof(text));

        await EnsureStartedAsync(cancellationToken);

        await _speakGate.WaitAsync(cancellationToken);
        string wavPath = null;
        try
        {
            wavPath = await _tts.SynthesizeToWavFileAsync(text, cancellationToken);
            var wavBytes = await File.ReadAllBytesAsync(wavPath, cancellationToken);
            var wavBase64 = Convert.ToBase64String(wavBytes);

            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_page == null || _browser is not { IsClosed: false })
                    throw new InvalidOperationException("Jitsi browser is not available.");

                if (!await IsJoinedAsync(_page))
                    throw new InvalidOperationException("Jitsi bot is not in the conference.");

                var json = await EvaluateInMainWorldAsyncAsync(_page, PublishWavToConferenceScript, wavBase64);
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var root = doc.RootElement;
                var ok = root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
                if (!ok)
                {
                    var error = root.TryGetProperty("error", out var errProp)
                        ? errProp.GetString()
                        : "unknown";
                    throw new InvalidOperationException($"Failed to publish Piper WAV to Jitsi: {error}");
                }

                var durationMs = root.TryGetProperty("durationMs", out var durProp) && durProp.TryGetDouble(out var d)
                    ? d
                    : 0;
                _logger.LogInformation(
                    "Spoke via Piper ({Engine}), textLength={Length}, durationMs={DurationMs:0}",
                    _tts.EngineName,
                    text.Length,
                    durationMs);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            if (wavPath != null)
            {
                try { File.Delete(wavPath); } catch { /* ignore */ }
            }

            _speakGate.Release();
        }
    }

    public async Task StopAsync()
    {
        if (_disposed)
            return;

        await StopChatMonitorAsync();

        await _gate.WaitAsync();
        try
        {
            await DisposeBrowserAsync();
            _logger.LogInformation("Jitsi bot stopped");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureChatMonitor()
    {
        if (_disposed || _page == null || _browser is not { IsClosed: false })
            return;

        if (_chatMonitorTask is { IsCompleted: false })
            return;

        _chatCts = new CancellationTokenSource();
        var token = _chatCts.Token;
        _chatMonitorTask = Task.Run(() => MonitorChatAsync(token), token);
        _logger.LogInformation("Jitsi chat monitor started");
    }

    private async Task StopChatMonitorAsync()
    {
        var cts = _chatCts;
        var task = _chatMonitorTask;
        _chatCts = null;
        _chatMonitorTask = null;

        if (cts == null)
            return;

        try
        {
            cts.Cancel();
            if (task != null)
            {
                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or AggregateException)
                {
                    // ignore shutdown races
                }
            }
        }
        finally
        {
            cts.Dispose();
            _seenChatMessageIds.Clear();
            _chatBaselineReady = false;
            _knownParticipants.Clear();
            _participantsBaselineReady = false;
        }
    }

    private async Task MonitorChatAsync(CancellationToken cancellationToken)
    {
        // History / roster often settle a few seconds after join; absorb without raising events.
        var absorbUntil = DateTime.UtcNow.AddSeconds(8);
        var joinCutoff = (_joinedAt ?? DateTime.UtcNow).AddSeconds(-5);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                IPage page;
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    page = _page;
                    if (page == null || _browser is not { IsClosed: false })
                        break;
                }
                finally
                {
                    _gate.Release();
                }

                // Ensure main-world hook (APP.store.subscribe + conference.messageReceived).
                await EvaluateInMainWorldAsync(page, InstallChatHookScript);

                var json = await EvaluateInMainWorldAsync(page, CollectChatMessagesScript);
                var snapshot = string.IsNullOrWhiteSpace(json)
                    ? null
                    : JsonSerializer.Deserialize<ChatSnapshot>(json, JsonOptions);

                if (snapshot?.Error is { Length: > 0 })
                    _logger.LogWarning("Chat collect error: {Error}", snapshot.Error);

                var messages = snapshot?.Messages ?? Array.Empty<ChatMessageDto>();
                var absorbing = DateTime.UtcNow < absorbUntil;

                if (!_chatBaselineReady || absorbing)
                {
                    foreach (var message in messages)
                    {
                        if (!string.IsNullOrWhiteSpace(message?.Id))
                            _seenChatMessageIds.Add(message.Id);
                    }

                    if (!_chatBaselineReady)
                    {
                        _chatBaselineReady = true;
                        _logger.LogInformation(
                            "Chat baseline captured ({Count} messages, appPresent={AppPresent}, hooked={Hooked}); absorbing history until {AbsorbUntil:u}",
                            _seenChatMessageIds.Count,
                            snapshot?.AppPresent,
                            snapshot?.Hooked,
                            absorbUntil);
                    }
                }
                else
                {
                    foreach (var message in messages)
                    {
                        if (message == null || string.IsNullOrWhiteSpace(message.Id))
                            continue;
                        if (!_seenChatMessageIds.Add(message.Id))
                            continue;
                        if (string.IsNullOrWhiteSpace(message.Text))
                            continue;

                        var timestamp = message.TimestampMs is > 0
                            ? DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampMs.Value)
                            : DateTimeOffset.UtcNow;

                        // Drop pre-join / history messages that slipped past baseline with new ids.
                        if (timestamp < joinCutoff)
                            continue;

                        var chatMessage = new ChatMessage
                        {
                            Id = message.Id,
                            Text = message.Text.Trim(),
                            Sender = string.IsNullOrWhiteSpace(message.Sender) ? "Unknown" : message.Sender.Trim(),
                            Timestamp = timestamp,
                            IsLocal = message.Local,
                        };

                        _logger.LogInformation(
                            "Jitsi chat message from {Sender}: {Text}",
                            chatMessage.Sender,
                            chatMessage.Text);

                        try
                        {
                            ChatMessageReceived?.Invoke(this, chatMessage);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "ChatMessageReceived handler failed");
                        }
                    }
                }

                await ProcessParticipantPresenceAsync(page, absorbing);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Chat monitor tick failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Jitsi chat monitor stopped");
    }

    private async Task ProcessParticipantPresenceAsync(IPage page, bool absorbing)
    {
        var room = await CollectRoomSnapshotAsync(page);
        var current = new Dictionary<string, ParticipantSnapshot>(StringComparer.Ordinal);

        foreach (var p in room?.Participants ?? Array.Empty<ParticipantSnapshot>())
        {
            if (p == null || string.IsNullOrWhiteSpace(p.Id))
                continue;
            if (p.Local)
                continue;
            if (IsBotParticipantName(p.Name))
                continue;

            var name = string.IsNullOrWhiteSpace(p.Name) ? "Участник" : p.Name.Trim();
            current[p.Id] = new ParticipantSnapshot { Id = p.Id, Name = name, Local = false };
        }

        if (!_participantsBaselineReady || absorbing)
        {
            foreach (var (id, p) in current)
                _knownParticipants[id] = p.Name;

            // Drop anyone who disappeared during absorb without announcing.
            foreach (var id in _knownParticipants.Keys.Except(current.Keys).ToArray())
                _knownParticipants.Remove(id);

            if (!_participantsBaselineReady)
            {
                _participantsBaselineReady = true;
                _logger.LogInformation(
                    "Participant baseline captured ({Count} users); absorbing roster until settle",
                    _knownParticipants.Count);
            }

            return;
        }

        foreach (var (id, p) in current)
        {
            if (_knownParticipants.TryGetValue(id, out var existingName))
            {
                if (!string.Equals(existingName, p.Name, StringComparison.Ordinal))
                    _knownParticipants[id] = p.Name;
                continue;
            }

            _knownParticipants[id] = p.Name;
            var joined = new RoomParticipant { Id = id, Name = p.Name };
            _logger.LogInformation("User connected: {Name} ({Id})", joined.Name, joined.Id);
            try
            {
                UserConnected?.Invoke(this, joined);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserConnected handler failed");
            }
        }

        foreach (var id in _knownParticipants.Keys.Except(current.Keys).ToArray())
        {
            var name = _knownParticipants[id];
            _knownParticipants.Remove(id);
            var left = new RoomParticipant { Id = id, Name = name };
            _logger.LogInformation("User disconnected: {Name} ({Id})", left.Name, left.Id);
            try
            {
                UserDisconnected?.Invoke(this, left);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserDisconnected handler failed");
            }
        }
    }

    private bool IsBotParticipantName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var n = name.Trim();
        if (!string.IsNullOrWhiteSpace(_configuration.BotName)
            && string.Equals(n, _configuration.BotName.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(_configuration.JitsiUserLogin)
            && string.Equals(n, _configuration.JitsiUserLogin.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await StopAsync();
        _disposed = true;
        _gate.Dispose();
    }

    private void EnsureCredentialsConfigured()
    {
        if (string.IsNullOrWhiteSpace(_configuration.JitsiUserLogin)
            || string.IsNullOrWhiteSpace(_configuration.JitsiUserPassword))
        {
            throw new InvalidOperationException(
                "JitsiUserLogin and JitsiUserPassword are required — anonymous join is not allowed.");
        }
    }

    private async Task<bool> TryAttachExistingBrowserAsync(string host, CancellationToken cancellationToken)
    {
        var port = _configuration.JitsiRemoteDebuggingPort;
        if (port <= 0)
            return false;

        cancellationToken.ThrowIfCancellationRequested();

        var browserUrl = $"http://127.0.0.1:{port}";
        try
        {
            // No HttpClient probe — its Timeout surfaces as TaskCanceledException and
            // looks like a crash in the debugger when nothing is listening on the port.
            _browser = await Puppeteer.ConnectAsync(new ConnectOptions
            {
                BrowserURL = browserUrl,
                DefaultViewport = null,
            });
            _ownsBrowser = false;
            _logger.LogInformation("Connected to existing Chromium at {BrowserUrl}", browserUrl);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "No existing Chromium at {BrowserUrl}", browserUrl);
            _browser = null;
            return false;
        }
    }

    private async Task LaunchBrowserAsync(CancellationToken cancellationToken)
    {
        var port = _configuration.JitsiRemoteDebuggingPort > 0
            ? _configuration.JitsiRemoteDebuggingPort
            : 9333;

        var userDataDir = ResolveUserDataDir();
        Directory.CreateDirectory(userDataDir);

        var executablePath = await ResolveChromiumPathAsync(cancellationToken);
        var args = new List<string>
        {
            "--use-fake-ui-for-media-stream",
            "--use-fake-device-for-media-stream",
            "--mute-audio",
            "--autoplay-policy=no-user-gesture-required",
            "--no-sandbox",
            "--disable-setuid-sandbox",
            "--disable-dev-shm-usage",
            "--disable-gpu",
            "--disable-logging",
            "--log-level=3",
            $"--remote-debugging-port={port}",
            $"--user-data-dir={userDataDir}",
        };

        var launchOptions = new LaunchOptions
        {
            Headless = _configuration.JitsiHeadless,
            Args = args.ToArray(),
            DefaultViewport = null,
            // false: do not forward Chromium stderr (DBus/ALSA/DevTools spam) to our console
            DumpIO = false,
        };

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            launchOptions.ExecutablePath = executablePath;
            _logger.LogInformation("Using Chromium at {ExecutablePath}", executablePath);
        }

        try
        {
            _browser = await Puppeteer.LaunchAsync(launchOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Chromium launch failed (executable={ExecutablePath}). Inner: {Inner}",
                executablePath,
                ex.InnerException?.Message ?? "(none)");
            throw;
        }
        _ownsBrowser = true;
        _logger.LogInformation(
            "Launched Chromium (debugging port {Port}, profile {UserDataDir})",
            port,
            userDataDir);
    }

    private async Task<IPage> ResolvePageAsync(string host)
    {
        var pages = await _browser.PagesAsync();
        foreach (var page in pages)
        {
            try
            {
                var url = page.Url ?? string.Empty;
                if (url.StartsWith(host, StringComparison.OrdinalIgnoreCase)
                    || url.Contains("jitsi", StringComparison.OrdinalIgnoreCase))
                    return page;
            }
            catch
            {
                // ignore dead pages
            }
        }

        return pages.FirstOrDefault() ?? await _browser.NewPageAsync();
    }

    private async Task PrepareProfileAsync(IPage page, string host, CancellationToken cancellationToken)
    {
        var botName = _configuration.BotName ?? string.Empty;
        var email = (_configuration.Gravatar ?? string.Empty).Trim();
        var avatarUrl = BuildGravatarAvatarUrl(email);

        // If already in-conference (attach path), do not navigate away — apply live.
        if (await IsJoinedAsync(page))
        {
            await WriteProfileToLocalStorageAsync(page, botName, email, avatarUrl);
            await ApplyLiveProfileAsync(page, cancellationToken);
            await _domSnapshots.SaveAsync(page, "01-already-joined-profile", cancellationToken);
            return;
        }

        // Always open origin root first so room load reads our localStorage (DOM showed
        // dump on /alcorats with a foreign gravatar hash — storage was never applied).
        await page.GoToAsync(host, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = 60_000,
        });

        await WriteProfileToLocalStorageAsync(page, botName, email, avatarUrl);

        _logger.LogInformation(
            "Bot profile cached: {BotName} email={Email} avatar={Avatar}",
            botName,
            string.IsNullOrEmpty(email) ? "(none)" : email,
            string.IsNullOrEmpty(avatarUrl) ? "(none)" : avatarUrl);
        await _domSnapshots.SaveAsync(page, "01-host-profile", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task WriteProfileToLocalStorageAsync(
        IPage page,
        string botName,
        string email,
        string avatarUrl)
    {
        await page.EvaluateFunctionAsync(
            """
            (name, email, avatarUrl) => {
                localStorage.setItem('features/base/profile', JSON.stringify({
                    displayName: name,
                    email: email,
                    avatarURL: avatarUrl || undefined
                }));
                try {
                    var settingsRaw = localStorage.getItem('features/base/settings');
                    var settings = settingsRaw ? JSON.parse(settingsRaw) : {};
                    if (!settings || typeof settings !== 'object') settings = {};
                    settings.displayName = name;
                    settings.email = email;
                    if (avatarUrl) settings.avatarURL = avatarUrl;
                    localStorage.setItem('features/base/settings', JSON.stringify(settings));
                } catch (eSettings) {}
                localStorage.setItem('displayname', name);
                localStorage.setItem('email', email);
                if (avatarUrl) localStorage.setItem('avatarURL', avatarUrl);
            }
            """,
            botName,
            email,
            avatarUrl ?? "");
    }

    /// <summary>
    /// Pushes display name / email / avatar into a live conference (DOM: Jitsi keeps using
    /// old gravatar hash until settings/conference APIs are updated).
    /// </summary>
    private async Task ApplyLiveProfileAsync(IPage page, CancellationToken cancellationToken)
    {
        var botName = _configuration.BotName ?? string.Empty;
        var email = (_configuration.Gravatar ?? string.Empty).Trim();
        var avatarUrl = BuildGravatarAvatarUrl(email);

        await WriteProfileToLocalStorageAsync(page, botName, email, avatarUrl);

        var json = await EvaluateInMainWorldAsync(
            page,
            $$"""
            function() {
                var name = {{JsonSerializer.Serialize(botName)}};
                var email = {{JsonSerializer.Serialize(email)}};
                var avatarUrl = {{JsonSerializer.Serialize(avatarUrl ?? "")}};
                var via = [];
                try {
                    var app = window.APP;
                    if (!app)
                        return JSON.stringify({ ok: false, error: 'no-app' });

                    try {
                        if (app.conference) {
                            if (typeof app.conference.changeLocalDisplayName === 'function') {
                                app.conference.changeLocalDisplayName(name);
                                via.push('changeLocalDisplayName');
                            }
                            if (email && typeof app.conference.changeLocalEmail === 'function') {
                                app.conference.changeLocalEmail(email);
                                via.push('changeLocalEmail');
                            }
                            if (avatarUrl && typeof app.conference.changeLocalAvatarUrl === 'function') {
                                app.conference.changeLocalAvatarUrl(avatarUrl);
                                via.push('changeLocalAvatarUrl');
                            }
                        }
                    } catch (eConf) { via.push('conf-error:' + String(eConf && eConf.message || eConf)); }

                    try {
                        if (app.store && typeof app.store.dispatch === 'function') {
                            app.store.dispatch({
                                type: 'UPDATE_SETTINGS',
                                settings: {
                                    displayName: name,
                                    email: email || undefined,
                                    avatarURL: avatarUrl || undefined
                                }
                            });
                            via.push('UPDATE_SETTINGS');
                            app.store.dispatch({
                                type: 'SETTINGS_UPDATED',
                                settings: {
                                    displayName: name,
                                    email: email || undefined,
                                    avatarURL: avatarUrl || undefined
                                }
                            });
                            via.push('SETTINGS_UPDATED');
                        }
                    } catch (eStore) { via.push('store-error:' + String(eStore && eStore.message || eStore)); }

                    try {
                        if (app.conference && app.conference._room
                            && typeof app.conference._room.setLocalParticipantProperty === 'function') {
                            if (email) app.conference._room.setLocalParticipantProperty('email', email);
                            if (avatarUrl) app.conference._room.setLocalParticipantProperty('avatar-url', avatarUrl);
                            via.push('setLocalParticipantProperty');
                        }
                    } catch (eRoom) { via.push('room-error:' + String(eRoom && eRoom.message || eRoom)); }

                    return JSON.stringify({ ok: true, via: via, email: email, avatarUrl: avatarUrl });
                } catch (e) {
                    return JSON.stringify({ ok: false, error: String(e && e.message || e) });
                }
            }
            """);

        _logger.LogInformation("Live profile applied: {Result}", json);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string BuildGravatarAvatarUrl(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.IndexOf('@') < 0)
            return "";

        var normalized = email.Trim().ToLowerInvariant();
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
        // DOM used size=200 and d=404 (broken when no gravatar). Force identicon fallback.
        return $"https://www.gravatar.com/avatar/{hash}?d=identicon&s=200";
    }

    private async Task JoinConferenceAsync(IPage page, CancellationToken cancellationToken)
    {
        var host = NormalizeHost(_configuration.JitsiHost);
        var room = (_configuration.JitsiRoom ?? string.Empty).Trim().Trim('/');
        var roomUrl = $"{host}/{room.ToLowerInvariant()}";
        var joinUrl =
            $"{roomUrl}#config.startWithAudioMuted=true&config.startWithVideoMuted=true";

        // HTTP Basic Auth (some Prosody Secure Domain setups).
        try
        {
            await page.AuthenticateAsync(new Credentials
            {
                Username = _configuration.JitsiUserLogin,
                Password = _configuration.JitsiUserPassword,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "page.AuthenticateAsync skipped");
        }

        _logger.LogInformation("Navigating to room {RoomUrl}", roomUrl);
        await page.GoToAsync(joinUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = 60_000,
        });

        // Let prejoin UI settle before dump / login probing.
        await Task.Delay(1500, cancellationToken);
        await _domSnapshots.SaveAsync(page, "02-prejoin", cancellationToken);

        // Login dialog may appear before or after the prejoin Join click.
        await SignInAsAccountAsync(page, requireDialog: false, timeout: TimeSpan.FromSeconds(15), cancellationToken);
        await _domSnapshots.SaveAsync(page, "03-after-auth-soft", cancellationToken);

        if (await IsJoinedAsync(page) && await HasAuthenticatedSessionAsync(page))
        {
            _joinedAt = DateTime.UtcNow;
            await _domSnapshots.SaveAsync(page, "06-joined-early", cancellationToken);
            _logger.LogInformation("Already in conference after login: {RoomUrl}", roomUrl);
            return;
        }

        try
        {
            _logger.LogInformation("Clicking conference join button...");
            // Avoid PuppeteerSharp WaitForSelectorAsync — it can stack-overflow in WaitTask.RerunAsync.
            var clicked = await TryClickJoinButtonAsync(page, TimeSpan.FromSeconds(15), cancellationToken);
            if (!clicked)
                await page.Keyboard.PressAsync("Enter");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to click join button");
        }

        await Task.Delay(1000, cancellationToken);
        await _domSnapshots.SaveAsync(page, "04-after-join-click", cancellationToken);

        // Secure Domain login often appears only after Join.
        await SignInAsAccountAsync(page, requireDialog: true, timeout: TimeSpan.FromSeconds(45), cancellationToken);
        await _domSnapshots.SaveAsync(page, "05-after-auth-hard", cancellationToken);

        await WaitUntilJoinedAsync(page, TimeSpan.FromSeconds(45), cancellationToken);

        if (!await HasAuthenticatedSessionAsync(page) && !await IsModeratorOrLoggedInAsync(page))
        {
            await _domSnapshots.SaveAsync(page, "05b-auth-failed", cancellationToken);
            await LogLoginDiagnosticsAsync(page);
            throw new InvalidOperationException(
                "Joined the room but account authentication was not confirmed; refusing anonymous session.");
        }

        _joinedAt = DateTime.UtcNow;
        await ApplyLiveProfileAsync(page, cancellationToken);
        await _domSnapshots.SaveAsync(page, "06-joined", cancellationToken);
        _logger.LogInformation("Jitsi bot is in the room as {Login}: {RoomUrl}",
            _configuration.JitsiUserLogin,
            roomUrl);
    }

    private async Task SignInAsAccountAsync(
        IPage page,
        bool requireDialog,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var login = _configuration.JitsiUserLogin;
        var password = _configuration.JitsiUserPassword;

        _logger.LogInformation(
            "Looking for account login dialog as {Login} (require={Require}, timeout={Timeout}s)...",
            login,
            requireDialog,
            (int)timeout.TotalSeconds);

        await TryClickAuthTriggersAsync(page);

        var deadline = DateTime.UtcNow + timeout;
        var submitted = false;
        var lobbyDumped = false;
        var apiAuthTried = false;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await HasAuthenticatedSessionAsync(page) || await IsModeratorOrLoggedInAsync(page))
            {
                _logger.LogInformation("Using existing authenticated Jitsi session");
                return;
            }

            // DOM: "Waiting for a moderator… Log-in" — guest join landed in lobby;
            // force host login instead of waiting anonymously.
            if (await IsWaitingForModeratorLobbyAsync(page))
            {
                if (!lobbyDumped)
                {
                    lobbyDumped = true;
                    _logger.LogInformation(
                        "Moderator lobby detected — forcing account Log-in as {Login}",
                        login);
                    await _domSnapshots.SaveAsync(page, "04b-moderator-lobby", cancellationToken);
                }

                await TryClickAuthTriggersAsync(page);
                if (!apiAuthTried)
                {
                    apiAuthTried = true;
                    await TryAuthenticateViaApiAsync(page, login, password);
                }
            }

            if (await TryFillAndSubmitLoginAsync(page, login, password))
            {
                submitted = true;
                break;
            }

            foreach (var frame in page.Frames)
            {
                if (ReferenceEquals(frame, page.MainFrame))
                    continue;

                if (await TryFillAndSubmitLoginInFrameAsync(frame, login, password))
                {
                    submitted = true;
                    break;
                }
            }

            if (submitted)
                break;

            await TryClickAuthTriggersAsync(page);
            await Task.Delay(500, cancellationToken);
        }

        if (!submitted)
        {
            if (await HasAuthenticatedSessionAsync(page) || await IsModeratorOrLoggedInAsync(page))
            {
                _logger.LogInformation("No login form — authenticated session already present");
                return;
            }

            // Already in the call and clearly logged in as account — OK.
            if (await IsJoinedAsync(page) && await IsModeratorOrLoggedInAsync(page))
                return;

            if (!requireDialog)
            {
                _logger.LogInformation("Login dialog not shown yet — will retry after join");
                return;
            }

            await LogLoginDiagnosticsAsync(page);
            throw new InvalidOperationException(
                "Account login dialog did not appear; refusing anonymous join.");
        }

        _logger.LogInformation("Submitted Secure Domain credentials for {Login}", login);

        var hideDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < hideDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await HasAuthenticatedSessionAsync(page) || await IsModeratorOrLoggedInAsync(page))
                break;
            if (!await IsLoginDialogVisibleAsync(page) && await IsJoinedAsync(page))
                break;
            // Form may reappear after failed attempt — try once more.
            if (await IsLoginDialogVisibleAsync(page))
                await TryFillAndSubmitLoginAsync(page, login, password);
            await Task.Delay(500, cancellationToken);
        }

        if (await IsLoginDialogVisibleAsync(page)
            && !await HasAuthenticatedSessionAsync(page)
            && !await IsModeratorOrLoggedInAsync(page))
            _logger.LogWarning("Login dialog still visible after submit — credentials may be wrong");

        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
    }

    private async Task<bool> TryClickJoinButtonAsync(
        IPage page,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var clicked = await page.EvaluateFunctionAsync<bool>(
                    """
                    () => {
                        function visible(el) {
                            if (!el || !el.getBoundingClientRect) return false;
                            try {
                                var r = el.getBoundingClientRect();
                                if (r.width < 2 || r.height < 2) return false;
                                var s = window.getComputedStyle(el);
                                return !!(s && s.display !== 'none' && s.visibility !== 'hidden' && s.opacity !== '0');
                            } catch (e) { return false; }
                        }

                        var selectors = [
                            'button[data-testid="prejoin.joinMeeting"]',
                            '.prejoin-page-button',
                            '[data-testid="prejoin.joinMeeting"]'
                        ];
                        for (var i = 0; i < selectors.length; i++) {
                            var el = document.querySelector(selectors[i]);
                            if (el && visible(el)) {
                                el.click();
                                return true;
                            }
                        }

                        var nodes = document.querySelectorAll('button, [role="button"], a');
                        for (var j = 0; j < nodes.length; j++) {
                            var n = nodes[j];
                            if (!visible(n)) continue;
                            var label = ((n.innerText || n.textContent || '') + ' ' + (n.getAttribute('aria-label') || ''))
                                .toLowerCase().replace(/\s+/g, ' ').trim();
                            if (label.indexOf('join meeting') >= 0
                                || label.indexOf('join the meeting') >= 0
                                || label.indexOf('присоединиться к встрече') >= 0
                                || label.indexOf('присоединиться') >= 0
                                || label === 'join'
                                || label.indexOf('start meeting') >= 0) {
                                // Do not match plain "войти" — that is usually account login.
                                n.click();
                                return true;
                            }
                        }
                        return false;
                    }
                    """);

                if (clicked)
                    return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Join button poll failed");
            }

            await Task.Delay(400, cancellationToken);
        }

        return false;
    }

    private async Task TryClickAuthTriggersAsync(IPage page)
    {
        try
        {
            var clicked = await page.EvaluateFunctionAsync<bool>(
                """
                () => {
                    function visible(el) {
                        if (!el || !el.getBoundingClientRect) return false;
                        try {
                            var r = el.getBoundingClientRect();
                            if (r.width < 2 || r.height < 2) return false;
                            var s = window.getComputedStyle(el);
                            return !!(s && s.display !== 'none' && s.visibility !== 'hidden' && s.opacity !== '0');
                        } catch (e) { return false; }
                    }
                    function norm(s) {
                        return String(s || '')
                            .toLowerCase()
                            .replace(/[\u2010-\u2015\u2212\-]+/g, ' ')  // log-in → log in
                            .replace(/\s+/g, ' ')
                            .trim();
                    }
                    function isAuthLabel(label) {
                        if (!label) return false;
                        // Guest join — never treat as host auth.
                        if (label === 'войти' || label === 'join' || label === 'join meeting'
                            || label === 'cancel' || label === 'отмена')
                            return false;
                        if (label === 'log in' || label === 'login' || label === 'sign in')
                            return true;
                        return label.indexOf('i am the host') >= 0
                            || label.indexOf("i'm the host") >= 0
                            || label.indexOf('log in') >= 0
                            || label.indexOf('login') >= 0
                            || label.indexOf('sign in') >= 0
                            || label.indexOf('become a moderator') >= 0
                            || label.indexOf('войти как') >= 0
                            || label.indexOf('авториз') >= 0
                            || label.indexOf('я ведущий') >= 0
                            || label.indexOf('я организатор') >= 0
                            || label.indexOf('я хост') >= 0
                            || label.indexOf('host login') >= 0
                            || label.indexOf('authenticate') >= 0
                            || label.indexOf('join in browser') >= 0
                            || label.indexOf('присоединиться в браузер') >= 0
                            || label.indexOf('продолжить в браузер') >= 0;
                    }

                    var nodes = document.querySelectorAll(
                        'a, button, [role="button"], div[class*="button"], span[role="button"]');
                    // Prefer exact Log-in on the wait-for-moderator dialog.
                    for (var pass = 0; pass < 2; pass++) {
                        for (var i = 0; i < nodes.length; i++) {
                            var el = nodes[i];
                            if (!visible(el)) continue;
                            var raw = (el.innerText || el.textContent || '') + ' ' + (el.getAttribute('aria-label') || '');
                            var label = norm(raw);
                            if (!isAuthLabel(label)) continue;
                            if (pass === 0) {
                                // First pass: short labels that are clearly the lobby Log-in.
                                if (!(label === 'log in' || label === 'login' || label === 'sign in'
                                    || label.indexOf('i am the host') >= 0
                                    || label.indexOf('become a moderator') >= 0))
                                    continue;
                            }
                            el.click();
                            return true;
                        }
                    }
                    return false;
                }
                """);

            if (clicked)
                _logger.LogInformation("Clicked host/moderator Log-in trigger");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auth trigger click skipped");
        }
    }

    private static async Task<bool> IsWaitingForModeratorLobbyAsync(IPage page)
    {
        try
        {
            return await page.EvaluateFunctionAsync<bool>(
                """
                () => {
                    var text = ((document.body && (document.body.innerText || document.body.textContent)) || '')
                        .toLowerCase();
                    return text.indexOf('waiting for a moderator') >= 0
                        || text.indexOf('no moderators have yet arrived') >= 0
                        || text.indexOf('become a moderator') >= 0
                        || text.indexOf('ожидание модератора') >= 0
                        || text.indexOf('ожидаем модератора') >= 0
                        || text.indexOf('конференция ещё не началась') >= 0
                        || text.indexOf('конференция еще не началась') >= 0;
                }
                """);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Secure Domain: upgrade anonymous lobby session to authenticated moderator via lib-jitsi-meet.
    /// </summary>
    private async Task TryAuthenticateViaApiAsync(IPage page, string login, string password)
    {
        try
        {
            var json = await EvaluateInMainWorldAsyncAsync(
                page,
                """
                async function(creds) {
                    var user = creds && creds.user || '';
                    var pass = creds && creds.pass || '';
                    var via = [];
                    try {
                        var app = window.APP;
                        if (!app)
                            return JSON.stringify({ ok: false, error: 'no-app' });

                        async function withTimeout(promise, ms) {
                            return await Promise.race([
                                Promise.resolve(promise),
                                new Promise(function (_, reject) {
                                    setTimeout(function () { reject(new Error('auth-timeout')); }, ms);
                                })
                            ]);
                        }

                        // Preferred: upgrade role while sitting in wait-for-moderator lobby.
                        try {
                            if (app.conference && typeof app.conference.authenticateAndUpgradeRole === 'function') {
                                await withTimeout(app.conference.authenticateAndUpgradeRole({
                                    id: user,
                                    password: pass
                                }), 8000);
                                via.push('authenticateAndUpgradeRole');
                                return JSON.stringify({ ok: true, via: via });
                            }
                        } catch (e1) {
                            via.push('upgrade-error:' + String(e1 && e1.message || e1));
                        }

                        try {
                            if (app.conference && app.conference._room
                                && typeof app.conference._room.authenticateAndUpgradeRole === 'function') {
                                await withTimeout(app.conference._room.authenticateAndUpgradeRole({
                                    id: user,
                                    password: pass
                                }), 8000);
                                via.push('_room.authenticateAndUpgradeRole');
                                return JSON.stringify({ ok: true, via: via });
                            }
                        } catch (e2) {
                            via.push('room-error:' + String(e2 && e2.message || e2));
                        }

                        // Redux auth dialog open (then DOM fill still runs).
                        try {
                            if (app.store && typeof app.store.dispatch === 'function') {
                                app.store.dispatch({ type: 'OPEN_DIALOG', componentName: 'LoginDialog' });
                                via.push('OPEN_DIALOG LoginDialog');
                            }
                        } catch (e3) {
                            via.push('dialog-error:' + String(e3 && e3.message || e3));
                        }

                        return JSON.stringify({ ok: via.length > 0, via: via });
                    } catch (e) {
                        return JSON.stringify({ ok: false, error: String(e && e.message || e) });
                    }
                }
                """,
                new { user = login, pass = password });

            _logger.LogInformation("Jitsi API auth attempt: {Result}", json);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Jitsi API auth skipped");
        }
    }

    private static async Task<bool> IsLoginDialogVisibleAsync(IPage page)
    {
        try
        {
            return await page.EvaluateFunctionAsync<bool>(LoginDialogVisibleScript);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TryFillAndSubmitLoginAsync(IPage page, string login, string password)
    {
        try
        {
            return await page.EvaluateFunctionAsync<bool>(FillLoginScript, login, password);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Main-frame login fill failed");
            return false;
        }
    }

    private async Task<bool> TryFillAndSubmitLoginInFrameAsync(IFrame frame, string login, string password)
    {
        try
        {
            return await frame.EvaluateFunctionAsync<bool>(FillLoginScript, login, password);
        }
        catch
        {
            return false;
        }
    }

    private const string LoginDialogVisibleScript =
        """
        () => {
            function visible(el) {
                if (!el || !el.getBoundingClientRect) return false;
                try {
                    var r = el.getBoundingClientRect();
                    if (r.width < 2 || r.height < 2) return false;
                    var s = window.getComputedStyle(el);
                    return !!(s && s.display !== 'none' && s.visibility !== 'hidden' && s.opacity !== '0');
                } catch (e) { return false; }
            }
            function collectInputs(root, out) {
                if (!root) return;
                var list = root.querySelectorAll ? root.querySelectorAll('input') : [];
                for (var i = 0; i < list.length; i++) out.push(list[i]);
                var all = root.querySelectorAll ? root.querySelectorAll('*') : [];
                for (var j = 0; j < all.length; j++) {
                    if (all[j].shadowRoot) collectInputs(all[j].shadowRoot, out);
                }
            }
            var text = ((document.body && (document.body.innerText || document.body.textContent)) || '').toLowerCase();
            if (text.indexOf('требуется авторизация') >= 0
                || text.indexOf('authentication required') >= 0
                || text.indexOf('login required') >= 0
                || text.indexOf('идентификатор пользователя') >= 0
                || text.indexOf('пароль пользователя') >= 0
                || text.indexOf('username') >= 0 && text.indexOf('password') >= 0)
                return true;

            var inputs = [];
            collectInputs(document, inputs);
            var pass = null;
            for (var i = 0; i < inputs.length; i++) {
                var el = inputs[i];
                if (!visible(el)) continue;
                if ((el.type || '').toLowerCase() === 'password') pass = el;
            }
            return !!pass;
        }
        """;

    private const string FillLoginScript =
        """
        (user, password) => {
            function visible(el) {
                if (!el || !el.getBoundingClientRect) return false;
                try {
                    var r = el.getBoundingClientRect();
                    if (r.width < 2 || r.height < 2) return false;
                    var s = window.getComputedStyle(el);
                    return !!(s && s.display !== 'none' && s.visibility !== 'hidden' && s.opacity !== '0');
                } catch (e) { return false; }
            }
            function meta(el) {
                return (
                    (el.name || '') + ' ' + (el.id || '') + ' ' + (el.placeholder || '') + ' ' +
                    (el.getAttribute('aria-label') || '') + ' ' + (el.getAttribute('autocomplete') || '') + ' ' +
                    (el.getAttribute('data-testid') || '')
                ).toLowerCase();
            }
            function setValue(el, value) {
                if (!el) return;
                el.focus();
                var proto = window.HTMLInputElement && window.HTMLInputElement.prototype;
                var descriptor = proto && Object.getOwnPropertyDescriptor(proto, 'value');
                if (descriptor && descriptor.set) descriptor.set.call(el, value);
                else el.value = value;
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
                el.dispatchEvent(new KeyboardEvent('keyup', { bubbles: true }));
            }
            function collectInputs(root, out) {
                if (!root) return;
                var list = root.querySelectorAll ? root.querySelectorAll('input') : [];
                for (var i = 0; i < list.length; i++) out.push(list[i]);
                var all = root.querySelectorAll ? root.querySelectorAll('*') : [];
                for (var j = 0; j < all.length; j++) {
                    if (all[j].shadowRoot) collectInputs(all[j].shadowRoot, out);
                }
            }

            var userEl = document.querySelector(
                'input#username, input#login_username, input[name="username"], input[name="user"], input[autocomplete="username"]');
            var passEl = document.querySelector(
                'input#password, input#login_password, input[name="password"], input[type="password"], input[autocomplete="current-password"]');

            var inputs = [];
            collectInputs(document, inputs);

            if (!passEl || !visible(passEl)) {
                passEl = null;
                for (var i = 0; i < inputs.length; i++) {
                    var el = inputs[i];
                    if (!visible(el)) continue;
                    if ((el.type || '').toLowerCase() === 'password') {
                        passEl = el;
                        break;
                    }
                }
            }

            if (!userEl || !visible(userEl)) {
                userEl = null;
                for (var j = 0; j < inputs.length; j++) {
                    var t = inputs[j];
                    if (!visible(t) || t === passEl) continue;
                    var tt = (t.type || 'text').toLowerCase();
                    if (tt !== 'text' && tt !== 'email' && tt !== 'username' && tt !== '')
                        continue;
                    var m = meta(t);
                    if (m.indexOf('user') >= 0 || m.indexOf('login') >= 0 || m.indexOf('ident') >= 0
                        || m.indexOf('пользовател') >= 0 || m.indexOf('логин') >= 0
                        || t.name === 'username' || t.id === 'login_username' || t.id === 'username') {
                        userEl = t;
                        break;
                    }
                }
            }

            // Any visible text field paired with password (Secure Domain dialog).
            if (passEl && !userEl) {
                for (var k = 0; k < inputs.length; k++) {
                    var u = inputs[k];
                    if (!visible(u) || u === passEl) continue;
                    var ut = (u.type || 'text').toLowerCase();
                    if (ut === 'text' || ut === 'email' || ut === 'username' || ut === '') {
                        userEl = u;
                        break;
                    }
                }
            }

            if (!passEl)
                return false;

            if (userEl) setValue(userEl, user || '');
            setValue(passEl, password || '');

            var submit = document.querySelector(
                'button[type="submit"], input[type="submit"], #login_button, button.primary, [data-testid="modal-login-button"]');
            if (submit && visible(submit)) {
                submit.click();
                return true;
            }

            var nodes = document.querySelectorAll('button, [role="button"], a, div[class*="button"]');
            for (var n = 0; n < nodes.length; n++) {
                var btn = nodes[n];
                if (!visible(btn)) continue;
                var label = ((btn.innerText || btn.textContent || '') + ' ' + (btn.getAttribute('aria-label') || ''))
                    .toLowerCase()
                    .replace(/[\u2010-\u2015\u2212\-]+/g, ' ')
                    .replace(/\s+/g, ' ')
                    .trim();
                if (label === 'cancel' || label === 'отмена')
                    continue;
                if (label.indexOf('login') >= 0 || label.indexOf('log in') >= 0 || label.indexOf('sign in') >= 0
                    || label.indexOf('войти') >= 0 || label === 'ok'
                    || label.indexOf('продолжить') >= 0 || label.indexOf('submit') >= 0
                    || label.indexOf('authenticate') >= 0) {
                    btn.click();
                    return true;
                }
            }

            if (passEl.form) {
                if (passEl.form.requestSubmit) passEl.form.requestSubmit();
                else passEl.form.submit();
                return true;
            }

            passEl.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', keyCode: 13, bubbles: true }));
            return true;
        }
        """;

    private static async Task<bool> HasAuthenticatedSessionAsync(IPage page)
    {
        try
        {
            return await page.EvaluateFunctionAsync<bool>(
                """
                () => {
                    try {
                        if (typeof APP === 'undefined' || !APP.store || typeof APP.store.getState !== 'function')
                            return false;
                        var state = APP.store.getState();
                        var jwt = state['features/base/jwt'] || {};
                        if (jwt.jwt || jwt.user)
                            return true;
                        var conf = state['features/base/conference'] || {};
                        // Only treat as authed when conference exists and auth is explicitly not required
                        // OR local participant is already a moderator (Secure Domain success).
                        if (conf.conference && conf.authRequired === false)
                            return true;
                        var parts = state['features/base/participants'] || {};
                        var local = parts.local;
                        if (local && (local.role === 'moderator' || local.features && local.features['jitsi_moderator'] === true))
                            return true;
                        return false;
                    } catch (e) {
                        return false;
                    }
                }
                """);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsModeratorOrLoggedInAsync(IPage page)
    {
        try
        {
            return await page.EvaluateFunctionAsync<bool>(
                """
                () => {
                    try {
                        if (typeof APP === 'undefined') return false;
                        if (APP.store && typeof APP.store.getState === 'function') {
                            var state = APP.store.getState();
                            var jwt = state['features/base/jwt'] || {};
                            if (jwt.jwt || jwt.user) return true;
                            var local = (state['features/base/participants'] || {}).local;
                            if (local && local.role === 'moderator') return true;
                        }
                        if (APP.conference) {
                            if (typeof APP.conference.isModerator === 'function' && APP.conference.isModerator())
                                return true;
                            if (typeof APP.conference.getRole === 'function'
                                && String(APP.conference.getRole() || '').toLowerCase() === 'moderator')
                                return true;
                        }
                    } catch (e) {}
                    return false;
                }
                """);
        }
        catch
        {
            return false;
        }
    }

    private async Task LogLoginDiagnosticsAsync(IPage page)
    {
        try
        {
            var dump = await page.EvaluateFunctionAsync<string>(
                """
                () => {
                    function visible(el) {
                        if (!el || !el.getBoundingClientRect) return false;
                        try {
                            var r = el.getBoundingClientRect();
                            if (r.width < 2 || r.height < 2) return false;
                            var s = window.getComputedStyle(el);
                            return !!(s && s.display !== 'none' && s.visibility !== 'hidden');
                        } catch (e) { return false; }
                    }
                    var inputs = [];
                    document.querySelectorAll('input').forEach(function (el) {
                        if (!visible(el)) return;
                        inputs.push({
                            type: el.type || '',
                            name: el.name || '',
                            id: el.id || '',
                            placeholder: el.placeholder || ''
                        });
                    });
                    var body = ((document.body && document.body.innerText) || '').slice(0, 400);
                    var joined = false;
                    try {
                        joined = !!(APP && APP.conference && APP.conference.isJoined && APP.conference.isJoined());
                    } catch (e) {}
                    return JSON.stringify({ url: location.href, joined: joined, inputs: inputs, bodyPreview: body });
                }
                """);
            _logger.LogWarning("Login diagnostics: {Dump}", dump);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Login diagnostics failed");
        }
    }

    private static async Task<bool> IsJoinedAsync(IPage page)
    {
        try
        {
            return await page.EvaluateFunctionAsync<bool>(
                """
                () => {
                    try {
                        if (typeof APP !== 'undefined' && APP.conference
                            && typeof APP.conference.isJoined === 'function')
                            return !!APP.conference.isJoined();
                    } catch (e) {}
                    return false;
                }
                """);
        }
        catch
        {
            return false;
        }
    }

    private async Task WaitUntilJoinedAsync(IPage page, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsJoinedAsync(page))
                return;
            await Task.Delay(500, cancellationToken);
        }

        _logger.LogWarning("Timed out waiting for APP.conference.isJoined()");
    }

    private Task DisposeBrowserAsync()
    {
        // Disconnect only — leave Chromium running so the next process can attach
        // via remote debugging and reuse the authenticated session.
        _joinedAt = null;
        _page = null;

        try
        {
            if (_browser != null)
            {
                _browser.Disconnect();
                _logger.LogInformation(
                    "Disconnected from Chromium (left running for reconnect, ownsBrowser={OwnsBrowser})",
                    _ownsBrowser);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disconnecting Jitsi browser");
        }

        _browser = null;
        _ownsBrowser = false;
        return Task.CompletedTask;
    }

    private string ResolveUserDataDir()
    {
        if (!string.IsNullOrWhiteSpace(_configuration.JitsiUserDataDir))
            return _configuration.JitsiUserDataDir;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlcoRats.Bot",
            "chromium-profile");
    }

    private async Task<string> ResolveChromiumPathAsync(CancellationToken cancellationToken)
    {
        if (IsUsableChromiumExecutable(_configuration.ChromiumExecutablePath))
            return _configuration.ChromiumExecutablePath;

        foreach (var path in DefaultChromiumPaths)
        {
            if (IsUsableChromiumExecutable(path))
                return path;
        }

        _logger.LogInformation("System Chromium not found; downloading Chrome for PuppeteerSharp...");
        var fetcher = new BrowserFetcher();
        var installed = await fetcher.DownloadAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return installed.GetExecutablePath();
    }

    /// <summary>
    /// Rejects missing paths and Ubuntu snap transitional stubs (chromium-browser → /snap/bin/chromium).
    /// </summary>
    private static bool IsUsableChromiumExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[256];
            var read = stream.Read(header);
            if (read >= 2 && header[0] == (byte)'#' && header[1] == (byte)'!')
            {
                var text = System.Text.Encoding.UTF8.GetString(header[..read]);
                if (text.Contains("/snap/", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("snap install", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static string NormalizeHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return string.Empty;

        return host.Trim().TrimEnd('/');
    }

    /// <summary>
    /// async function(wavBase64) — decode full WAV, publish via Jitsi local audio track, wait until finished.
    /// </summary>
    private const string PublishWavToConferenceScript =
        """
        async function(wavBase64) {
            async function unmuteMicrophone(app, conference) {
                var via = [];

                // DOM toolbox (from live dump): aria-label="Unmute microphone"
                try {
                    var selectors = [
                        '[aria-label="Unmute microphone"]',
                        '[aria-label="Unmute"]',
                        '[aria-label*="Unmute microphone"]',
                        '[aria-label="Включить микрофон"]',
                        '[aria-label*="Включить микрофон"]'
                    ];
                    for (var s = 0; s < selectors.length; s++) {
                        var el = document.querySelector(selectors[s]);
                        if (el) {
                            el.click();
                            via.push('dom:' + selectors[s]);
                            break;
                        }
                    }
                    if (via.length === 0) {
                        var nodes = document.querySelectorAll('[aria-label], .toolbox-button, [role="button"]');
                        for (var n = 0; n < nodes.length; n++) {
                            var lab = String(nodes[n].getAttribute('aria-label') || nodes[n].innerText || '').toLowerCase();
                            if (lab.indexOf('unmute') >= 0 || lab.indexOf('включить микрофон') >= 0) {
                                nodes[n].click();
                                via.push('dom-scan');
                                break;
                            }
                        }
                    }
                } catch (eDom) {}

                // UI / APP helper (preferred — updates toolbar mute state).
                try {
                    if (app.conference && typeof app.conference.muteAudio === 'function') {
                        app.conference.muteAudio(false);
                        via.push('APP.conference.muteAudio(false)');
                    }
                } catch (e1) {}

                try {
                    if (typeof APP !== 'undefined' && APP.conference && typeof APP.conference.muteAudio === 'function') {
                        APP.conference.muteAudio(false);
                        via.push('muteAudio');
                    }
                } catch (e2) {}

                // Unmute every local audio JitsiTrack.
                try {
                    if (conference && typeof conference.getLocalTracks === 'function') {
                        var locals = conference.getLocalTracks() || [];
                        for (var i = 0; i < locals.length; i++) {
                            var tr = locals[i];
                            if (!tr || typeof tr.getType !== 'function' || tr.getType() !== 'audio')
                                continue;
                            if (typeof tr.unmute === 'function') {
                                await tr.unmute();
                                via.push('track.unmute');
                            }
                            try {
                                var mt = typeof tr.getTrack === 'function' ? tr.getTrack() : null;
                                if (mt && mt.enabled === false)
                                    mt.enabled = true;
                            } catch (eMt) {}
                        }
                    }
                } catch (e3) {}

                // Redux audio-mute flag when present.
                try {
                    if (app.store && typeof app.store.dispatch === 'function') {
                        app.store.dispatch({ type: 'SET_AUDIO_MUTED', muted: false });
                        via.push('SET_AUDIO_MUTED');
                    }
                } catch (e4) {}

                return via;
            }

            try {
                if (!wavBase64 || typeof wavBase64 !== 'string')
                    return JSON.stringify({ ok: false, error: 'empty-wav' });

                var app = window.APP;
                if (!app)
                    return JSON.stringify({ ok: false, error: 'no-app' });

                var conference = null;
                try {
                    if (app.store && typeof app.store.getState === 'function') {
                        var state = app.store.getState();
                        conference = (state['features/base/conference'] || {}).conference || null;
                    }
                } catch (eState) {}
                if (!conference && app.conference && app.conference._room)
                    conference = app.conference._room;

                if (!conference)
                    return JSON.stringify({ ok: false, error: 'no-conference' });

                // Mic must be on before TTS audio is published.
                var unmuteVia = await unmuteMicrophone(app, conference);

                var binary = atob(wavBase64);
                var bytes = new Uint8Array(binary.length);
                for (var i = 0; i < binary.length; i++)
                    bytes[i] = binary.charCodeAt(i);

                var Ctx = window.AudioContext || window.webkitAudioContext;
                if (!Ctx)
                    return JSON.stringify({ ok: false, error: 'no-audiocontext' });

                var ctx = new Ctx();
                if (ctx.state === 'suspended' && typeof ctx.resume === 'function')
                    await ctx.resume();

                var audioBuffer = await ctx.decodeAudioData(bytes.buffer.slice(0));
                var dest = ctx.createMediaStreamDestination();
                var source = ctx.createBufferSource();
                source.buffer = audioBuffer;
                source.connect(dest);

                var mediaStream = dest.stream;
                var mediaTrack = mediaStream.getAudioTracks()[0];
                if (!mediaTrack)
                    return JSON.stringify({ ok: false, error: 'no-media-track' });
                mediaTrack.enabled = true;

                var newTrack = null;
                var oldTrack = null;
                var replaced = false;

                if (typeof JitsiMeetJS !== 'undefined'
                    && typeof JitsiMeetJS.createLocalTracksFromMediaStreams === 'function') {
                    var created = await JitsiMeetJS.createLocalTracksFromMediaStreams([{
                        stream: mediaStream,
                        track: mediaTrack,
                        mediaType: 'audio',
                        sourceType: 'audio',
                        videoType: undefined
                    }]);
                    newTrack = created && created[0] ? created[0] : null;

                    if (newTrack && typeof conference.getLocalTracks === 'function') {
                        var locals = conference.getLocalTracks() || [];
                        for (var t = 0; t < locals.length; t++) {
                            if (locals[t] && typeof locals[t].getType === 'function'
                                && locals[t].getType() === 'audio') {
                                oldTrack = locals[t];
                                break;
                            }
                        }

                        if (oldTrack && typeof conference.replaceTrack === 'function') {
                            await conference.replaceTrack(oldTrack, newTrack);
                            replaced = true;
                        } else if (typeof conference.addTrack === 'function') {
                            await conference.addTrack(newTrack);
                            replaced = true;
                        }
                    }

                    if (newTrack && typeof newTrack.unmute === 'function') {
                        try { await newTrack.unmute(); } catch (eUnmute) {}
                    }
                }

                // Ensure UI mute is still off after track swap.
                await unmuteMicrophone(app, conference);

                var ended = new Promise(function (resolve) {
                    source.onended = function () { resolve(); };
                    setTimeout(resolve, Math.ceil((audioBuffer.duration + 0.75) * 1000));
                });

                source.start(0);
                await ended;

                if (replaced && newTrack) {
                    try {
                        if (oldTrack && typeof conference.replaceTrack === 'function')
                            await conference.replaceTrack(newTrack, oldTrack);
                        else if (typeof conference.removeTrack === 'function')
                            await conference.removeTrack(newTrack);
                    } catch (eRestore) {}

                    try {
                        if (oldTrack && typeof oldTrack.mute === 'function')
                            await oldTrack.mute();
                    } catch (eRemute) {}

                    try {
                        if (app.conference && typeof app.conference.muteAudio === 'function')
                            app.conference.muteAudio(true);
                    } catch (eMuteUi) {}

                    try {
                        if (typeof newTrack.dispose === 'function')
                            newTrack.dispose();
                    } catch (eDisp) {}
                }

                try { await ctx.close(); } catch (eClose) {}

                return JSON.stringify({
                    ok: true,
                    durationMs: Math.round(audioBuffer.duration * 1000),
                    replaced: replaced,
                    micUnmutedVia: unmuteVia
                });
            } catch (e) {
                return JSON.stringify({ ok: false, error: String(e && e.message || e) });
            }
        }
        """;

    private const string InstallChatHookScript =
        """
        function() {
            try {
                var app = window.APP;
                if (!window.__arChat) {
                    window.__arChat = { hooked: false, queue: [], seen: {}, suppressUntil: 0 };
                }
                var hook = window.__arChat;
                if (hook.hooked)
                    return JSON.stringify({ ok: true, hooked: true });
                if (!app || !app.store || typeof app.store.getState !== 'function')
                    return JSON.stringify({ ok: false, hooked: false, reason: 'no-app' });

                // Absorb join/history traffic; do not enqueue until this passes.
                hook.suppressUntil = Date.now() + 8000;

                function mapMessage(m) {
                    if (!m || m.isReaction) return null;
                    var text = String(m.message != null ? m.message : (m.content != null ? m.content : '')).trim();
                    if (!text) return null;
                    var id = m.messageId != null ? String(m.messageId)
                        : (m.id != null ? String(m.id)
                        : String((m.timestamp || 0) + ':' + text + ':' + (m.displayName || m.participantName || '')));
                    var ts = 0;
                    if (typeof m.timestamp === 'number' && m.timestamp > 0)
                        ts = m.timestamp < 1e12 ? m.timestamp * 1000 : m.timestamp;
                    var myId = '';
                    try {
                        if (app.conference && typeof app.conference.getMyUserId === 'function')
                            myId = String(app.conference.getMyUserId() || '');
                    } catch (eMy) {}
                    var pid = m.participantId != null ? String(m.participantId) : '';
                    return {
                        id: id,
                        text: text,
                        sender: String(m.displayName || m.participantName || m.sender || m.nick || ''),
                        timestampMs: ts,
                        local: !!(m.local || m.isLocal || (myId && pid && myId === pid))
                    };
                }

                function enqueueMapped(mapped) {
                    if (!mapped || hook.seen[mapped.id]) return;
                    hook.seen[mapped.id] = true;
                    if (Date.now() < hook.suppressUntil) return;
                    hook.queue.push(mapped);
                }

                function enqueueFromStore() {
                    try {
                        var chat = app.store.getState()['features/chat'] || {};
                        var messages = chat.messages || [];
                        for (var i = 0; i < messages.length; i++)
                            enqueueMapped(mapMessage(messages[i]));
                    } catch (eStore) {}
                }

                // Baseline: mark current messages as seen, don't enqueue.
                try {
                    var chat0 = app.store.getState()['features/chat'] || {};
                    var existing = chat0.messages || [];
                    for (var j = 0; j < existing.length; j++) {
                        var mapped0 = mapMessage(existing[j]);
                        if (mapped0) hook.seen[mapped0.id] = true;
                    }
                } catch (eBase) {}

                if (typeof app.store.subscribe === 'function') {
                    app.store.subscribe(function () { enqueueFromStore(); });
                }

                // lib-jitsi-meet chat events (names vary by version).
                try {
                    var conf = app.conference;
                    if (conf && typeof conf.on === 'function') {
                        var onMsg = function (id, text, ts) {
                            try {
                                var name = '';
                                try {
                                    var p = conf.getParticipantById && conf.getParticipantById(id);
                                    name = (p && (p.getDisplayName && p.getDisplayName() || p._displayName)) || '';
                                } catch (eName) {}
                                var tsMs = typeof ts === 'number' ? (ts < 1e12 ? ts * 1000 : ts) : Date.now();
                                var mid = String(ts || Date.now()) + ':' + String(id || '') + ':' + String(text || '');
                                var myId2 = '';
                                try { myId2 = String(conf.getMyUserId && conf.getMyUserId() || ''); } catch (e2) {}
                                enqueueMapped({
                                    id: mid,
                                    text: String(text || ''),
                                    sender: String(name || id || ''),
                                    timestampMs: tsMs,
                                    local: !!(myId2 && id && String(id) === myId2)
                                });
                            } catch (eMsg) {}
                        };
                        conf.on('conference.messageReceived', onMsg);
                        conf.on('conference.user_message_received', onMsg);
                        conf.on('messageReceived', onMsg);
                    }
                } catch (eConf) {}

                hook.hooked = true;
                return JSON.stringify({ ok: true, hooked: true });
            } catch (e) {
                return JSON.stringify({ ok: false, error: String(e && e.message || e) });
            }
        }
        """;

    private const string CollectChatMessagesScript =
        """
        function() {
            try {
                var app = window.APP;
                var appPresent = !!app;
                var hooked = !!(window.__arChat && window.__arChat.hooked);
                var result = [];

                // When hooked, only drain the live queue (store dump re-surfaces history with other ids).
                if (hooked) {
                    if (window.__arChat.queue && window.__arChat.queue.length)
                        result = window.__arChat.queue.splice(0, window.__arChat.queue.length);
                } else if (app && app.store && typeof app.store.getState === 'function') {
                    var chat = app.store.getState()['features/chat'] || {};
                    var messages = chat.messages || [];
                    var myId = '';
                    try {
                        if (app.conference && typeof app.conference.getMyUserId === 'function')
                            myId = String(app.conference.getMyUserId() || '');
                    } catch (eMy) {}

                    for (var i = 0; i < messages.length; i++) {
                        var m = messages[i];
                        if (!m || m.isReaction) continue;
                        var text = String(m.message != null ? m.message : (m.content != null ? m.content : '')).trim();
                        if (!text) continue;
                        var id = m.messageId != null ? String(m.messageId)
                            : (m.id != null ? String(m.id)
                            : String((m.timestamp || 0) + ':' + text + ':' + (m.displayName || m.participantName || '')));
                        var ts = 0;
                        if (typeof m.timestamp === 'number' && m.timestamp > 0)
                            ts = m.timestamp < 1e12 ? m.timestamp * 1000 : m.timestamp;
                        var pid = m.participantId != null ? String(m.participantId) : '';
                        result.push({
                            id: id,
                            text: text,
                            sender: String(m.displayName || m.participantName || m.sender || m.nick || ''),
                            timestampMs: ts,
                            local: !!(m.local || m.isLocal || (myId && pid && myId === pid))
                        });
                    }
                }

                return JSON.stringify({
                    messages: result,
                    appPresent: appPresent,
                    hooked: hooked
                });
            } catch (e) {
                return JSON.stringify({
                    messages: [],
                    appPresent: false,
                    hooked: false,
                    error: String(e && e.message || e)
                });
            }
        }
        """;

    // NOTE: evaluated via EvaluateExpressionAsync in the page main world (needs window.APP).
    private const string CollectParticipantsScript =
        """
        function() {
            function isNonPersonParticipant(p, idHint) {
                try {
                    if (!p || typeof p !== 'object') return false;
                    var fake = p.fakeParticipant;
                    if (fake != null && fake !== false && fake !== '')
                        return true;
                    if (p.isVirtualScreenShareParticipant === true
                        || p.isVirtualScreenshareParticipant === true
                        || p.virtualScreenShareOwner)
                        return true;
                    var id = idHint != null ? String(idHint)
                        : (p.id != null ? String(p.id) : '');
                    if (id && (/(-v\d+)$/i.test(id) || /-desktop$/i.test(id)
                        || /_screen/i.test(id) || /-screenshare$/i.test(id)))
                        return true;
                    var name = String(p.name || p.displayName || '');
                    if (/screen share/i.test(name) || /'s screen$/i.test(name)
                        || /демонстрац/i.test(name) || /\bэкран\b/i.test(name))
                        return true;
                } catch (e) {}
                return false;
            }

            function addParticipant(byId, p, isLocal) {
                if (!p || typeof p !== 'object') return;
                var id = p.id != null ? String(p.id) : null;
                if (!id) return;
                if (isNonPersonParticipant(p, id)) return;
                byId[id] = {
                    id: id,
                    name: p.name || p.displayName || (isLocal || p.local ? 'Вы' : 'Участник'),
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

            function coerceTs(value) {
                if (value == null || value === '') return 0;
                if (typeof value === 'number' && isFinite(value) && value > 0) return value;
                if (typeof value === 'string') {
                    var n = parseInt(value, 10);
                    return isFinite(n) && n > 0 ? n : 0;
                }
                return 0;
            }

            function normalizeTs(ts) {
                if (!ts) return 0;
                // seconds -> ms
                if (ts > 0 && ts < 1e12) ts = ts * 1000;
                return ts;
            }

            function parseDurationToMs(text) {
                text = String(text || '').trim();
                var m = text.match(/^(\d{1,3}):([0-5]?\d):([0-5]\d)$/);
                if (m) {
                    return ((parseInt(m[1], 10) * 60 + parseInt(m[2], 10)) * 60 + parseInt(m[3], 10)) * 1000;
                }
                m = text.match(/^([0-5]?\d):([0-5]\d)$/);
                if (m) {
                    return (parseInt(m[1], 10) * 60 + parseInt(m[2], 10)) * 1000;
                }
                return 0;
            }

            function getOnlineMsFromDom() {
                var selectors = [
                    '#conferenceTimer',
                    '[id*="conference-timer"]',
                    '[id*="conferenceTimer"]',
                    '[class*="conference-timer"]',
                    '[class*="conferenceTimer"]',
                    '[class*="roomTimer"]',
                    '[class*="RoomTimer"]'
                ];
                var i, j, els, ms;
                for (i = 0; i < selectors.length; i++) {
                    try {
                        els = document.querySelectorAll(selectors[i]);
                        for (j = 0; j < els.length; j++) {
                            ms = parseDurationToMs(els[j].textContent || els[j].innerText || '');
                            if (ms > 0) return ms;
                        }
                    } catch (eSel) {}
                }

                // Fallback: scan short text nodes for a lone duration (header timer).
                try {
                    var nodes = document.querySelectorAll('div, span, p');
                    for (i = 0; i < nodes.length; i++) {
                        var t = (nodes[i].childNodes.length === 1)
                            ? String(nodes[i].textContent || '').trim()
                            : '';
                        if (!t || t.length > 12) continue;
                        ms = parseDurationToMs(t);
                        if (ms >= 60000) return ms; // ignore tiny mm:ss noise under 1 min unless exact timer el
                        if (ms > 0 && /timer|Timer|duration/i.test(nodes[i].className || nodes[i].id || ''))
                            return ms;
                    }
                } catch (eScan) {}
                return 0;
            }

            function addParticipantsFromDom(byId) {
                try {
                    var names = document.querySelectorAll('span.displayname, .displayname, [id$="_name"], #localDisplayName');
                    for (var i = 0; i < names.length; i++) {
                        var name = String(names[i].textContent || '').trim();
                        if (!name) continue;
                        var id = names[i].id || ('dom-' + i + '-' + name);
                        if (isNonPersonParticipant({ name: name }, id)) continue;
                        if (!byId[id])
                            byId[id] = { id: id, name: name, local: id === 'localDisplayName' };
                    }
                } catch (eDom) {}
            }

            var byId = {};
            var conferenceTimestampMs = 0;
            var onlineMs = 0;
            var confKeys = [];
            var app = (typeof window !== 'undefined') ? window.APP : undefined;
            var appPresent = !!app;

            try {
                onlineMs = getOnlineMsFromDom();

                if (app) {
                    var myId = '';
                    try {
                        if (app.conference && typeof app.conference.getMyUserId === 'function')
                            myId = String(app.conference.getMyUserId() || '');
                    } catch (eMy) {}

                    if (app.store && typeof app.store.getState === 'function') {
                        var state = app.store.getState();
                        var conf = state['features/base/conference'] || {};
                        try { confKeys = Object.keys(conf); } catch (eKeys) {}

                        conferenceTimestampMs = normalizeTs(coerceTs(conf.conferenceTimestamp));

                        var pstate = state['features/base/participants'];
                        if (pstate && typeof pstate === 'object') {
                            addParticipant(byId, pstate.local, true);
                            eachMapLike(pstate.remote, function (p) { addParticipant(byId, p, false); });
                        }
                    }

                    if (!conferenceTimestampMs && app.conference) {
                        try {
                            if (typeof app.conference.getMeetingStartedTimestamp === 'function')
                                conferenceTimestampMs = normalizeTs(coerceTs(app.conference.getMeetingStartedTimestamp()));
                        } catch (eTs) {}
                        try {
                            if (!conferenceTimestampMs && app.conference._room
                                && typeof app.conference._room.getMeetingStartedTimestamp === 'function')
                                conferenceTimestampMs = normalizeTs(coerceTs(app.conference._room.getMeetingStartedTimestamp()));
                        } catch (eTs2) {}
                    }

                    if (Object.keys(byId).length === 0 && app.conference && typeof app.conference.listMembers === 'function') {
                        try {
                            app.conference.listMembers().forEach(function (m) {
                                if (!m) return;
                                var id = m.id != null ? String(m.id)
                                    : (typeof m.getId === 'function' ? String(m.getId() || '') : '');
                                if (!id) return;
                                if (typeof m.isHidden === 'function' && m.isHidden()) return;
                                var member = {
                                    id: id,
                                    name: (m.displayName || m.name || (m.getDisplayName && m.getDisplayName()) || 'Участник'),
                                    fakeParticipant: m.fakeParticipant
                                        || (typeof m.isHidden === 'function' && m.isHidden() ? 'hidden' : null)
                                };
                                if (isNonPersonParticipant(member, id)) return;
                                byId[id] = { id: id, name: member.name, local: !!(myId && id === myId) };
                            });
                        } catch (e) {}
                    }

                    if (Object.keys(byId).length <= 1 && app.conference && app.conference._room) {
                        try {
                            var room = app.conference._room;
                            if (!myId && typeof room.myUserId === 'function')
                                myId = String(room.myUserId() || '');
                            if (myId && !byId[myId])
                                byId[myId] = { id: myId, name: 'Вы', local: true };
                            var parts = room.participants || {};
                            Object.keys(parts).forEach(function (id) {
                                if (!id || byId[String(id)]) return;
                                var p = parts[id];
                                if (!p) return;
                                var entry = {
                                    id: String(id),
                                    name: (p._displayName || p.getDisplayName && p.getDisplayName() || 'Участник')
                                };
                                if (isNonPersonParticipant(entry, id)) return;
                                byId[String(id)] = {
                                    id: String(id),
                                    name: entry.name,
                                    local: !!(myId && String(id) === myId)
                                };
                            });
                        } catch (e) {}
                    }
                }

                // DOM fallback for names (matches filmstrip dump: span.displayname).
                if (Object.keys(byId).length === 0)
                    addParticipantsFromDom(byId);

                if (!onlineMs && conferenceTimestampMs > 0)
                    onlineMs = Date.now() - conferenceTimestampMs;
                if (onlineMs < 0) onlineMs = 0;
            } catch (e) {}

            return JSON.stringify({
                participants: Object.keys(byId).map(function (k) { return byId[k]; }),
                conferenceTimestampMs: conferenceTimestampMs || 0,
                onlineMs: onlineMs || 0,
                appPresent: appPresent,
                confKeys: confKeys
            });
        }
        """;

    private sealed class RoomSnapshot
    {
        public ParticipantSnapshot[] Participants { get; set; }

        public long? ConferenceTimestampMs { get; set; }

        public long? OnlineMs { get; set; }

        public bool? AppPresent { get; set; }

        public string[] ConfKeys { get; set; }
    }

    private sealed class ParticipantSnapshot
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public bool Local { get; set; }
    }

    private sealed class ChatSnapshot
    {
        public ChatMessageDto[] Messages { get; set; }

        public bool? AppPresent { get; set; }

        public bool? Hooked { get; set; }

        public string Error { get; set; }
    }

    private sealed class ChatMessageDto
    {
        public string Id { get; set; }

        public string Text { get; set; }

        public string Sender { get; set; }

        public long? TimestampMs { get; set; }

        public bool Local { get; set; }
    }
}
