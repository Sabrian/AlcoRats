using AlcoRats.Bot.Models;

namespace AlcoRats.Bot.Services;

interface IDatabase : IDisposable
{
    void AddSubscriber(Subscriber subscriber);

    bool RemoveSubscriber(Subscriber source);

    IReadOnlyList<Subscriber> GetSubscribers();

    Subscriber GetSubscriber(long id);
}
