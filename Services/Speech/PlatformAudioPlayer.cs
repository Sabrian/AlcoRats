using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlcoRats.Services.Speech;

/// <summary>
/// Default WAV player for Desktop (Windows PlaySound / shell open elsewhere).
/// </summary>
public sealed class PlatformAudioPlayer(ILogger<PlatformAudioPlayer> logger = null) : IAudioPlayer
{
    private readonly ILogger<PlatformAudioPlayer> _logger = logger ?? NullLogger<PlatformAudioPlayer>.Instance;

    public async Task PlayWavAsync(string wavPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
            throw new FileNotFoundException("WAV file not found.", wavPath);

        _logger.LogDebug("Playing WAV: {Path}", wavPath);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await PlayWindowsAsync(wavPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = wavPath,
            UseShellExecute = true
        };
        Process.Start(psi);
        await Task.Delay(800, cancellationToken).ConfigureAwait(false);
    }

    private static Task PlayWindowsAsync(string wavPath, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = PlaySound(wavPath, IntPtr.Zero, SndFilename | SndSync | SndNoDefault);
            if (result == 0 && File.Exists(wavPath))
                throw new InvalidOperationException("PlaySound failed for " + wavPath);
        }, cancellationToken);
    }

    private const uint SndSync = 0x0000;
    private const uint SndFilename = 0x00020000;
    private const uint SndNoDefault = 0x0002;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    private static extern int PlaySound(string pszSound, IntPtr hmod, uint fdwSound);
}
