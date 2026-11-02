namespace AlcoRats.Bot.Models;

class ChatMessage
{
    public string Id { get; set; }

    public string Text { get; set; }

    public string Sender { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    /// <summary>True when the message was sent by the local Jitsi participant (this bot).</summary>
    public bool IsLocal { get; set; }

    /// <summary>True when the message is a private (1:1) chat, not the room group chat.</summary>
    public bool IsPrivate { get; set; }

    /// <summary>Private-message recipient display name when known; otherwise null/empty.</summary>
    public string Recipient { get; set; }
}
