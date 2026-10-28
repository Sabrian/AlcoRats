using PuppeteerSharp;

namespace AlcoRats.Bot.Jitsi;

/// <summary>
/// No-op snapshot store used when <see cref="Configuration.SaveDomSnapshots"/> is disabled.
/// </summary>
sealed class NullDomSnapshotStore : IDomSnapshotStore
{
    public Task SaveAsync(IPage page, string screenName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
