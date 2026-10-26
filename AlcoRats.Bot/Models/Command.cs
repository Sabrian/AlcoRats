using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace AlcoRats.Bot.Models
{
    class Command
    {
        public CommandKind Kind { get; set; }

        public User User { get; set; }

        public string[] Arguments { get; set; }
    }

    enum CommandKind
    {
        List, Golive,
        Go = Golive
    }
}
