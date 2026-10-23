using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AlcoRats.Services.Speech;

/// <summary>
/// Plays a WAV file using the best available platform mechanism from shared code.
/// Android overrides via <see cref="PlayHandler"/>.
/// </summary>
public static class PlatformAudioPlayer
{
    public static Func<string, CancellationToken, Task>? PlayHandler { get; set; }

    public static async Task PlayWavAsync(string wavPath, CancellationToken cancellationToken = default)
    {
        if (PlayHandler is not null)
        {
            await PlayHandler(wavPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await PlayWindowsAsync(wavPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Fallback: best-effort OS open (may not wait for completion).
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
            // System.Media is Windows-only; use P/Invoke winmm to avoid extra package.
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
