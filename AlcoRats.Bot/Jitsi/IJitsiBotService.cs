using AlcoRats.Bot.Models;

namespace AlcoRats.Bot.Jitsi;

interface IJitsiBotService
{
    bool IsRunning { get; }

    /// <summary>
    /// Raised when a new message appears in the Jitsi room chat.
    /// </summary>
    event EventHandler<ChatMessage> ChatMessageReceived;

    /// <summary>
    /// Raised when a real user joins the room after the bot's presence baseline.
    /// Screen-share / virtual participants do not raise this.
    /// </summary>
    event EventHandler<RoomParticipant> UserConnected;

    /// <summary>
    /// Raised when a real user leaves the room after the bot's presence baseline.
    /// Ending a screen share does not raise this.
    /// </summary>
    event EventHandler<RoomParticipant> UserDisconnected;

    /// <summary>
    /// Starts the bot if needed (connects to an existing Chromium when possible).
    /// </summary>
    Task EnsureStartedAsync(CancellationToken cancellationToken = default);

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<Room> GetRoom();

    Task StopAsync();

    Task SendMessage(string message);
}
