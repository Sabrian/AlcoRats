namespace AlcoRats.Bot.Services;

/// <summary>
/// No-op AI engine — <see cref="Query"/> always returns <c>null</c>.
/// </summary>
sealed class EmptyAiChat : IAIChat
{
    public Task<string> Query(string query) => Task.FromResult<string>(null);
}
