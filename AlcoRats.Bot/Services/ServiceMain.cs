using System;
using System.Collections.Generic;
using System.Text;

namespace AlcoRats.Bot.Services
{
    class ServiceMain : IServiceMain
    {
        private readonly Configuration _configuration;

        private readonly ICommandProvider _commandProvider;

        private readonly ICommandProcessor _commandProcessor;

        public ServiceMain(Configuration configuration, ICommandProvider commandProvider, ICommandProcessor commandProcessor)
        {
            _configuration = configuration;
            _commandProvider = commandProvider;
            _commandProcessor = commandProcessor;
        }

        public async Task Process()
        {
            while (true)
            {
                var command = await _commandProvider.Dequeue();
                await _commandProcessor.Process(command);
            }
        }
    }
}
