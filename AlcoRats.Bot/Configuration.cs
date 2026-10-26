using System.IO;
using System.Text.Json;

namespace AlcoRats.Bot;

public class Configuration
{
    public string TlToken { get; set; } = "6670124184:AAF9ORrkDLCoo5uK4Km-GpHeAzvtNMWt8rU";

    public string JitsiHost = "https://alkonost-our.site/";

    public string JitsiRoom = "AlcoRats";

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
