using AlcoRats.Bot.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace AlcoRats.Bot.Services
{
    class CommandProcessor : ICommandProcessor
    {
        public Task Process(Command command)
        {
            return Task.CompletedTask;
        }
    }
}
