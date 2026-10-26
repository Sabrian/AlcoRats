using AlcoRats.Bot.Hosting;
using AlcoRats.Bot.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlcoRats.Bot;

internal class Program
{
    static async Task Main(string[] args)
    {
        var services = AppHost.Initialize();

        try
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Program");
            var configuration = services.GetRequiredService<Configuration>();
            
            logger.LogInformation("Bot started. JitsiHost={JitsiHost}, JitsiRoom={JitsiRoom}",
                configuration.JitsiHost,
                configuration.JitsiRoom);

            var jitsi = services.GetRequiredService<AlcoRats.Bot.Jitsi.IJitsiBotService>();
            _ = Task.Run(async () =>
            {
                try
                {
                    await jitsi.EnsureStartedAsync();
                    logger.LogInformation("Jitsi bot ready (IsRunning={IsRunning})", jitsi.IsRunning);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Initial Jitsi start failed; will retry on commands");
                }
            });

            var main = services.GetRequiredService<IServiceMain>();
            await main.Process();
        }
        finally
        {
            AppHost.Shutdown();
        }
    }
}
