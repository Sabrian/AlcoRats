using System;
using System.Threading;
using System.Threading.Tasks;
using AlcoRats.Services.Speech;
using Android.OS;
using Android.Speech.Tts;
using Java.Util;
using Application = Android.App.Application;

namespace AlcoRats.Android;

/// <summary>
/// Platform TTS used when Piper model cannot be loaded on device.
/// </summary>
public sealed class AndroidTextToSpeechEngine : Java.Lang.Object, ITtsEngine, TextToSpeech.IOnInitListener
{
    private readonly object _gate = new();
    private TextToSpeech? _tts;
    private TaskCompletionSource<bool>? _initTcs;
    private bool _ready;

    public string EngineName => "AndroidTTS";

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_ready)
                return Task.CompletedTask;

            if (_initTcs is not null)
                return _initTcs.Task;

            _initTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var context = Application.Context
                ?? throw new InvalidOperationException("Android context is not ready.");
            _tts = new TextToSpeech(context, this);
            return _initTcs.Task.WaitAsync(cancellationToken);
        }
    }

    public void OnInit(OperationResult status)
    {
        if (status != OperationResult.Success || _tts is null)
        {
            _initTcs?.TrySetException(new InvalidOperationException("Android TextToSpeech init failed."));
            return;
        }

        var locale = Locale.ForLanguageTag("ru-RU");
        var result = _tts.SetLanguage(locale);
        if (result is LanguageAvailableResult.MissingData or LanguageAvailableResult.NotSupported)
            _tts.SetLanguage(Locale.Default!);

        _tts.SetSpeechRate(1.0f);
        _ready = true;
        _initTcs?.TrySetResult(true);
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (_tts is null || string.IsNullOrWhiteSpace(text))
            return;

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var utteranceId = Guid.NewGuid().ToString("N");

        void Handler(string? id)
        {
            if (id == utteranceId)
                done.TrySetResult();
        }

        _tts.SetOnUtteranceProgressListener(new UtteranceListener(Handler));
        var paramsBundle = new Bundle();
        paramsBundle.PutString(TextToSpeech.Engine.KeyParamUtteranceId, utteranceId);

        var queueMode = QueueMode.Flush;
        var speakResult = _tts.Speak(text, queueMode, paramsBundle, utteranceId);
        if (speakResult != OperationResult.Success)
        {
            done.TrySetException(new InvalidOperationException("Speak failed."));
        }

        await using var reg = cancellationToken.Register(() =>
        {
            try { _tts.Stop(); } catch { /* ignore */ }
            done.TrySetCanceled(cancellationToken);
        });

        await done.Task.ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tts?.Stop();
            _tts?.Shutdown();
            _tts = null;
        }

        base.Dispose(disposing);
    }

    private sealed class UtteranceListener(Action<string?> onDone) : UtteranceProgressListener
    {
        public override void OnStart(string? utteranceId) { }

        public override void OnDone(string? utteranceId) => onDone(utteranceId);

#pragma warning disable CS0672, CS0618
        public override void OnError(string? utteranceId) => onDone(utteranceId);
#pragma warning restore CS0672, CS0618
    }
}
