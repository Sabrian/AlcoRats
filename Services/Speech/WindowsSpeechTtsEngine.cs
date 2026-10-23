using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AlcoRats.Services.Speech;

/// <summary>
/// Windows SAPI fallback when Piper is unavailable.
/// </summary>
public sealed class WindowsSpeechTtsEngine : ITtsEngine
{
    public string EngineName => "WindowsSAPI";

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("Windows SAPI is only available on Windows.");
        return Task.CompletedTask;
    }

    public Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var escaped = text.Replace("'", "''");
            var script =
                "Add-Type -AssemblyName System.Speech; " +
                "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                "try { $s.SelectVoiceByHints([System.Speech.Synthesis.VoiceGender]::Female, " +
                "[System.Speech.Synthesis.VoiceAge]::Adult, 0, [CultureInfo]'ru-RU') } catch {}; " +
                "$s.Rate = 0; $s.Speak('" + escaped + "');";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -STA -Command " + script,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var process = Process.Start(psi);
            process?.WaitForExit(60_000);
        }, cancellationToken);
    }
}
