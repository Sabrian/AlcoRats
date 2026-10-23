using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;
using SherpaOnnx;

namespace AlcoRats.Services.Speech;

/// <summary>
/// Offline Piper TTS via sherpa-onnx. Downloads the Russian voice once into app storage.
/// No Python or external apps required — only the first-run model download (~20 MB).
/// </summary>
public sealed class PiperTtsEngine : ITtsEngine, IDisposable
{
    private const string ModelArchiveName = "vits-piper-ru_RU-irina-medium-int8.tar.bz2";
    private const string ModelDownloadUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/" + ModelArchiveName;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private readonly string _rootDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineTts? _tts;
    private bool _ready;

    public PiperTtsEngine(string? cacheDirectory = null)
    {
        _rootDir = cacheDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AlcoRats",
                "piper");
    }

    public string EngineName => "Piper";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ready)
                return;

            Directory.CreateDirectory(_rootDir);
            var modelDir = await EnsureModelAsync(cancellationToken).ConfigureAwait(false);
            _tts = CreateTts(modelDir);
            _ready = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (!_ready)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string wavPath;
        try
        {
            var tts = _tts ?? throw new InvalidOperationException("Piper TTS is not initialized.");
            var audio = tts.Generate(text.Trim(), speed: 1.0f, speakerId: 0);
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
            await PlatformAudioPlayer.PlayWavAsync(wavPath, cancellationToken).ConfigureAwait(false);
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
        _gate.Dispose();
    }

    private async Task<string> EnsureModelAsync(CancellationToken cancellationToken)
    {
        var extractDir = Path.Combine(_rootDir, "vits-piper-ru_RU-irina-medium-int8");
        if (TryFindModelFiles(extractDir, out _, out _, out _))
            return extractDir;

        if (Directory.Exists(_rootDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(_rootDir))
            {
                if (TryFindModelFiles(dir, out _, out _, out _))
                    return dir;
            }
        }

        var archivePath = Path.Combine(_rootDir, ModelArchiveName);
        if (!File.Exists(archivePath) || new FileInfo(archivePath).Length < 1_000_000)
        {
            var tmp = archivePath + ".partial";
            await using (var response = await Http.GetStreamAsync(ModelDownloadUrl, cancellationToken)
                             .ConfigureAwait(false))
            await using (var file = File.Create(tmp))
            {
                await response.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(archivePath))
                File.Delete(archivePath);
            File.Move(tmp, archivePath);
        }

        Directory.CreateDirectory(extractDir);
        await Task.Run(() => ExtractTarBz2(archivePath, extractDir), cancellationToken)
            .ConfigureAwait(false);

        if (!TryFindModelFiles(extractDir, out _, out _, out _))
        {
            var nested = Directory.EnumerateDirectories(extractDir).FirstOrDefault();
            if (nested is not null && TryFindModelFiles(nested, out _, out _, out _))
                return nested;
        }

        if (!TryFindModelFiles(extractDir, out _, out _, out _))
            throw new InvalidOperationException("Piper model files were not found after download.");

        return extractDir;
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
                    NoiseScale = 0.667f,
                    NoiseScaleW = 0.8f,
                    LengthScale = 1.0f
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
