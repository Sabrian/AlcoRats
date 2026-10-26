using AlcoRats.Bot.Models;
using LiteDB;
using System;
using System.Collections.Generic;
using System.Text;

namespace AlcoRats.Bot.Services
{
    interface IDatabase: IDisposable
    {
        void AddSubscriber(Subscriber subscriber);

        bool RemoveSubscriber(Subscriber source);

        ILiteQueryable<Subscriber> GetSubscribers();

        Subscriber GetSubscriber(long id);
    }
}
