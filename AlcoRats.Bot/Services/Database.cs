using System;
using System.Collections.Generic;
using System.Linq;
using AlcoRats.Bot.Models;
using LiteDB;

namespace AlcoRats.Bot.Services;

class Database: IDatabase
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<Subscriber> _subscribers;
    public Database()
    {
        _db = new ("data.db");
        _subscribers = _db.GetCollection<Subscriber>();
    }

    public void AddSubscriber(Subscriber subscriber)
    {
        lock (_db)
        {
            _subscribers.EnsureIndex(subscriber => subscriber.Id);
            _subscribers.Insert(subscriber);
        }
    }

    public bool RemoveSubscriber(Subscriber source)
    {
        lock (_db)
        {
            return _subscribers.DeleteMany(item => item.Id == source.Id) > 0;
        }
    }

    public ILiteQueryable<Subscriber> GetSubscribers()
    {
        lock (_db)
        {
            return _subscribers.Query();
        }
    }

    public Subscriber GetSubscriber(long id)
    {
        lock (_db)
        {
            return _subscribers.FindById(id);
        }
    }

    public void Dispose()
    {
        lock (_db)
        {
            _db.Dispose();
        }
    }
}