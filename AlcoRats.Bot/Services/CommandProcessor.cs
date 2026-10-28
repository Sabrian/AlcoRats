using AlcoRats.Bot.Jitsi;
using AlcoRats.Bot.Models;
using Microsoft.Extensions.Logging;

namespace AlcoRats.Bot.Services;

class CommandProcessor : ICommandProcessor
{
    private const string HELP = "Использование:\r\n@AlcoratsBot: посмотреть список участников конференции\r\n@AlcoratsBot `golive` или `go`: получить ссылку на конференцию\r\n@AlcoratsBot `app`: получить ссылку на приложение\r\n@AloratsBot `subscribe`, `subscribe all` или `unsubscribe`: управление подпиской\r\nОтветьте на любое сообщение @AlcoratsBot для того чтобы написать в конференцию";

    private readonly IJitsiBotService _jitsiBot;
    private readonly Configuration _configuration;
    private readonly IDatabase _database;
    private readonly ILogger<CommandProcessor> _logger;
    private readonly IClientProvider _clientProvider;

    private readonly IAIChat _aIChat;


    public CommandProcessor(IJitsiBotService jitsiBot, Configuration configuration, ILogger<CommandProcessor> logger, IDatabase database, IClientProvider clientProvider, IAIChat aIChat)
    {
        _logger = logger;
        _jitsiBot = jitsiBot;
        _configuration = configuration;
        _database = database;
        _clientProvider = clientProvider;

        _jitsiBot.ChatMessageReceived += OnJitsiChatMessageReceived;
        _jitsiBot.UserConnected += OnJitsiUserConnected;
        _jitsiBot.UserDisconnected += OnJitsiUserDisconnected;
        _aIChat = aIChat;
    }

    private async void OnJitsiChatMessageReceived(object sender, ChatMessage message)
    {
        if (message == null || message.IsLocal || string.IsNullOrWhiteSpace(message.Text))
            return;

        try
        {
            var list = message.Text.ToLower();
            var anonce = message.Text.StartsWith("@@");
            if (anonce)
            {
                message.Text = message.Text[2..].Trim();
            }

            var subscribers = _database.GetSubscribers();
            foreach (var subscriber in subscribers)
            {
                if (subscriber.All || anonce)
                {
                    var client = _clientProvider.GetClient(subscriber.Id);
                    if (client == null)
                        continue;

                    await client.Response($"[{message.Sender}]\n{message.Text}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to forward Jitsi chat message to Telegram subscribers");
        }
    }

    private async void OnJitsiUserConnected(object sender, RoomParticipant participant)
    {
        if (participant == null || string.IsNullOrWhiteSpace(participant.Name))
            return;

        await NotifyAllSubscribersAsync($"{participant.Name} подключился к конференции");
    }

    private async void OnJitsiUserDisconnected(object sender, RoomParticipant participant)
    {
        if (participant == null || string.IsNullOrWhiteSpace(participant.Name))
            return;

        await NotifyAllSubscribersAsync($"{participant.Name} отключился от конференции");
    }

    private async Task NotifyAllSubscribersAsync(string text)
    {
        try
        {
            foreach (var subscriber in _database.GetSubscribers())
            {
                if (!subscriber.All)
                    continue;

                var client = _clientProvider.GetClient(subscriber.Id);
                if (client == null)
                    continue;

                await client.Response(text);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to notify All subscribers: {Text}", text);
        }
    }

    public async Task Process(ICommand command)
    {
        try
        {
            await _jitsiBot.EnsureStartedAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure Jitsi bot is running");
            await command.Response("Jitsi недоступен: " + ex.Message);
            return;
        }

        switch (command.Kind)
        {
            case CommandKind.Golive:
                _logger.LogInformation("Golive requested by {User}", command.User?.UserName ?? command.User?.Id.ToString());
                await command.Response(_jitsiBot.IsRunning ? $"{_configuration.JitsiHost}{_configuration.JitsiRoom}" : $"Не удалось соединится с {_configuration.JitsiRoom}");
                break;

            case CommandKind.List:
            {
                var room = await _jitsiBot.GetRoom();
                var members = room.Members is { Count: > 0 }
                    ? string.Join("\n", room.Members.Where(item => item.Name != _configuration.BotName).Select(m => "• " + m.Name))
                    : "(пусто)";
                await command.Response(
                    $"Комната: {room.Name}\nОнлайн: {room.Online:hh\\:mm\\:ss}\nУчастники:\n{members}");
                break;
            }

            case CommandKind.Subscribe:
            {
                if (_database.GetSubscriber(command.ChatId) is { } subscriber)
                {
                    await command.Response($"{command.Owner} уже подписан(а)");
                }
                else
                {
                    var all = (command.Arguments.FirstOrDefault() == "all" && !command.IsGroup);
                    _database.AddSubscriber(new Subscriber { Id = command.ChatId, Name = command.Owner, All = all });
                    if (all)
                    {
                        await command.Response($"{command.Owner} упешно подписан(а) на все");
                    }
                    else
                    {
                        await command.Response($"{command.Owner} упешно подписан(а)");
                    }
                }
                break;
            }

            case CommandKind.Unsubscribe:
            {
                if (_database.GetSubscriber(command.ChatId) is { } subscriber)
                {
                    _database.RemoveSubscriber(new Subscriber { Id = command.ChatId });
                    await command.Response($"{command.Owner} упешно отписана(а)");
                }
                else
                {
                    await command.Response($"{command.Owner} не был(а) подписан(а)");
                }
                break;
            }

            case CommandKind.Help:
                await command.Response(HELP, MessageMarkup.Markdown);
                break;

            case CommandKind.Message:
                var text = command.Arguments.FirstOrDefault();
                if (!string.IsNullOrEmpty(text))
                {
                    await _jitsiBot.SendMessage($"[{command.UserName}]: {text}");
                    await _jitsiBot.SpeakAsync(await AIChat(command.UserName, text));
                }
                break;
        }
    }

    private async Task<string> AIChat(string name, string message)
    {
        return await _aIChat.Query($"Отвечай кратко, если не попросят иначе. На следущей строке обращение участника конференции: {name}\r\n{message}");
    }
}
