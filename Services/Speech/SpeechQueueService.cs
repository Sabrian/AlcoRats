using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AlcoRats.Services.Speech;

/// <summary>
/// Queued TTS playback. Prefer Piper when registered as <see cref="ITtsEngine"/>.
/// Engine initialization is deferred until the first speak / explicit warm-up.
/// </summary>
public sealed class SpeechQueueService : IDisposable
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly ITtsEngine _engine;
    private readonly ILogger<SpeechQueueService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private int _workerStarted;
    private bool _engineReady;

    public SpeechQueueService(ITtsEngine engine, ILogger<SpeechQueueService> logger)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _logger = logger;
    }

    public string? ActiveEngineName => _engine.EngineName;

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        EnsureWorker();
        if (!_queue.Writer.TryWrite(text.Trim()))
            _logger.LogWarning("Failed to enqueue TTS utterance");
        else
            _logger.LogDebug("TTS enqueued: {Text}", text);
    }

    public void Shutdown()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
    }

    public void Dispose()
    {
        Shutdown();
        _cts.Dispose();
        _initGate.Dispose();
    }

    private void EnsureWorker()
    {
        if (Interlocked.CompareExchange(ref _workerStarted, 1, 0) != 0)
            return;

        var token = _cts.Token;
        _ = Task.Run(() => WorkerLoopAsync(token), token);
    }

    private async Task EnsureEngineAsync(CancellationToken token)
    {
        if (_engineReady)
            return;

        await _initGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_engineReady)
                return;

            await _engine.InitializeAsync(token).ConfigureAwait(false);
            _engineReady = true;
            _logger.LogInformation("TTS engine ready: {Engine}", _engine.EngineName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TTS engine initialization failed");
            throw;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task WorkerLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var text in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await EnsureEngineAsync(token).ConfigureAwait(false);
                    await _engine.SpeakAsync(text, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "TTS speak failed for text: {Text}", text);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shut down
        }
    }
}
