namespace AlcoRats.Bot.Services;

interface IAIChat
{
    Task<string> Query(string query);
}
