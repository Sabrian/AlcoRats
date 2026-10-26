using AlcoRats.Bot.Models;

namespace AlcoRats.Bot.Jitsi;

interface IJitsiBotService
{
    bool IsRunning { get; }

    /// <summary>
    /// Starts the bot if needed (connects to an existing Chromium when possible).
    /// </summary>
    Task EnsureStartedAsync(CancellationToken cancellationToken = default);

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<Room> GetRoom();

    Task StopAsync();
}
