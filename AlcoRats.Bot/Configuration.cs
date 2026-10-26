using System.IO;
using System.Text.Json;

namespace AlcoRats.Bot;

public class Configuration
{
    public string Token { get; set; } = "6670124184:AAF9ORrkDLCoo5uK4Km-GpHeAzvtNMWt8rU";

    public string TelegramBotName { get; set; } = "@vkvorovaykabot";

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
    }
}
