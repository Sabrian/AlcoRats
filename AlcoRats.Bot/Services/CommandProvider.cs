using AlcoRats.Bot.Core;
using AlcoRats.Bot.Models;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Telegram.Bot;

namespace AlcoRats.Bot.Services
{
    class CommandProvider : ICommandProvider
    {
        private readonly MessageQueue<Command> _commands = new MessageQueue<Command>();

        private readonly Configuration _configuration;

        private readonly ILogger<CommandProvider> _logger;

        public CommandProvider(Configuration configuration, ILogger<CommandProvider> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        private TelegramBotClient _client;

        private bool _initialized;

        private Lock _lock = new Lock();

        private void EnsureInitialized()
        {
            if (_client != null) return;

            lock (_lock)
            {
                _client?.Close();
                _client = new TelegramBotClient(_configuration.Token);
                _client.ReceiveAsync(async (botClient, update, _) =>
                {
                    if (update.Message == null)
                        return;

                    var from = update.Message.From;
                    if (from == null)
                        return;

                    if (update.Message.Date < DateTime.UtcNow.AddMinutes(-1))
                        return;

                    var splitters = " \r\n\t/".ToCharArray();
                    var trimmed = update.Message?.Text?.Trim(splitters) ?? string.Empty;
                    var words = trimmed.Split(splitters, StringSplitOptions.RemoveEmptyEntries);
                    if (words.FirstOrDefault() is not { } cmd)
                        return;

                    if (Enum.TryParse<CommandKind>(cmd, true, out var kind))
                    {
                        _commands.Enqueue
                        (
                            new Command { Kind = kind, Arguments = words.Skip(1).ToArray(), User = new User { Id = from.Id, FirstName = from.FirstName, LastName = from.LastName, UserName = from.Username } }
                        );

                    }
                },
                (_, exception, _) =>
                {
                    _logger.LogError(exception, exception.Message);
                    _client?.Close();
                    _client = null;
                });
            }
        }

        public async Task<Command> Dequeue()
        {
            EnsureInitialized();

            return await _commands.Dequeue();
        }
    }
}
