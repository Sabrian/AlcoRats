using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using Telegram.Bot.Types;

namespace AlcoRats.Bot.Models
{
    interface ICommand: IClient
    {
        public int Id { get; }

        public long ChatId { get; }

        public string Owner { get; }

        public CommandKind Kind { get; }

        public ProviderUser User { get; }

        public string[] Arguments { get; }

        bool IsGroup
        {
            get
            {
                return ChatId != User.Id;

            }
        }

        public string UserName
        {
            get
            {
                if(User.FirstName is { } first || User.LastName is { } last)
                {
                    return $"{User.FirstName} {User.LastName}".Trim();
                }

                return string.IsNullOrEmpty(User.UserName) ? Owner : User.UserName;
            }
        }
    }

    enum CommandKind
    {
        List, Golive,
        Go = Golive,
        App,
        Subscribe,
        Unsubscribe,
        Message,
        Help
    }
}
