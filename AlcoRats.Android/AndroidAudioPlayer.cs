using System.Threading;
using System.Threading.Tasks;
using AlcoRats.Services.Speech;
using Android.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlcoRats.Android;

/// <summary>
/// Android <see cref="IAudioPlayer"/> implementation using MediaPlayer.
/// </summary>
public sealed class AndroidAudioPlayer(ILogger<AndroidAudioPlayer>? logger = null) : IAudioPlayer
{
    private readonly ILogger<AndroidAudioPlayer> _logger = logger ?? NullLogger<AndroidAudioPlayer>.Instance;

    public async Task PlayWavAsync(string wavPath, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Playing WAV via MediaPlayer: {Path}", wavPath);

        var player = new MediaPlayer();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.Completion += (_, _) => done.TrySetResult();
        player.Error += (_, args) =>
        {
            _logger.LogWarning("MediaPlayer error while playing {Path}: {What}/{Extra}", wavPath, args.What, args.Extra);
            done.TrySetResult();
        };

        try
        {
            await player.SetDataSourceAsync(wavPath).ConfigureAwait(false);
            player.Prepare();
            player.Start();
            await using var reg = cancellationToken.Register(() =>
            {
                try { player.Stop(); } catch { /* ignore */ }
                done.TrySetCanceled(cancellationToken);
            });
            await done.Task.ConfigureAwait(false);
        }
        finally
        {
            player.Release();
            player.Dispose();
        }
    }
}
