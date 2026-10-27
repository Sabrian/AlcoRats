using System.Text.Json;
using AlcoRats.Bot.Models;
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
    private readonly SemaphoreSlim _gate = new(1, 1);

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

    public JitsiBotService(Configuration configuration, ILogger<JitsiBotService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsRunning => _starting || _browser is { IsClosed: false };

    public event EventHandler<ChatMessage> ChatMessageReceived;

    public Task EnsureStartedAsync(CancellationToken cancellationToken = default)
        => StartAsync(cancellationToken);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureCredentialsConfigured();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_starting)
            {
                _logger.LogInformation("Jitsi bot start already in progress");
                return;
            }

            if (_browser is { IsClosed: false } && _page != null)
            {
                if (await IsJoinedAsync(_page))
                {
                    _joinedAt ??= DateTime.UtcNow;
                    _logger.LogDebug("Jitsi bot already joined");
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
        }
    }

    private async Task MonitorChatAsync(CancellationToken cancellationToken)
    {
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
                if (!_chatBaselineReady)
                {
                    foreach (var message in messages)
                    {
                        if (!string.IsNullOrWhiteSpace(message?.Id))
                            _seenChatMessageIds.Add(message.Id);
                    }

                    _chatBaselineReady = true;
                    _logger.LogInformation(
                        "Chat baseline captured ({Count} messages, appPresent={AppPresent}, hooked={Hooked})",
                        _seenChatMessageIds.Count,
                        snapshot?.AppPresent,
                        snapshot?.Hooked);
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

                        var chatMessage = new ChatMessage
                        {
                            Id = message.Id,
                            Text = message.Text.Trim(),
                            Sender = string.IsNullOrWhiteSpace(message.Sender) ? "Unknown" : message.Sender.Trim(),
                            Timestamp = message.TimestampMs is > 0
                                ? DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampMs.Value)
                                : DateTimeOffset.UtcNow,
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
            $"--remote-debugging-port={port}",
            $"--user-data-dir={userDataDir}",
        };

        var launchOptions = new LaunchOptions
        {
            Headless = _configuration.JitsiHeadless,
            Args = args.ToArray(),
            DefaultViewport = null,
            DumpIO = true,
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
        var email = _configuration.Gravatar ?? string.Empty;

        var url = page.Url ?? string.Empty;
        if (!url.StartsWith(host, StringComparison.OrdinalIgnoreCase))
        {
            await page.GoToAsync(host, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                Timeout = 60_000,
            });
        }

        await page.EvaluateFunctionAsync(
            """
            (name, email) => {
                localStorage.setItem('features/base/profile', JSON.stringify({
                    displayName: name,
                    email: email
                }));
                localStorage.setItem('displayname', name);
                localStorage.setItem('email', email);
            }
            """,
            botName,
            email);

        _logger.LogInformation("Bot profile cached: {BotName} ({Email})", botName, email);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task JoinConferenceAsync(IPage page, CancellationToken cancellationToken)
    {
        var host = NormalizeHost(_configuration.JitsiHost);
        var room = (_configuration.JitsiRoom ?? string.Empty).Trim().Trim('/');
        var roomUrl = $"{host}/{room.ToLowerInvariant()}";
        var joinUrl =
            $"{roomUrl}#config.startWithAudioMuted=true&config.startWithVideoMuted=true";

        _logger.LogInformation("Navigating to room {RoomUrl}", roomUrl);
        await page.GoToAsync(joinUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = 60_000,
        });

        // Login dialog may appear before or after the prejoin Join click.
        await SignInAsAccountAsync(page, requireDialog: false, timeout: TimeSpan.FromSeconds(12), cancellationToken);

        if (await IsJoinedAsync(page))
        {
            _joinedAt = DateTime.UtcNow;
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

        await SignInAsAccountAsync(page, requireDialog: true, timeout: TimeSpan.FromSeconds(25), cancellationToken);

        await WaitUntilJoinedAsync(page, TimeSpan.FromSeconds(30), cancellationToken);
        _joinedAt = DateTime.UtcNow;
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

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsJoinedAsync(page) || await HasAuthenticatedSessionAsync(page))
            {
                _logger.LogInformation("Using existing authenticated Jitsi session");
                return;
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
            await Task.Delay(400, cancellationToken);
        }

        if (!submitted)
        {
            if (await IsJoinedAsync(page) || await HasAuthenticatedSessionAsync(page))
            {
                _logger.LogInformation("No login form — authenticated session already present");
                return;
            }

            if (!requireDialog)
            {
                _logger.LogInformation("Login dialog not shown yet — will retry after join");
                return;
            }

            throw new InvalidOperationException(
                "Account login dialog did not appear; refusing anonymous join.");
        }

        _logger.LogInformation("Submitted Secure Domain credentials for {Login}", login);

        var hideDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < hideDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsLoginDialogVisibleAsync(page))
                break;
            if (await IsJoinedAsync(page) || await HasAuthenticatedSessionAsync(page))
                break;
            await Task.Delay(500, cancellationToken);
        }

        if (await IsLoginDialogVisibleAsync(page))
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
                                || label === 'войти') {
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
            await page.EvaluateFunctionAsync(
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

                    var nodes = document.querySelectorAll('a, button, [role="button"], div[class*="button"]');
                    for (var i = 0; i < nodes.length; i++) {
                        var el = nodes[i];
                        if (!visible(el)) continue;
                        var label = (
                            (el.innerText || el.textContent || '') + ' ' +
                            (el.getAttribute('aria-label') || '')
                        ).toLowerCase().replace(/\s+/g, ' ').trim();
                        if (!label) continue;
                        if (label.indexOf('i am the host') >= 0
                            || label.indexOf('i\'m the host') >= 0
                            || label.indexOf('login') >= 0
                            || label.indexOf('sign in') >= 0
                            || label.indexOf('войти') >= 0
                            || label.indexOf('авториз') >= 0
                            || label.indexOf('я ведущий') >= 0
                            || label.indexOf('я организатор') >= 0
                            || label.indexOf('join in browser') >= 0
                            || label.indexOf('присоединиться в браузер') >= 0
                            || label.indexOf('продолжить в браузер') >= 0) {
                            el.click();
                            return true;
                        }
                    }
                    return false;
                }
                """);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auth trigger click skipped");
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
            function meta(el) {
                return (
                    (el.name || '') + ' ' + (el.id || '') + ' ' + (el.placeholder || '') + ' ' +
                    (el.getAttribute('aria-label') || '') + ' ' + (el.getAttribute('autocomplete') || '')
                ).toLowerCase();
            }
            var text = ((document.body && (document.body.innerText || document.body.textContent)) || '').toLowerCase();
            if (text.indexOf('требуется авторизация') >= 0
                || text.indexOf('authentication required') >= 0
                || text.indexOf('login required') >= 0
                || text.indexOf('идентификатор пользователя') >= 0
                || text.indexOf('пароль пользователя') >= 0)
                return true;

            var user = null, pass = null;
            var inputs = document.querySelectorAll('input');
            for (var i = 0; i < inputs.length; i++) {
                var el = inputs[i];
                if (!visible(el)) continue;
                var type = (el.type || 'text').toLowerCase();
                var m = meta(el);
                if (type === 'password') pass = el;
                else if (type === 'text' || type === 'email' || type === 'username' || type === '') {
                    if (m.indexOf('user') >= 0 || m.indexOf('login') >= 0 || m.indexOf('ident') >= 0
                        || m.indexOf('пользовател') >= 0 || m.indexOf('логин') >= 0)
                        user = el;
                }
            }
            return !!(pass && (user || pass));
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
                var proto = window.HTMLInputElement && window.HTMLInputElement.prototype;
                var descriptor = proto && Object.getOwnPropertyDescriptor(proto, 'value');
                if (descriptor && descriptor.set) descriptor.set.call(el, value);
                else el.value = value;
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
            }

            var userEl = null, passEl = null;
            var inputs = document.querySelectorAll('input');
            for (var i = 0; i < inputs.length; i++) {
                var el = inputs[i];
                if (!visible(el)) continue;
                var type = (el.type || 'text').toLowerCase();
                var m = meta(el);
                if (type === 'password') {
                    if (!passEl) passEl = el;
                    continue;
                }
                if (type !== 'text' && type !== 'email' && type !== 'username' && type !== '')
                    continue;
                if (m.indexOf('user') >= 0 || m.indexOf('login') >= 0 || m.indexOf('ident') >= 0
                    || m.indexOf('пользовател') >= 0 || m.indexOf('логин') >= 0 || m.indexOf('name') >= 0
                    || el.name === 'username' || el.id === 'login_username')
                    userEl = el;
            }
            if (passEl && !userEl) {
                for (var j = 0; j < inputs.length; j++) {
                    var t = inputs[j];
                    if (!visible(t) || t === passEl) continue;
                    var tt = (t.type || 'text').toLowerCase();
                    if (tt === 'text' || tt === 'email' || tt === 'username' || tt === '') {
                        userEl = t;
                        break;
                    }
                }
            }

            // Need at least a password field (Secure Domain / JWT login).
            if (!passEl)
                return false;

            if (userEl) setValue(userEl, user || '');
            setValue(passEl, password || '');

            var submit = document.querySelector(
                'button[type="submit"], input[type="submit"], #login_button, button.primary');
            if (submit && visible(submit)) {
                submit.click();
                return true;
            }

            var nodes = document.querySelectorAll('button, [role="button"], a');
            for (var k = 0; k < nodes.length; k++) {
                var n = nodes[k];
                if (!visible(n)) continue;
                var label = ((n.innerText || n.textContent || '') + ' ' + (n.getAttribute('aria-label') || ''))
                    .toLowerCase();
                if (label.indexOf('login') >= 0 || label.indexOf('sign in') >= 0
                    || label.indexOf('войти') >= 0 || label.indexOf('ok') >= 0
                    || label.indexOf('продолжить') >= 0 || label.indexOf('submit') >= 0) {
                    n.click();
                    return true;
                }
            }

            if (passEl.form) {
                passEl.form.requestSubmit ? passEl.form.requestSubmit() : passEl.form.submit();
                return true;
            }

            passEl.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
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
                        if (conf.authRequired === false && conf.conference)
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

    private const string InstallChatHookScript =
        """
        function() {
            try {
                var app = window.APP;
                if (!window.__arChat) {
                    window.__arChat = { hooked: false, queue: [], seen: {} };
                }
                var hook = window.__arChat;
                if (hook.hooked)
                    return JSON.stringify({ ok: true, hooked: true });
                if (!app || !app.store || typeof app.store.getState !== 'function')
                    return JSON.stringify({ ok: false, hooked: false, reason: 'no-app' });

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

                function enqueueFromStore() {
                    try {
                        var chat = app.store.getState()['features/chat'] || {};
                        var messages = chat.messages || [];
                        for (var i = 0; i < messages.length; i++) {
                            var mapped = mapMessage(messages[i]);
                            if (!mapped || hook.seen[mapped.id]) continue;
                            hook.seen[mapped.id] = true;
                            hook.queue.push(mapped);
                        }
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
                                var mid = String(ts || Date.now()) + ':' + String(id || '') + ':' + String(text || '');
                                if (hook.seen[mid]) return;
                                hook.seen[mid] = true;
                                var myId2 = '';
                                try { myId2 = String(conf.getMyUserId && conf.getMyUserId() || ''); } catch (e2) {}
                                hook.queue.push({
                                    id: mid,
                                    text: String(text || ''),
                                    sender: String(name || id || ''),
                                    timestampMs: typeof ts === 'number' ? (ts < 1e12 ? ts * 1000 : ts) : Date.now(),
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

                // Drain event queue first (from store.subscribe / conference.on).
                if (window.__arChat && window.__arChat.queue && window.__arChat.queue.length) {
                    result = window.__arChat.queue.splice(0, window.__arChat.queue.length);
                }

                // Also snapshot current store messages (dedupe happens in C#).
                if (app && app.store && typeof app.store.getState === 'function') {
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
                    name: p.name || p.displayName || (isLocal || p.local ? 'Вы' : 'Участник')
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
                        if (!byId[id])
                            byId[id] = { name: name };
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
                                byId[id] = { name: member.name };
                            });
                        } catch (e) {}
                    }

                    if (Object.keys(byId).length <= 1 && app.conference && app.conference._room) {
                        try {
                            var room = app.conference._room;
                            if (!myId && typeof room.myUserId === 'function')
                                myId = String(room.myUserId() || '');
                            if (myId && !byId[myId])
                                byId[myId] = { name: 'Вы' };
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
                                byId[String(id)] = { name: entry.name };
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
        public string Name { get; set; }
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
