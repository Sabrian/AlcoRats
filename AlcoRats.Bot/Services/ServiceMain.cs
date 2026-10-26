using System;
using System.Collections.Generic;
using System.Text;

namespace AlcoRats.Bot.Services
{
    public class ServiceMain : IServiceMain
    {
        private readonly Configuration _configuration;

        public ServiceMain(Configuration configuration)
        {
            _configuration = configuration;
        }

        public Task Process()
        {
            return Task.Delay(10000);
        }
    }
}
