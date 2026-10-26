using System;
using System.Collections.Generic;
using System.Linq;

namespace AlcoRats.Services.Speech;

public sealed record PiperVoiceInfo(string Id, string Title, string ModelFolder)
{
    public string ArchiveName => ModelFolder + ".tar.bz2";

    public string DownloadUrl =>
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/" + ArchiveName;
}

/// <summary>Russian Piper voices available via sherpa-onnx releases.</summary>
public static class PiperVoiceCatalog
{
    public const string DefaultVoiceId = "irina";

    public static IReadOnlyList<PiperVoiceInfo> All { get; } =
    [
        new("irina", "Ирина (жен.)", "vits-piper-ru_RU-irina-medium"),
        new("dmitri", "Дмитрий (муж.)", "vits-piper-ru_RU-dmitri-medium"),
        new("denis", "Денис (муж.)", "vits-piper-ru_RU-denis-medium"),
        new("ruslan", "Руслан (муж.)", "vits-piper-ru_RU-ruslan-medium")
    ];

    public static PiperVoiceInfo Resolve(string voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
            return All[0];

        return All.FirstOrDefault(v =>
                   string.Equals(v.Id, voiceId, StringComparison.OrdinalIgnoreCase))
               ?? All[0];
    }
}
