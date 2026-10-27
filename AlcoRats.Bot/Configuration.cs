using System.IO;
using System.Text.Json;

namespace AlcoRats.Bot;

public class Configuration
{
    public string Token { get; set; } = "8886065444:AAG3j0TdDYv7Yn8yy5k-B9hk-gxKPVFatdk";

    public string TelegramBotName { get; set; } = "@alcoratsbot";

    public string JitsiHost = "https://alkonost-our.site/";

    public string JitsiRoom = "AlcoRats";

    public string JitsiUserLogin = "Ceiling_Cat";

    public string JitsiUserPassword = "Mi6Ohz8joech";

    public string Gravatar = "";

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
    }
}
