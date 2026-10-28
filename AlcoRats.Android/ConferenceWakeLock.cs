#nullable enable
using Android.Content;
using Android.OS;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlcoRats.Android;

/// <summary>
/// Holds a <see cref="PowerManager.WakeLockFlags.Partial"/> wake lock while a conference is live
/// so Doze does not freeze the WebView/JS bridge after the screen turns off.
/// </summary>
internal static class ConferenceWakeLock
{
    private static readonly object Gate = new();
    private static PowerManager.WakeLock? _wakeLock;

    public static void Acquire(Context context, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        lock (Gate)
        {
            try
            {
                if (_wakeLock?.IsHeld == true)
                    return;

                var pm = context.ApplicationContext?.GetSystemService(Context.PowerService) as PowerManager
                         ?? context.GetSystemService(Context.PowerService) as PowerManager;
                if (pm is null)
                    return;

#pragma warning disable CA1422 // WakeLockFlags.Partial is the correct CPU keep-alive flag.
                _wakeLock = pm.NewWakeLock(WakeLockFlags.Partial, "AlcoRats:Conference");
#pragma warning restore CA1422
                _wakeLock?.SetReferenceCounted(false);
                _wakeLock?.Acquire();
                logger.LogDebug("Conference PARTIAL_WAKE_LOCK acquired");
            }
            catch (System.Exception ex)
            {
                logger.LogWarning(ex, "Failed to acquire conference wake lock");
            }
        }
    }

    public static void Release(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        lock (Gate)
        {
            try
            {
                if (_wakeLock?.IsHeld == true)
                {
                    _wakeLock.Release();
                    logger.LogDebug("Conference PARTIAL_WAKE_LOCK released");
                }
            }
            catch (System.Exception ex)
            {
                logger.LogWarning(ex, "Failed to release conference wake lock");
            }
            finally
            {
                _wakeLock = null;
            }
        }
    }
}
