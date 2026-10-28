using PuppeteerSharp;

namespace AlcoRats.Bot.Jitsi;

/// <summary>
/// Captures page DOM snapshots for offline UI analysis (login, prejoin, lobby, …).
/// </summary>
interface IDomSnapshotStore
{
    Task SaveAsync(IPage page, string screenName, CancellationToken cancellationToken = default);
}
