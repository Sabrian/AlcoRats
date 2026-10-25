using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AlcoRats.Settings;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SherpaOnnx;

namespace AlcoRats.Services.Speech;

/// <summary>
/// Offline Piper TTS via sherpa-onnx. Voice is selected in settings; model downloads on first use.
/// </summary>
public sealed class PiperTtsEngine : ITtsEngine, IDisposable
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private readonly string _rootDir;
    private readonly IAudioPlayer _audioPlayer;
    private readonly AppSettings _settings;
    private readonly ILogger<PiperTtsEngine> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineTts? _tts;
    private string? _loadedVoiceId;
    private bool _ready;

    public PiperTtsEngine(
        IAudioPlayer audioPlayer,
        AppSettings settings,
        ILogger<PiperTtsEngine>? logger = null,
        string? cacheDirectory = null)
    {
        _audioPlayer = audioPlayer ?? throw new ArgumentNullException(nameof(audioPlayer));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? NullLogger<PiperTtsEngine>.Instance;
        _rootDir = cacheDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AlcoRats",
                "piper");
    }

    public string EngineName => "Piper/" + PiperVoiceCatalog.Resolve(_settings.TtsVoice).Id;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureVoiceLoadedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        await EnsureVoiceLoadedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string wavPath;
        try
        {
            var tts = _tts ?? throw new InvalidOperationException("Piper TTS is not initialized.");
            // Slightly slower than 1.0 reads clearer on phone speakers.
            var audio = tts.Generate(text.Trim(), speed: 0.92f, speakerId: 0);
            try
            {
                wavPath = Path.Combine(_rootDir, $"utt_{Guid.NewGuid():N}.wav");
                if (!audio.SaveToWaveFile(wavPath))
                    throw new InvalidOperationException("Failed to write Piper WAV.");
            }
            finally
            {
                audio.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await _audioPlayer.PlayWavAsync(wavPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(wavPath); } catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        _tts?.Dispose();
        _tts = null;
        _ready = false;
        _loadedVoiceId = null;
        _gate.Dispose();
    }

    private async Task EnsureVoiceLoadedAsync(CancellationToken cancellationToken)
    {
        var voice = PiperVoiceCatalog.Resolve(_settings.TtsVoice);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ready && string.Equals(_loadedVoiceId, voice.Id, StringComparison.OrdinalIgnoreCase))
                return;

            Directory.CreateDirectory(_rootDir);
            var modelDir = await EnsureModelAsync(voice, cancellationToken).ConfigureAwait(false);

            _tts?.Dispose();
            _tts = CreateTts(modelDir);
            _loadedVoiceId = voice.Id;
            _ready = true;
            _logger.LogInformation("Piper TTS ready: {Voice} from {ModelDir}", voice.Title, modelDir);
        }
        catch (Exception ex)
        {
            _ready = false;
            _loadedVoiceId = null;
            _logger.LogError(ex, "Piper TTS initialization failed for voice {Voice}", voice.Id);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> EnsureModelAsync(PiperVoiceInfo voice, CancellationToken cancellationToken)
    {
        var extractDir = Path.Combine(_rootDir, voice.ModelFolder);
        if (TryFindPreferredModel(extractDir, out var readyDir))
            return readyDir;

        var archivePath = Path.Combine(_rootDir, voice.ArchiveName);
        if (!File.Exists(archivePath) || new FileInfo(archivePath).Length < 1_000_000)
        {
            _logger.LogInformation("Downloading Piper model {Voice} from {Url}", voice.Id, voice.DownloadUrl);
            var tmp = archivePath + ".partial";
            await using (var response = await Http.GetStreamAsync(voice.DownloadUrl, cancellationToken)
                             .ConfigureAwait(false))
            await using (var file = File.Create(tmp))
            {
                await response.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(archivePath))
                File.Delete(archivePath);
            File.Move(tmp, archivePath);
            _logger.LogInformation("Piper model archive saved to {Path}", archivePath);
        }

        Directory.CreateDirectory(extractDir);
        await Task.Run(() => ExtractTarBz2(archivePath, extractDir), cancellationToken)
            .ConfigureAwait(false);

        if (TryFindPreferredModel(extractDir, out readyDir))
            return readyDir;

        throw new InvalidOperationException("Piper model files were not found after download: " + voice.ModelFolder);
    }

    private static bool TryFindPreferredModel(string extractDir, out string modelDir)
    {
        modelDir = "";
        if (TryFindModelFiles(extractDir, out var model, out _, out _)
            && !model.Contains("int8", StringComparison.OrdinalIgnoreCase))
        {
            modelDir = extractDir;
            return true;
        }

        if (!Directory.Exists(extractDir))
            return false;

        foreach (var nested in Directory.EnumerateDirectories(extractDir))
        {
            if (TryFindModelFiles(nested, out model, out _, out _)
                && !model.Contains("int8", StringComparison.OrdinalIgnoreCase))
            {
                modelDir = nested;
                return true;
            }
        }

        return false;
    }

    private static void ExtractTarBz2(string archivePath, string extractDir)
    {
        using var file = File.OpenRead(archivePath);
        using var bz2 = new BZip2InputStream(file);
        using var tar = TarArchive.CreateInputTarArchive(bz2, Encoding.UTF8);
        tar.ExtractContents(extractDir);
    }

    private static OfflineTts CreateTts(string modelDir)
    {
        if (!TryFindModelFiles(modelDir, out var model, out var tokens, out var dataDir))
            throw new InvalidOperationException("Incomplete Piper model directory: " + modelDir);

        var config = new OfflineTtsConfig
        {
            Model = new OfflineTtsModelConfig
            {
                NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                Provider = "cpu",
                Debug = 0,
                Vits = new OfflineTtsVitsModelConfig
                {
                    Model = model,
                    Tokens = tokens,
                    DataDir = dataDir,
                    NoiseScale = 0.45f,
                    NoiseScaleW = 0.55f,
                    LengthScale = 1.08f
                }
            },
            MaxNumSentences = 1
        };

        return new OfflineTts(config);
    }

    private static bool TryFindModelFiles(
        string root,
        out string modelPath,
        out string tokensPath,
        out string dataDir)
    {
        modelPath = "";
        tokensPath = "";
        dataDir = "";
        if (!Directory.Exists(root))
            return false;

        modelPath = Directory.EnumerateFiles(root, "*.onnx", SearchOption.AllDirectories)
            .OrderBy(p => p.Length)
            .FirstOrDefault() ?? "";
        tokensPath = Directory.EnumerateFiles(root, "tokens.txt", SearchOption.AllDirectories)
            .FirstOrDefault() ?? "";
        dataDir = Directory.EnumerateDirectories(root, "espeak-ng-data", SearchOption.AllDirectories)
            .FirstOrDefault() ?? "";

        return File.Exists(modelPath) && File.Exists(tokensPath) && Directory.Exists(dataDir);
    }
}
