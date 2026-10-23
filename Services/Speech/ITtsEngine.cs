using System.Threading;
using System.Threading.Tasks;

namespace AlcoRats.Services.Speech;

public interface ITtsEngine
{
    string EngineName { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task SpeakAsync(string text, CancellationToken cancellationToken = default);
}
