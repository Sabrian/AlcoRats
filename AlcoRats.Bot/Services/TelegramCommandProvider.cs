using System.Text;
using AlcoRats.Bot.Core;
using AlcoRats.Bot.Models;
using Microsoft.Extensions.Logging;
using Serilog;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AlcoRats.Bot.Services;

class TelegramCommandProvider : ICommandProvider, IClientProvider
{
    private static readonly TimeSpan TransientLogInterval = TimeSpan.FromSeconds(60);

    private readonly MessageQueue<ICommand> _commands = new();
    private readonly Configuration _configuration;
    private readonly ILogger<TelegramCommandProvider> _logger;
    private readonly Lock _lock = new();

    private TelegramBotClient _client;
    private CancellationTokenSource _receiveCts;
    private DateTime _lastTransientLogUtc = DateTime.MinValue;
    private int _suppressedTransientErrors;

    public TelegramCommandProvider(Configuration configuration, ILogger<TelegramCommandProvider> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private void EnsureInitialized()
    {
        if (_client != null)
            return;

        lock (_lock)
        {
            if (_client != null)
                return;

            _receiveCts = new CancellationTokenSource();
            _client = new TelegramBotClient(_configuration.Token);
            _client.ReceiveAsync(
                async (botClient, update, _) =>
                {
                    if (update.Message == null)
                        return;

                    var from = update.Message.From;
                    if (from == null)
                        return;

                    if (update.Message.Date < DateTime.UtcNow.AddMinutes(-1))
                        return;

                    if (update.Message.Chat?.Id is not { } chatId)
                        return;

                    var splitters = " \r\n\t/".ToCharArray();
                    var message = update.Message.Text?.Trim(splitters) ?? string.Empty;
                    if (update.Message.ReplyToMessage is { } reply && reply.From is { } original && original.IsBot && ("@" + original.Username.ToLower() == _configuration.TelegramBotName))
                    {
                        _commands.Enqueue(
                            new Command(botClient)
                            {
                                Owner = update.Message.Chat?.Title ?? from.Username ?? $"{from.FirstName} {from.LastName}".Trim(),
                                Id = update.Message.Id,
                                Kind = CommandKind.Message,
                                Arguments = [ message ],
                                User = new ProviderUser
                                {
                                    Id = from.Id,
                                    FirstName = from.FirstName,
                                    LastName = from.LastName,
                                    UserName = from.Username,
                                },
                                ChatId = chatId,
                            });

                        return;
                    }




                    if (chatId != from.Id)
                    {
                        if (message.ToLower().StartsWith($"{_configuration.TelegramBotName}"))
                        {
                            message = message[_configuration.TelegramBotName.Length..];
                        }
                        else
                        {
                            return;
                        }
                    }

                    var words = message.Split(splitters, StringSplitOptions.RemoveEmptyEntries);
                    if (words.FirstOrDefault() is not { } cmd)
                    {
                        cmd = "list";
                    }

                    if(cmd.StartsWith("/") || cmd.StartsWith("\\"))
                    {
                        cmd = cmd.Substring(1);
                    }

                    var at = cmd.IndexOf('@');
                    if (at >= 0)
                        cmd = cmd[..at];

                    if (!Enum.TryParse<CommandKind>(cmd, true, out var kind))
                        return;

                    _commands.Enqueue(
                        new Command(botClient)
                        {
                            Owner = update.Message.Chat?.Title ?? from.Username ?? $"{from.FirstName} {from.LastName}".Trim(),
                            Id = update.Message.Id,
                            Kind = kind,
                            Arguments = words.Skip(1).ToArray(),
                            User = new ProviderUser
                            {
                                Id = from.Id,
                                FirstName = from.FirstName,
                                LastName = from.LastName,
                                UserName = from.Username,
                            },
                            ChatId = chatId,
                        });
                },
                async (_, exception, ct) =>
                {
                    // Do not recreate the client here — that causes getUpdates 409 and restart loops.
                    // Telegram.Bot keeps polling; we only need sane logging + brief backoff on outages.
                    if (IsTransientTelegramError(exception))
                    {
                        var now = DateTime.UtcNow;
                        if (now - _lastTransientLogUtc >= TransientLogInterval)
                        {
                            var suppressed = Interlocked.Exchange(ref _suppressedTransientErrors, 0);
                            _logger.LogWarning(
                                exception,
                                "Telegram API temporary outage ({Message}). Polling continues.{Suppressed}",
                                exception.Message,
                                suppressed > 0 ? $" Suppressed {suppressed} similar error(s)." : "");
                            _lastTransientLogUtc = now;
                        }
                        else
                        {
                            Interlocked.Increment(ref _suppressedTransientErrors);
                        }

                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(3), ct);
                        }
                        catch (OperationCanceledException)
                        {
                            // shutting down
                        }

                        return;
                    }

                    _logger.LogError(exception, "Telegram receive error: {Message}", exception.Message);
                },
                cancellationToken: _receiveCts.Token);
        }
    }

    private static bool IsTransientTelegramError(Exception exception)
    {
        for (var e = exception; e != null; e = e.InnerException)
        {
            if (e is ApiRequestException api)
            {
                // 502/503/504 and similar gateway blips from api.telegram.org
                if (api.ErrorCode is >= 500 and < 600)
                    return true;
                if (api.Message.Contains("Bad Gateway", StringComparison.OrdinalIgnoreCase)
                    || api.Message.Contains("Gateway Timeout", StringComparison.OrdinalIgnoreCase)
                    || api.Message.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (e is RequestException)
                return true;

            if (e is TimeoutException or TaskCanceledException or HttpRequestException or IOException)
                return true;

            var msg = e.Message ?? "";
            if (msg.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Bad Gateway", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Gateway Timeout", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public async Task<ICommand> Dequeue()
    {
        EnsureInitialized();
        return await _commands.Dequeue();
    }

    public IClient GetClient(long id)
    {
        EnsureInitialized();
        return new Client(_client) { ChatId = id };
    }

    class Client: IClient
    {
        private readonly ITelegramBotClient _client;

        public long ChatId { get; set; }

        public int Id { get; set; }

        public ProviderUser User { get; set; }

        public Client(ITelegramBotClient client)
        {
            _client = client;
        }

        public async Task Response(string message, MessageMarkup markup = MessageMarkup.None)
        {
            try
            {
                ReplyParameters replyParameters = null;
                if (User != null && ChatId != User.Id && Id != 0)
                    replyParameters = Id;

                var parseMode = ToParseMode(markup);

                var lines = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                var buffer = new StringBuilder();
                foreach (var line in lines)
                {
                    if (line.Length + buffer.Length > 4000)
                    {
                        await _client.SendMessage(new ChatId(ChatId), buffer.ToString(), parseMode: parseMode, replyParameters: replyParameters);
                        buffer.Clear();
                    }
                    else
                        buffer.AppendLine(line);
                }

                if (buffer.Length > 0)
                    await _client.SendMessage(new ChatId(ChatId), buffer.ToString(), parseMode: parseMode, replyParameters: replyParameters);
            }
            catch (Exception e)
            {
                Log.Error(e, e.Message);
            }
        }

        private static ParseMode ToParseMode(MessageMarkup markup) => markup switch
        {
            MessageMarkup.Markdown => ParseMode.Markdown,
            MessageMarkup.MarkdownV2 => ParseMode.MarkdownV2,
            MessageMarkup.Html => ParseMode.Html,
            _ => ParseMode.None,
        };
    }

    class Command : Client, ICommand
    {

        public Command(ITelegramBotClient client): base(client)
        {
        }

        public CommandKind Kind { get; set; }
        public string[] Arguments { get; set; }
        public string Owner { get; set; }
    }
}
