using AlcoRats.Bot.Core;
using AlcoRats.Bot.Models;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace AlcoRats.Bot.Services;

class TelegramCommandProvider : ICommandProvider, IClientProvider
{
    private readonly MessageQueue<ICommand> _commands = new();
    private readonly Configuration _configuration;
    private readonly ILogger<TelegramCommandProvider> _logger;
    private readonly Lock _lock = new();

    private TelegramBotClient _client;
    private CancellationTokenSource _receiveCts;

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
                (_, exception, _) =>
                {
                    // Do not recreate the client here — that causes getUpdates 409 and restart loops.
                    _logger.LogError(exception, "Telegram receive error: {Message}", exception.Message);
                },
                cancellationToken: _receiveCts.Token);
        }
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

        public async Task Response(string message)
        {
            try
            {
                ReplyParameters replyParameters = null;
                if (User != null && ChatId != User.Id && Id != 0)
                    replyParameters = Id;

                var lines = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                var buffer = new StringBuilder();
                foreach (var line in lines)
                {
                    if (line.Length + buffer.Length > 4000)
                    {
                        await _client.SendMessage(new ChatId(ChatId), buffer.ToString(), replyParameters: replyParameters);
                        buffer.Clear();
                    }
                    else
                        buffer.AppendLine(line);
                }

                if (buffer.Length > 0)
                    await _client.SendMessage(new ChatId(ChatId), buffer.ToString(), replyParameters: replyParameters);
            }
            catch (Exception e)
            {
                Log.Error(e, e.Message);
            }
        }
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
