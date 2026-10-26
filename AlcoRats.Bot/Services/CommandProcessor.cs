using AlcoRats.Bot.Jitsi;
using AlcoRats.Bot.Models;
using Microsoft.Extensions.Logging;

namespace AlcoRats.Bot.Services;

class CommandProcessor : ICommandProcessor
{
    private readonly IJitsiBotService _jitsiBot;
    private readonly Configuration _configuration;
    private readonly IDatabase _database;
    private readonly ILogger<CommandProcessor> _logger;
    private readonly IClientProvider _clientProvider;


    public CommandProcessor(IJitsiBotService jitsiBot, Configuration configuration, ILogger<CommandProcessor> logger, IDatabase database, IClientProvider clientProvider)
    {
        _logger = logger;
        _jitsiBot = jitsiBot;
        _configuration = configuration;
        _database = database;
        _clientProvider = clientProvider;
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
                    _database.AddSubscriber(new Subscriber { Id = command.ChatId, Name = command.Owner });
                    await command.Response($"{command.Owner} упешно подписан(а)");
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

            case CommandKind.Message:
                foreach(var subscriber in _database.GetSubscribers().ToEnumerable())
                {
                    await _clientProvider.GetClient(subscriber.Id)?.Response($"Отправитель: {command.Arguments.FirstOrDefault()}\r\nАбракадабра!");
                }
                break;
        }
    }
}
