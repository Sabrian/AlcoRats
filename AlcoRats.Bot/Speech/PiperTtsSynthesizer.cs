using System.Net.Http;
using System.Text;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace AlcoRats.Bot.Speech;

/// <summary>
/// Offline Piper TTS via sherpa-onnx. Prefers a bundled <c>Speech/*.tar.bz2</c> archive (full WAV output).
/// </summary>
sealed class PiperTtsSynthesizer : IDisposable
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
    };

    private readonly Configuration _configuration;
    private readonly ILogger<PiperTtsSynthesizer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _rootDir;

    private OfflineTts _tts;
    private string _loadedVoiceId;
    private bool _disposed;

    public PiperTtsSynthesizer(Configuration configuration, ILogger<PiperTtsSynthesizer> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _rootDir = ResolveCacheDir(configuration);
    }

    public string EngineName => "Piper/" + PiperVoiceCatalog.Resolve(_configuration.PiperVoice).Id;

    /// <summary>
    /// Synthesizes <paramref name="text"/> to a temporary WAV file. Caller must delete the file.
    /// </summary>
    public async Task<string> SynthesizeToWavFileAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text must not be empty.", nameof(text));

        await EnsureVoiceLoadedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tts = _tts ?? throw new InvalidOperationException("Piper TTS is not initialized.");
            Directory.CreateDirectory(_rootDir);
            var wavPath = Path.Combine(_rootDir, $"utt_{Guid.NewGuid():N}.wav");

            var audio = tts.Generate(text.Trim(), speed: 0.92f, speakerId: 0);
            try
            {
                if (!audio.SaveToWaveFile(wavPath))
                    throw new InvalidOperationException("Failed to write Piper WAV.");
            }
            finally
            {
                audio.Dispose();
            }

            _logger.LogDebug("Piper WAV written: {Path} ({Bytes} bytes)", wavPath, new FileInfo(wavPath).Length);
            return wavPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _tts?.Dispose();
        _tts = null;
        _gate.Dispose();
    }

    private async Task EnsureVoiceLoadedAsync(CancellationToken cancellationToken)
    {
        var voice = PiperVoiceCatalog.Resolve(_configuration.PiperVoice);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tts != null && string.Equals(_loadedVoiceId, voice.Id, StringComparison.OrdinalIgnoreCase))
                return;

            Directory.CreateDirectory(_rootDir);
            var modelDir = await EnsureModelAsync(voice, cancellationToken).ConfigureAwait(false);

            _tts?.Dispose();
            _tts = CreateTts(modelDir);
            _loadedVoiceId = voice.Id;
            _logger.LogInformation(
                "Piper TTS ready: {Voice} ({Engine}) from {ModelDir} (cache={CacheDir})",
                voice.Title,
                EngineName,
                modelDir,
                _rootDir);
        }
        catch (Exception ex)
        {
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

        var archivePath = ResolveArchivePath(voice);
        if (archivePath == null)
        {
            archivePath = Path.Combine(_rootDir, voice.ArchiveName);
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
        else
        {
            _logger.LogInformation("Using bundled Piper archive: {Path}", archivePath);
        }

        Directory.CreateDirectory(extractDir);
        await Task.Run(() => ExtractTarBz2(archivePath, extractDir), cancellationToken)
            .ConfigureAwait(false);

        if (TryFindPreferredModel(extractDir, out readyDir))
            return readyDir;

        throw new InvalidOperationException("Piper model files were not found after extract: " + voice.ModelFolder);
    }

    /// <summary>
    /// Looks for <c>vits-piper-…tar.bz2</c> in cache dir, then next to the app under <c>Speech/</c>.
    /// </summary>
    private string ResolveArchivePath(PiperVoiceInfo voice)
    {
        foreach (var candidate in EnumerateArchiveCandidates(voice.ArchiveName))
        {
            if (File.Exists(candidate) && new FileInfo(candidate).Length > 1_000_000)
                return candidate;
        }

        return null;
    }

    private IEnumerable<string> EnumerateArchiveCandidates(string archiveName)
    {
        yield return Path.Combine(_rootDir, archiveName);

        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "Speech", archiveName);
        yield return Path.Combine(baseDir, archiveName);

        // Dev: running from bin/... while archive stays in project Speech/
        var projectSpeech = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Speech", archiveName));
        yield return projectSpeech;
    }

    private static string ResolveCacheDir(Configuration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.PiperCacheDir))
            return configuration.PiperCacheDir.Trim();

        var speechDir = Path.Combine(AppContext.BaseDirectory, "Speech");
        if (Directory.Exists(speechDir))
            return speechDir;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
            return Path.Combine(local, "AlcoRats.Bot", "piper");

        return Path.Combine(AppContext.BaseDirectory, "piper");
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
                    LengthScale = 1.08f,
                },
            },
            MaxNumSentences = 1,
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
