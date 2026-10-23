using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AlcoRats.Services.Speech;

/// <summary>
/// Global TTS queue. Prefer Piper when registered.
/// </summary>
public static class TtsService
{
    private static readonly Channel<string> Queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private static ITtsEngine? _engine;
    private static int _workerStarted;
    private static CancellationTokenSource? _cts;

    public static string? ActiveEngineName => _engine?.EngineName;

    public static void Configure(ITtsEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        EnsureWorker();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.InitializeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Announcements will retry on first Speak.
            }
        });
    }

    public static void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _engine is null)
            return;

        EnsureWorker();
        Queue.Writer.TryWrite(text.Trim());
    }

    public static void Shutdown()
    {
        _cts?.Cancel();
        _cts = null;
        Interlocked.Exchange(ref _workerStarted, 0);
    }

    private static void EnsureWorker()
    {
        if (Interlocked.CompareExchange(ref _workerStarted, 1, 0) != 0)
            return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => WorkerLoopAsync(token), token);
    }

    private static async Task WorkerLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var text in Queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var engine = _engine;
                if (engine is null)
                    continue;

                try
                {
                    await engine.SpeakAsync(text, token).ConfigureAwait(false);
                }
                catch
                {
                    // Keep draining the queue.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shut down
        }
    }
}
