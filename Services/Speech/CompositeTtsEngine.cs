using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AlcoRats.Services.Speech;

/// <summary>
/// Prefers the first working engine (Piper), then fallbacks.
/// </summary>
public sealed class CompositeTtsEngine : ITtsEngine
{
    private readonly IReadOnlyList<ITtsEngine> _engines;
    private ITtsEngine? _active;

    public CompositeTtsEngine(params ITtsEngine[] engines)
    {
        _engines = engines ?? Array.Empty<ITtsEngine>();
    }

    public string EngineName => _active?.EngineName ?? "Composite";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Exception? last = null;
        foreach (var engine in _engines)
        {
            try
            {
                await engine.InitializeAsync(cancellationToken).ConfigureAwait(false);
                _active = engine;
                return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("No TTS engine could be initialized.");
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_active is null)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

        if (_active is null)
            return;

        try
        {
            await _active.SpeakAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Try remaining engines once.
            foreach (var engine in _engines)
            {
                if (ReferenceEquals(engine, _active))
                    continue;
                try
                {
                    await engine.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    await engine.SpeakAsync(text, cancellationToken).ConfigureAwait(false);
                    _active = engine;
                    return;
                }
                catch
                {
                    // continue
                }
            }

            throw;
        }
    }
}
