namespace AlcoRats.Bot.Models;

class ChatMessage
{
    public string Id { get; set; }

    public string Text { get; set; }

    public string Sender { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public bool IsLocal { get; set; }
}
