using AlcoRats.Bot.Models;
using LiteDB;

namespace AlcoRats.Bot.Services;

class Database : IDatabase
{
    private readonly object _sync = new();
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<Subscriber> _subscribers;

    public Database()
    {
        _db = new LiteDatabase("data.db");
        _subscribers = _db.GetCollection<Subscriber>();
        _subscribers.EnsureIndex(x => x.Id, unique: true);
    }

    public void AddSubscriber(Subscriber subscriber)
    {
        lock (_sync)
        {
            _subscribers.Upsert(subscriber);
        }
    }

    public bool RemoveSubscriber(Subscriber source)
    {
        lock (_sync)
        {
            return _subscribers.DeleteMany(x => x.Id == source.Id) > 0;
        }
    }

    public IReadOnlyList<Subscriber> GetSubscribers()
    {
        lock (_sync)
        {
            // Materialize under lock — never return a live LiteDB cursor/query.
            return _subscribers.FindAll().ToList();
        }
    }

    public Subscriber GetSubscriber(long id)
    {
        lock (_sync)
        {
            return _subscribers.FindOne(x => x.Id == id);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _db.Dispose();
        }
    }
}
