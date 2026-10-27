namespace AlcoRats.Bot.Models;

interface IClient
{
    /// <param name="markup">
    /// How Telegram should parse <paramref name="message"/>.
    /// Default <see cref="MessageMarkup.None"/> — plain text, no entities.
    /// </param>
    Task Response(string message, MessageMarkup markup = MessageMarkup.None);
}

/// <summary>
/// Text markup for Telegram Bot API <c>parse_mode</c>.
/// </summary>
enum MessageMarkup
{
    /// <summary>Plain text — no parse mode (default).</summary>
    None = 0,

    /// <summary>Legacy Telegram Markdown (<c>parse_mode=Markdown</c>).</summary>
    Markdown,

    /// <summary>Telegram MarkdownV2 (<c>parse_mode=MarkdownV2</c>).</summary>
    MarkdownV2,

    /// <summary>Telegram HTML (<c>parse_mode=HTML</c>).</summary>
    Html,
}
