using LiteDB;

namespace AlcoRats.Bot.Models;

class Subscriber
{
    public string Name { get; set; }

    [BsonId]
    public long Id { get; set; }

    public bool All { get; set; }
}
