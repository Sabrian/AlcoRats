using System.Threading;
using System.Threading.Tasks;

namespace AlcoRats.Services.Speech;

public interface IAudioPlayer
{
    Task PlayWavAsync(string wavPath, CancellationToken cancellationToken = default);
}
