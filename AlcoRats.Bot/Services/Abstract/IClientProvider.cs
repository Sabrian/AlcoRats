using AlcoRats.Bot.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace AlcoRats.Bot.Services
{
    interface IClientProvider
    {
        IClient GetClient(long id);
    }
}
