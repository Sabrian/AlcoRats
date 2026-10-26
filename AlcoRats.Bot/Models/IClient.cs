using System;
using System.Collections.Generic;
using System.Text;

namespace AlcoRats.Bot.Models
{
    interface IClient
    {
        Task Response(string message);
    }
}
