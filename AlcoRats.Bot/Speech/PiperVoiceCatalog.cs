namespace AlcoRats.Bot.Speech;

sealed record PiperVoiceInfo(string Id, string Title, string ModelFolder)
{
    public string ArchiveName => ModelFolder + ".tar.bz2";

    public string DownloadUrl =>
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/" + ArchiveName;
}

/// <summary>Russian Piper voices available via sherpa-onnx releases.</summary>
static class PiperVoiceCatalog
{
    public const string DefaultVoiceId = "dmitri";

    public static IReadOnlyList<PiperVoiceInfo> All { get; } =
    [
        new("dmitri", "Дмитрий (муж.)", "vits-piper-ru_RU-dmitri-medium"),
        new("irina", "Ирина (жен.)", "vits-piper-ru_RU-irina-medium"),
        new("denis", "Денис (муж.)", "vits-piper-ru_RU-denis-medium"),
        new("ruslan", "Руслан (муж.)", "vits-piper-ru_RU-ruslan-medium"),
    ];

    public static PiperVoiceInfo Resolve(string voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
            return All.First(v => v.Id == DefaultVoiceId);

        return All.FirstOrDefault(v =>
                   string.Equals(v.Id, voiceId, StringComparison.OrdinalIgnoreCase))
               ?? All.First(v => v.Id == DefaultVoiceId);
    }
}
