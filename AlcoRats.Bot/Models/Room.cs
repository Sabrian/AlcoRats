using System;
using System.Collections.Generic;
using System.Text;

namespace AlcoRats.Bot.Models
{
    class Room
    {
        public TimeSpan Online { get; set; }

        public string Name { get; set; }

        public IReadOnlyList<Member> Members { get; set; }
    }
}
