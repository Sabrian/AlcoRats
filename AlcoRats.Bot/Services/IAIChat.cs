namespace AlcoRats.Bot.Services;

interface IAIChat
{
    /// <summary>
    /// Returns a model reply, or <c>null</c> when the engine is disabled / empty.
    /// </summary>
    Task<string> Query(string query);
}
