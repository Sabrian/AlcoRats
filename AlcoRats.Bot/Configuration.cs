using System.IO;
using System.Text.Json;

namespace AlcoRats.Bot;

public class Configuration
{
    public string Token { get; set; } = "";

    public string TelegramBotName { get; set; } = "@alcoratsbot";

    public string JitsiHost = "https://alkonost-our.site/";

    public string JitsiRoom = "AlcoRats";

    public string JitsiUserLogin = "Ceiling_Cat";

    public string JitsiUserPassword = "Mi6Ohz8joech";

    public string Gravatar = "bot.alcoholic@gmail.com";

    public string BotName = "[BOT] Ceiling Cat";

    /// <summary>
    /// Path to Chromium/Chrome. Empty = try system paths, then download via PuppeteerSharp.
    /// </summary>
    public string ChromiumExecutablePath { get; set; } = "";

    /// <summary>
    /// Persistent Chromium profile (cookies / Secure Domain session). Empty = LocalAppData default.
    /// </summary>
    public string JitsiUserDataDir { get; set; } = "";

    /// <summary>
    /// Remote debugging port for reuse across process restarts. 0 = disable attach.
    /// </summary>
    public int JitsiRemoteDebuggingPort { get; set; } = 9333;

    public bool JitsiHeadless { get; set; } = true;

    /// <summary>
    /// When true, writes HTML/JSON DOM dumps under <c>doms/</c> during join/login.
    /// When false, a no-op <c>IDomSnapshotStore</c> is registered.
    /// </summary>
    public bool SaveDomSnapshots { get; set; } = false;

    /// <summary>
    /// Piper voice id: dmitri | irina | denis | ruslan. Default: dmitri (bundled archive in Speech/).
    /// </summary>
    public string PiperVoice { get; set; } = "dmitri";

    /// <summary>
    /// Directory for Piper extract cache and temp WAVs.
    /// Empty = <c>AppContext.BaseDirectory/Speech</c> when present, else LocalAppData.
    /// </summary>
    public string PiperCacheDir { get; set; } = "";

    /// <summary>
    /// Which <c>IAIChat</c> implementation to register: <c>openrouter</c> | <c>gemini</c> | <c>empty</c>.
    /// </summary>
    public string AiEngine { get; set; } = "openrouter";

    /// <summary>
    /// Gemini Developer API key (AI Studio). Prefer env <c>GEMINI_API_KEY</c> in production.
    /// </summary>
    public string GeminiApiKey { get; set; } = "";

    /// <summary>
    /// Gemini model id for the free Developer API. Default: gemini-flash-latest.
    /// </summary>
    public string GeminiModel { get; set; } = "gemini-flash-latest";

    /// <summary>
    /// OpenRouter API key. Prefer env <c>OPENROUTER_API_KEY</c> / <c>OpenRouterKey</c> in production.
    /// </summary>
    public string OpenRouterKey { get; set; } = "";

    /// <summary>
    /// OpenRouter model id. Default: <c>openrouter/free</c> (free models router).
    /// </summary>
    public string OpenRouterModel { get; set; } = "openrouter/free";

    public static Configuration Instance { get; private set; } = null!;

    static Configuration()
    {
        try
        {
            if (File.Exists("configuration.json"))
            {
                Instance = JsonSerializer.Deserialize<Configuration>(File.ReadAllText("configuration.json")) ?? new Configuration();
            }
            else
            {
                Instance = new Configuration();
            }
        }
        catch
        {
            Instance = new Configuration();
        }

        ApplyEnvironmentOverrides(Instance);
    }

    private static void ApplyEnvironmentOverrides(Configuration config)
    {
        static string Env(string name) => Environment.GetEnvironmentVariable(name);

        if (!string.IsNullOrWhiteSpace(Env("ChromiumExecutablePath")))
            config.ChromiumExecutablePath = Env("ChromiumExecutablePath");
        if (!string.IsNullOrWhiteSpace(Env("JitsiUserDataDir")))
            config.JitsiUserDataDir = Env("JitsiUserDataDir");
        if (int.TryParse(Env("JitsiRemoteDebuggingPort"), out var port))
            config.JitsiRemoteDebuggingPort = port;
        if (bool.TryParse(Env("JitsiHeadless"), out var headless))
            config.JitsiHeadless = headless;
        if (bool.TryParse(Env("SaveDomSnapshots"), out var saveDomSnapshots))
            config.SaveDomSnapshots = saveDomSnapshots;
        if (!string.IsNullOrWhiteSpace(Env("Token")))
            config.Token = Env("Token");
        if (!string.IsNullOrWhiteSpace(Env("TelegramBotName")))
            config.TelegramBotName = Env("TelegramBotName");
        if (!string.IsNullOrWhiteSpace(Env("JitsiHost")))
            config.JitsiHost = Env("JitsiHost");
        if (!string.IsNullOrWhiteSpace(Env("JitsiRoom")))
            config.JitsiRoom = Env("JitsiRoom");
        if (!string.IsNullOrWhiteSpace(Env("JitsiUserLogin")))
            config.JitsiUserLogin = Env("JitsiUserLogin");
        if (!string.IsNullOrWhiteSpace(Env("JitsiUserPassword")))
            config.JitsiUserPassword = Env("JitsiUserPassword");
        if (!string.IsNullOrWhiteSpace(Env("BotName")))
            config.BotName = Env("BotName");
        if (!string.IsNullOrWhiteSpace(Env("PiperVoice")))
            config.PiperVoice = Env("PiperVoice");
        if (!string.IsNullOrWhiteSpace(Env("PiperCacheDir")))
            config.PiperCacheDir = Env("PiperCacheDir");
        var geminiKey = Env("GeminiApiKey");
        if (string.IsNullOrWhiteSpace(geminiKey))
            geminiKey = Env("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(geminiKey))
            config.GeminiApiKey = geminiKey;
        if (!string.IsNullOrWhiteSpace(Env("GeminiModel")))
            config.GeminiModel = Env("GeminiModel");
        if (!string.IsNullOrWhiteSpace(Env("AiEngine")))
            config.AiEngine = Env("AiEngine");
        var openRouterKey = Env("OpenRouterKey");
        if (string.IsNullOrWhiteSpace(openRouterKey))
            openRouterKey = Env("OPENROUTER_API_KEY");
        if (!string.IsNullOrWhiteSpace(openRouterKey))
            config.OpenRouterKey = openRouterKey;
        if (!string.IsNullOrWhiteSpace(Env("OpenRouterModel")))
            config.OpenRouterModel = Env("OpenRouterModel");
    }
}
